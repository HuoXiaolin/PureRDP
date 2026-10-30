using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using PureRDP.UI;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int _checks;
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static IntPtr Packed(Point point) => (IntPtr)unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16)));

    private static object Field(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args)!;
    private static void Check(bool value, string message)
    {
        if (!value)
        {
            Directory.CreateDirectory(Path.Combine("outputs", "ci-tests"));
            File.WriteAllText(Path.Combine("outputs", "ci-tests", "window-assertion-failure.txt"), message);
            throw new InvalidOperationException(message);
        }
        _checks++;
    }
    private static void Pump()
    {
        for (var index = 0; index < 12; index++) { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static FormRdpClient Window(FormRdpClient? manager)
    {
        var type = typeof(FormRdpClient);
        var window = (FormRdpClient)Activator.CreateInstance(type, Members, null,
            new object?[] { Array.Empty<string>(), true, manager }, null)!;
        // Exercise real HWND transitions without login, user settings writes,
        // tray startup, shortcut registration or any remote network session.
        var shown = type.GetMethod("FormRdpClient_Shown", Members)!;
        window.Shown -= (EventHandler)shown.CreateDelegate(typeof(EventHandler), window);
        var closing = type.GetMethod("FormRdpClient_FormClosing", Members)!;
        window.FormClosing -= (FormClosingEventHandler)closing.CreateDelegate(typeof(FormClosingEventHandler), window);
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(SystemInformation.VirtualScreen.Right + 4096, 100);
        window.Size = new Size(800, 600);
        window.ShowInTaskbar = false;
        return window;
    }

    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var failureFile = Path.Combine("outputs", "ci-tests", "window-assertion-failure.txt");
        if (File.Exists(failureFile)) File.Delete(failureFile);
        TestSilentStartupVisibility();
        TestPreLoginViewport();
        TestRoutingPacket();
        using var manager = Window(null);
        _ = manager.Handle;
        using var first = Window(manager);
        using var second = Window(manager);
        ((IList)Field(manager, "_sessionWindows")).Add(first);
        ((IList)Field(manager, "_sessionWindows")).Add(second);
        first.Show(); second.Show(); Pump();
        Call(first, "SetRemoteConnectionChrome", true);
        Call(second, "SetRemoteConnectionChrome", true);
        // Exercise the corner overlay's native mouse-down entry, not only
        // the innermost proportional-size calculation.
        var grips = (Array)Field(first, "_cornerGrips");
        Check(grips.Length == 2, "Only bottom-left and bottom-right resize controls may exist.");
        var bottomRight = grips.GetValue(1)!;
        // Unlike sending WM_LBUTTONDOWN directly, native point discovery
        // proves that Windows can actually route a physical corner click.
        var hitTestBounds = first.Bounds;
        second.Hide();
        first.Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(40, 40), new Size(640, 480));
        first.Activate();
        Call(first, "PositionWindowChrome");
        Pump();
        var visibleGrip = (Control)bottomRight;
        Check(first.Region != null && !first.Region.IsVisible(0, 0) && first.Region.IsVisible(16, 1),
            "Connected windows must have a default 16-pixel rounded clipping region.");
        var originalRoundedRegion = first.Region;
        Call(first, "PositionWindowChrome");
        Check(ReferenceEquals(first.Region, originalRoundedRegion), "Unchanged geometry must reuse its region without repeated GDI allocations.");
        typeof(FormRdpClient).GetField("_roundedConnectionCorners", Members)!.SetValue(first, false);
        Call(first, "PositionWindowChrome");
        Check(first.Region == null, "Turning rounded corners off must restore rectangular geometry.");
        typeof(FormRdpClient).GetField("_roundedConnectionCorners", Members)!.SetValue(first, true);
        Call(first, "PositionWindowChrome");
        Check(visibleGrip.Size == new Size(5, 5), "Resize corner must be approximately one third of its previous dimensions.");
        using (var cornerSurface = (Bitmap)Call(visibleGrip, "CreateSurfaceBitmap"))
            Check(cornerSurface.GetPixel(0, 0).A is > 0 and <= 32, "Resize corner must be nearly transparent but retain native input.");
        var nativePoint = visibleGrip.PointToScreen(new Point(2, 2));
        var nativeTarget = WindowFromPoint(nativePoint);
        Console.WriteLine($"Corner routing: grip={visibleGrip.Handle} target={nativeTarget} targetControl={Control.FromHandle(nativeTarget)?.GetType().Name} visible={visibleGrip.Visible} enabled={visibleGrip.Enabled} composition={visibleGrip.GetType().GetProperty("LastCompositionError", Members)!.GetValue(visibleGrip)} bounds={visibleGrip.Bounds}");
        Check(nativeTarget == visibleGrip.Handle, "A real bottom-right click must hit the resize grip, not the RDP surface or another window.");
        Check(first.Region != null && !first.Region.IsVisible(first.Width - 2, first.Height - 2),
            "The corner overlay must remain hittable even outside the clipped host region.");
        foreach (Control corner in grips)
            Check(WindowFromPoint(corner.PointToScreen(new Point(2, 2))) == corner.Handle,
                "Both bottom corner overlays must remain real native hit targets with rounded host corners.");
        foreach (var localPoint in new[] { new Point(1, 1), new Point(3, 3) })
            Check(WindowFromPoint(visibleGrip.PointToScreen(localPoint)) == visibleGrip.Handle,
                "Resize target must remain hittable away from its center.");
        var routedBefore = first.Bounds;
        SendMessage(nativeTarget, 0x0201, (IntPtr)1, Packed(new Point(2, 2)));
        var routedMove = first.PointToClient(new Point(nativePoint.X + 80, nativePoint.Y + 60));
        SendMessage(first.Handle, 0x0200, (IntPtr)1, Packed(routedMove));
        SendMessage(first.Handle, 0x0202, IntPtr.Zero, Packed(routedMove));
        Check(first.Width > routedBefore.Width && GetCapture() != first.Handle,
            "Point-discovered corner must drive a complete resize and release capture.");
        TestFreeResizeAndMemory(first, (Control)grips.GetValue(0)!, visibleGrip);
        first.Bounds = hitTestBounds;
        second.Show();
        Pump();
        TestCollapsedRail();
        var resizeBefore = first.Bounds;
        var gripDownPoint = ((Control)bottomRight).PointToScreen(new Point(2, 2));
        Call(bottomRight, "WndProc", Message.Create(((Control)bottomRight).Handle, 0x0201,
            (IntPtr)1, Packed(new Point(2, 2))));
        Check((int)Field(first, "_pendingResizeHitTest") == 17,
            "Bottom-right overlay mouse-down must arm resizing.");
        var origin = (Point)Field(first, "_manualResizeStartCursor");
        Check(origin == gripDownPoint,
            "Resize origin must use the grip mouse message, not a stale global pointer.");
        var resizePoint = first.PointToClient(new Point(origin.X + 100, origin.Y + 75));
        SendMessage(first.Handle, 0x0200, (IntPtr)1, Packed(resizePoint));
        Check(first.Width > resizeBefore.Width && first.Height > resizeBefore.Height,
            "Captured native mouse-move must grow the host without sampling the global pointer.");
        Check(GetCapture() == first.Handle, "Host must retain capture during the resize.");
        SendMessage(first.Handle, 0x0202, IntPtr.Zero, Packed(resizePoint));
        Check((int)Field(first, "_pendingResizeHitTest") == 1 && (int)Field(first, "_manualResizeHitTest") == 1 &&
            GetCapture() != first.Handle && !(bool)Field(Field(first, "_rdpService"), "_interactiveResize"),
            "Native mouse-up must end pending/active resize, release capture and resume display updates.");
        var gripControl = (Control)bottomRight;
        Check(SendMessage(gripControl.Handle, 0x0084, IntPtr.Zero, Packed(gripControl.PointToScreen(new Point(2, 2)))) == (IntPtr)1,
            "Corner must remain client input, not enter a competing system sizing loop.");
        Check(SendMessage(first.Handle, 0x0084, IntPtr.Zero,
            Packed(first.PointToScreen(new Point(first.ClientSize.Width - 2, first.ClientSize.Height - 2)))) == (IntPtr)1,
            "Borderless host must leave resizing to the custom message path.");
        // A release dispatched to a nested ActiveX HWND must finish the same
        // gesture before the edge hit-test/padding early returns can swallow it.
        var filterType = typeof(FormRdpClient).Assembly.GetType("PureRDP.UI.Services.RemoteResizeInputFilter")!;
        using (var filter = (IDisposable)Activator.CreateInstance(filterType, Members, null, new object[] { first }, null)!)
        {
            var resizeCanvas = (Control)Field(first, "panelRdp");
            SendMessage(gripControl.Handle, 0x0201, (IntPtr)1, Packed(new Point(2, 2)));
            var downOrigin = (Point)Field(first, "_manualResizeStartCursor");
            var queuedMove = Message.Create(resizeCanvas.Handle, 0x0200, (IntPtr)1,
                Packed(resizeCanvas.PointToClient(new Point(downOrigin.X + 130, downOrigin.Y + 90))));
            Check((bool)Call(filter, "PreFilterMessage", queuedMove), "Child mouse-move must use the active gesture before edge filtering.");
            var queuedUp = Message.Create(resizeCanvas.Handle, 0x0202, IntPtr.Zero, queuedMove.LParam);
            Check((bool)Call(filter, "PreFilterMessage", queuedUp) && GetCapture() != first.Handle,
                "Child mouse-up must be consumed and release host capture.");
        }
        first.Bounds = resizeBefore;
        var moveOrigin = first.PointToScreen(new Point(120, 100));
        Check((bool)Call(first, "BeginRemoteWindowMove", moveOrigin), "Windowed host must allow movement.");
        Call(first, "UpdateRemoteWindowMove", new Point(moveOrigin.X + 80, moveOrigin.Y + 45));
        Check(first.Location == new Point(resizeBefore.X + 80, resizeBefore.Y + 45) && first.Size == resizeBefore.Size,
            "Moving must preserve the normal window size.");
        Call(first, "EndRemoteWindowMove");
        Check(!(bool)Field(first, "_windowMoveActive") && !(bool)Field(first, "_windowMovePending"),
            "Move completion must clear all capture state.");
        first.Bounds = resizeBefore;
        var strip = (Control)Field(first, "_glassChromeStrip");
        Call(strip, "Expand");
        var padding = new Point(1, strip.ClientSize.Height / 2);
        Check(strip.Region!.IsVisible(padding), "Padding must not expose a nested native edge.");
        Check(SendMessage(strip.Handle, 0x0084, IntPtr.Zero, Packed(strip.PointToScreen(padding))) == (IntPtr)1,
            "Padding must be inert client input, not HTTRANSPARENT or caption input.");
        foreach (var mouseMessage in new[] { 0x0201, 0x0202, 0x0203, 0x0202, 0x0204, 0x0205 })
            SendMessage(strip.Handle, mouseMessage, IntPtr.Zero, Packed(padding));
        Check(!strip.Capture && GetCapture() == IntPtr.Zero &&
            !(bool)Field(first, "_windowMovePending") && (int)Field(first, "_pendingResizeHitTest") == 1,
            "Blank clicks/double clicks must not capture or arm a gesture.");
        SendMessage(gripControl.Handle, 0x0201, (IntPtr)1, Packed(new Point(2, 2)));
        SendMessage(first.Handle, 0x001F, IntPtr.Zero, IntPtr.Zero);
        Check(GetCapture() != first.Handle && (int)Field(first, "_pendingResizeHitTest") == 1,
            "Cancel mode must safely end a corner click without movement.");
        var dragArea = (Rectangle)strip.GetType().GetProperty("DragBounds", Members)!.GetValue(strip)!;
        var dragPoint = new Point(dragArea.Left + 2, dragArea.Top + 2);
        Check((bool)Call(strip, "IsInteractivePoint", strip.PointToScreen(dragPoint)), "Drag handle must be hit-testable.");
        Check((int)Call(strip, "ButtonAt", dragPoint) == -1, "Drag handle must not overlap a control button.");
        Call(strip, "WndProc", Message.Create(strip.Handle, 0x0201, (IntPtr)1,
            (IntPtr)((dragPoint.Y << 16) | dragPoint.X)));
        Check((bool)Field(first, "_windowMovePending"), "Control-bar drag handle must arm only movement.");
        Call(first, "EndRemoteWindowMove");
        Call(strip, "WndProc", Message.Create(strip.Handle, 0x0203, (IntPtr)1,
            (IntPtr)((dragPoint.Y << 16) | dragPoint.X)));
        Check(!(bool)Field(first, "_windowMovePending") && first.Bounds == resizeBefore,
            "Double-clicking the drag handle must not maximize, restore or enter fullscreen.");
        var moveCanvas = (Control)Field(first, "panelRdp");
        var input = Message.Create(moveCanvas.Handle, 0x0201, (IntPtr)1, (IntPtr)((100 << 16) | 120));
        Check(!(bool)Call(first, "TryHandleWindowMoveInput", input), "Remote clicks must not initiate movement with any modifiers.");
        Check(!(bool)Field(first, "_windowMovePending"), "Only the nine-dot overlay may initiate movement.");
        Call(first, "EndRemoteWindowMove");
        first.WindowState = FormWindowState.Maximized; Pump();
        moveOrigin = first.PointToScreen(new Point(200, 40));
        Call(first, "BeginRemoteWindowMove", moveOrigin);
        Call(first, "UpdateRemoteWindowMove", new Point(moveOrigin.X + 70, moveOrigin.Y + 50));
        Check(first.WindowState == FormWindowState.Normal, "Dragging a maximized window must restore it.");
        var restoredMoveBounds = first.Bounds;
        Call(first, "UpdateRemoteWindowMove", new Point(moveOrigin.X + 90, moveOrigin.Y + 65));
        Check(first.Location == new Point(restoredMoveBounds.X + 20, restoredMoveBounds.Y + 15),
            "Movement must continue smoothly after restoring a maximized window.");
        Call(first, "EndRemoteWindowMove");
        first.Bounds = resizeBefore;
        Call(first, "BeginRemoteWindowMove", first.PointToScreen(new Point(100, 60)));
        Check((bool)Call(first, "ProcessCmdKey", new Message(), Keys.Escape), "Escape must cancel an active move.");
        Check(!(bool)Field(first, "_windowMovePending"), "Escape must leave no pending move.");
        for (var buttonIndex = 0; buttonIndex < 4; buttonIndex++)
        {
            var buttonArea = (Rectangle)Call(strip, "ButtonBounds", buttonIndex);
            Check(!buttonArea.IntersectsWith(dragArea), "Move handle must not overlap any existing toolbar button.");
        }
        typeof(FormRdpClient).GetField("_autoTileSessions", Members)!.SetValue(manager, true);
        Call(manager, "ArrangeSessions"); Pump();
        var gridFirst = first.Bounds;
        var gridSecond = second.Bounds;
        moveOrigin = first.PointToScreen(new Point(100, 60));
        Call(first, "BeginRemoteWindowMove", moveOrigin);
        Call(first, "UpdateRemoteWindowMove", new Point(moveOrigin.X + 30, moveOrigin.Y + 25));
        Call(first, "EndRemoteWindowMove"); Pump();
        Check(first.Location == new Point(gridFirst.X + 30, gridFirst.Y + 25) && second.Bounds == gridSecond,
            "Grid dragging must move only the selected session, never its siblings.");
        typeof(FormRdpClient).GetField("_autoTileSessions", Members)!.SetValue(manager, false);
        Call(manager, "ArrangeSessions");
        var moveService = Field(first, "_rdpService");
        Call(moveService, "SetFullScreen", true, true); Pump();
        var fullscreenBoundsForMove = first.Bounds;
        Check(!(bool)Call(first, "BeginRemoteWindowMove", first.Location), "True fullscreen must reject movement.");
        Check(!(bool)Call(first, "TryHandleWindowMoveInput", input),
            "True fullscreen mouse input must not be consumed.");
        Call(first, "UpdateRemoteWindowMove", new Point(first.Left + 120, first.Top + 60));
        Check(first.Bounds == fullscreenBoundsForMove && (bool)moveService.GetType().GetProperty("FullScreen")!.GetValue(moveService)!,
            "Move attempts must not alter fullscreen size or state.");
        Call(moveService, "SetFullScreen", false, true);
        first.Bounds = resizeBefore;
        // A completion callback must not unhide a hidden connection window or
        // canvas if there was no matching redraw suspension in this transition.
        second.Hide();
        Call(manager, "EndBulkWindowStateTransition"); Pump();
        Check(!IsWindowVisible(second.Handle), "Unpaired redraw resume unexpectedly makes a hidden connection window visible.");
        second.Show(); Pump();
        // The shell can restore a connection without going through our toggle.
        // The remembered "all minimized" flag then disagrees with the HWNDs.
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        first.WindowState = FormWindowState.Normal;
        second.WindowState = FormWindowState.Normal; Pump();
        Check(!(bool)Field(Field(first, "_rdpService"), "_displayUpdatesSuspended") &&
            !(bool)Field(Field(second, "_rdpService"), "_displayUpdatesSuspended"),
            "Taskbar-restored hosts must release the group display suspension before fullscreen changes.");
        Check((bool)Field(manager, "_allSessionsMinimized"), "Repro requires the stale group flag after external restore.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(IsIconic(first.Handle) && IsIconic(second.Handle), "First command after taskbar restore must minimize, not redundantly restore.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(!IsIconic(first.Handle) && !IsIconic(second.Handle), "Next command restores the externally restored pair.");

        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        first.WindowState = FormWindowState.Maximized; Pump();
        Check(IsIconic(second.Handle), "Partial external restore must leave the other connection minimized before the command.");
        Check(!(bool)Field(Field(first, "_rdpService"), "_displayUpdatesSuspended"), "Externally restored host must resume display updates.");
        Check((bool)Field(Field(second, "_rdpService"), "_displayUpdatesSuspended"), "Sibling still minimized must retain display suspension.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(IsIconic(first.Handle) && IsIconic(second.Handle), "Partial taskbar restore must also minimize all on the first command.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(first.WindowState == FormWindowState.Maximized && second.WindowState == FormWindowState.Normal,
            "Restoring after partial external restore must preserve both visible window states.");
        first.WindowState = FormWindowState.Normal; Pump();
        Call(manager, "ToggleAllSessionMinimize", false);
        first.WindowState = FormWindowState.Normal;
        Check((bool)Field(Field(first, "_rdpService"), "_displayUpdatesSuspended"), "Bulk transition must not resume display prematurely.");
        Pump();
        Check(!(bool)Field(Field(first, "_rdpService"), "_displayUpdatesSuspended"), "A restore before the bulk callback drains must resume after the transaction completes.");
        Check((bool)Field(Field(second, "_rdpService"), "_displayUpdatesSuspended"), "Bulk completion must leave the minimized sibling paused.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        var service = Field(first, "_rdpService");
        var normalBounds = first.Bounds;
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        first.WindowState = FormWindowState.Normal; Pump();
        Call(service, "SetFullScreen", true, true); Pump();
        Check(!(bool)Field(service, "_displayUpdatesSuspended"), "True fullscreen after taskbar restore must not inherit minimize suspension.");
        Check(first.Bounds == Screen.FromControl(first).Bounds, "True fullscreen host must cover its entire monitor.");
        Check(!(bool)Field(first, "_fullScreenTransitionInProgress"), "Fullscreen transaction must finish.");
        Call(service, "SetFullScreen", false, true); Pump();
        Check(first.Bounds == normalBounds, "Leaving true fullscreen must retain the original normal bounds.");
        Check(!(bool)Field(service, "_displayUpdatesSuspended"), "Leaving true fullscreen must not leave display updates paused.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        // Exercise the exact working-area maximize -> monitor-bounds fullscreen
        // transition, not just entering fullscreen from an ordinary window.
        for (var transition = 0; transition < 10; transition++)
        {
            first.WindowState = FormWindowState.Maximized; Pump();
            Call(service, "SetFullScreen", true, true); Pump();
            Check(first.WindowState == FormWindowState.Normal && first.Bounds == Screen.FromControl(first).Bounds,
                "Maximized-to-fullscreen must use monitor bounds rather than the taskbar working area.");
            Check(((Panel)Field(first, "panelRdp")).ClientSize == first.ClientSize && !(bool)Field(service, "_displayUpdatesSuspended"),
                "Fullscreen canvas must cover the host with layout updates enabled.");
            Call(service, "SetFullScreen", false, true); Pump();
            Check(first.WindowState == FormWindowState.Maximized && IsZoomed(first.Handle),
                "Fullscreen exit must restore the previous working-area maximized state.");
        }
        first.WindowState = FormWindowState.Normal; Pump();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            Call(manager, "ToggleAllSessionMinimize", false); Pump();
            Check(first.WindowState == FormWindowState.Minimized && IsIconic(first.Handle), "First connection did not minimize.");
            Check(second.WindowState == FormWindowState.Minimized && IsIconic(second.Handle), "Second connection did not minimize.");
            Call(manager, "ToggleAllSessionMinimize", false); Pump();
            Check(first.WindowState == FormWindowState.Normal && !IsIconic(first.Handle), "First connection did not restore its normal window state.");
            Check(second.WindowState == FormWindowState.Normal && !IsIconic(second.Handle), "Second connection did not restore its normal window state.");
        }
        first.WindowState = FormWindowState.Maximized; Pump();
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(IsIconic(first.Handle) && IsIconic(second.Handle), "Mixed maximized/normal windows did not minimize together.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(first.WindowState == FormWindowState.Maximized && IsZoomed(first.Handle), "Maximized window did not retain its state.");
        Check(second.WindowState == FormWindowState.Normal && !IsZoomed(second.Handle), "Normal window was incorrectly maximized.");
        first.WindowState = FormWindowState.Minimized;
        second.WindowState = FormWindowState.Minimized; Pump();
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(first.WindowState == FormWindowState.Maximized && IsZoomed(first.Handle), "Taskbar-minimized window did not restore maximized.");
        Check(second.WindowState == FormWindowState.Normal && !IsIconic(second.Handle), "Taskbar-minimized window did not restore normal.");
        first.WindowState = FormWindowState.Normal;
        var fullscreenService = Field(first, "_rdpService");
        typeof(FormRdpClient).GetField("_nonFullScreenBounds", Members)!.SetValue(first, first.Bounds);
        typeof(FormRdpClient).GetField("_nonFullScreenState", Members)!.SetValue(first, FormWindowState.Normal);
        fullscreenService.GetType().GetField("_fullScreen", Members)!.SetValue(fullscreenService, true);
        first.Bounds = Screen.PrimaryScreen!.Bounds;
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        first.WindowState = FormWindowState.Normal; Pump();
        Call(manager, "ToggleAllSessionMinimize", true); Pump();
        Check(IsIconic(first.Handle) && IsIconic(second.Handle), "Queued first command must minimize an externally restored fullscreen host.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(first.Bounds == Screen.PrimaryScreen.Bounds &&
            (bool)fullscreenService.GetType().GetProperty("FullScreen")!.GetValue(fullscreenService)!,
            "Fullscreen snapshot must remain fullscreen after resynchronized group minimize/restore.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(IsIconic(first.Handle), "Borderless fullscreen window did not minimize.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(!IsIconic(first.Handle) && first.Bounds == Screen.PrimaryScreen.Bounds &&
            (bool)fullscreenService.GetType().GetProperty("FullScreen")!.GetValue(fullscreenService)!,
            "Fullscreen window did not restore its original state.");
        for (var iteration = 0; iteration < 3; iteration++)
        {
            Call(manager, "ToggleAllSessionMinimize", false);
            Call(manager, "ToggleAllSessionMinimize", false); Pump();
            Check(!IsIconic(first.Handle) && !IsIconic(second.Handle), "Rapid minimize/restore left a window minimized.");
        }
        var canvas = (Panel)Field(second, "panelRdp");
        canvas.Hide();
        var canvasHandle = canvas.Handle;
        var canvasService = Field(second, "_rdpService");
        Call(canvasService, "SetNativeRedrawEnabled", true);
        Check(!IsWindowVisible(canvasHandle), "Unpaired redraw resume showed a hidden remote canvas.");
        Call(canvasService, "SetNativeRedrawEnabled", false);
        Call(canvasService, "SetNativeRedrawEnabled", true);
        Check(!IsWindowVisible(canvasHandle), "Redraw suspension showed a canvas which was hidden before the operation.");
        // Paired canvas redraw suppression must not hide the top-level HWND.
        Call(first, "SetRestoreRedraw", false);
        Check(IsWindowVisible(first.Handle), "Restore redraw suspension removed visibility from the top-level window.");
        Call(first, "SetRestoreRedraw", true);
        Console.WriteLine($"PASS: {_checks} minimize/restore checks; real host HWNDs, no RDP login.");
    }

    private static void TestRoutingPacket()
    {
        // Capture only the first X.224 request on loopback. No server handshake,
        // credentials, remote login or user-provided ticket is involved.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var captured = System.Threading.Tasks.Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            var length = (header[2] << 8) | header[3];
            if (length < 4 || length > 4096) throw new Exception("Invalid loopback TPKT length.");
            var packet = new byte[length];
            header.CopyTo(packet, 0);
            await stream.ReadExactlyAsync(packet.AsMemory(4), timeout.Token);
            // End this transport-only fixture without a server negotiation.
            return packet;
        });
        using var window = Window(null);
        window.Show();
        var service = Field(window, "_rdpService");
        var profile = Field(window, "_appSettings").GetType().GetProperty("Connection")!.GetValue(Field(window, "_appSettings"))!;
        profile.GetType().GetProperty("LoadBalanceInfo")!.SetValue(profile, "Cookie: msts=purerdp-wire-fixture");
        service.GetType().GetField("_connectionSettings", Members)!.SetValue(service, profile);
        Call(service, "CreateRdpControlAndClient");
        try
        {
            var native = (MSTSCLib.MsRdpClient10NotSafeForScripting)Field(service, "_rdpClient");
            native.Server = "127.0.0.1";
            native.AdvancedSettings9.RDPPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            native.AdvancedSettings9.EnableAutoReconnect = false;
            Call(service, "ApplyRoutingSettings");
            native.Connect();
            while (!captured.IsCompleted && !timeout.IsCancellationRequested) { Application.DoEvents(); Thread.Sleep(10); }
            var bytes = captured.GetAwaiter().GetResult();
            var expected = System.Text.Encoding.ASCII.GetBytes("Cookie: msts=purerdp-wire-fixture \r\n");
            Check(bytes.AsSpan(11).StartsWith(expected), "X.224 must contain exact ASCII routing cookie bytes and CRLF, without UTF-16 nulls.");
            Console.WriteLine("PASS: synthetic routing cookie verified in loopback X.224 packet.");
            if (native.Connected != 0) native.Disconnect();
        }
        finally { Call(service, "RemoveRdpControlAndClient"); }
    }

    private static void TestPreLoginViewport()
    {
        Console.WriteLine("Pre-login regression: creating isolated host.");
        using var window = Window(null);
        window.Show();
        Call(window, "SetRemoteConnectionChrome", true);
        window.ClientSize = new Size(800, 600);
        var service = Field(window, "_rdpService");
        var profile = Field(window, "_appSettings").GetType().GetProperty("Connection")!.GetValue(Field(window, "_appSettings"))!;
        profile.GetType().GetProperty("AutoResize")!.SetValue(profile, true);
        profile.GetType().GetProperty("ViewScaleFactor")!.SetValue(profile, (uint?)0);
        profile.GetType().GetProperty("Password")!.SetValue(profile, null);
        profile.GetType().GetProperty("Width")!.SetValue(profile, 1600);
        profile.GetType().GetProperty("Height")!.SetValue(profile, 900);
        profile.GetType().GetProperty("PreserveImportedDesktop")!.SetValue(profile, true);
        Check((Size)Call(service, "GetInitialDesktopSize", profile) == new Size(1600, 900),
            "Broker compatibility must honor the imported initial resolution before any desktop negotiation.");
        profile.GetType().GetProperty("PreserveImportedDesktop")!.SetValue(profile, null);
        service.GetType().GetField("_connectionSettings", Members)!.SetValue(service, profile);
        Check((Size)Call(service, "GetInitialDesktopSize", profile) == new Size(800, 600),
            "Dynamic pre-login sizing must use the window even when the profile contains an older desktop resolution.");
        profile.GetType().GetProperty("AutoResize")!.SetValue(profile, false);
        Check((Size)Call(service, "GetInitialDesktopSize", profile) == new Size(1600, 900), "Fixed-resolution profiles must retain their initial desktop size.");
        profile.GetType().GetProperty("AutoResize")!.SetValue(profile, true);
        profile.GetType().GetProperty("ViewScaleFactor")!.SetValue(profile, (uint?)150);
        Check((Size)Call(service, "GetInitialDesktopSize", profile) == new Size(1600, 900), "Fixed picture zoom must retain its remote desktop baseline.");
        profile.GetType().GetProperty("ViewScaleFactor")!.SetValue(profile, (uint?)0);
        foreach (var mode in new[] { "_tileMode", "_previewMode" })
        {
            service.GetType().GetField(mode, Members)!.SetValue(service, true);
            Check((Size)Call(service, "GetInitialDesktopSize", profile) == new Size(1600, 900), "Tiles and previews must retain their configured remote dimensions.");
            service.GetType().GetField(mode, Members)!.SetValue(service, false);
        }
        // Instantiate the real ActiveX without ever calling Connect: this
        // tests the pre-login HWND and scaling properties without credentials,
        // a remote session, native authentication popups or network traffic.
        Console.WriteLine("Pre-login regression: creating disconnected ActiveX.");
        Call(service, "CreateRdpControlAndClient");
        try
        {
        Console.WriteLine("Pre-login regression: configuring local desktop.");
        var client = (MSTSCLib.MsRdpClient10NotSafeForScripting)Field(service, "_rdpClient");
        profile.GetType().GetProperty("AlternateShell")!.SetValue(profile, "fixture-remote-startup --ticket=example:token");
        profile.GetType().GetProperty("ShellWorkingDirectory")!.SetValue(profile, "C:\\fixture remote");
        Call(service, "ApplyRemoteStartupSettings");
        Check(client.Connected == 0 && client.SecuredSettings3.StartProgram == "fixture-remote-startup --ticket=example:token" &&
            client.SecuredSettings3.WorkDir == "C:\\fixture remote",
            "Disconnected native ActiveX must receive the remote startup command and working directory without local execution or login.");
        profile.GetType().GetProperty("AlternateShell")!.SetValue(profile, null);
        profile.GetType().GetProperty("ShellWorkingDirectory")!.SetValue(profile, null);
        Call(service, "ApplyRemoteStartupSettings");
        Check(string.IsNullOrEmpty(client.SecuredSettings3.StartProgram) && string.IsNullOrEmpty(client.SecuredSettings3.WorkDir),
            "Reusing a native control for an ordinary session must clear the previous startup payload.");
        var syntheticClear = System.Text.Encoding.Unicode.GetBytes("fixture-native-import-password");
        var syntheticEncrypted = System.Security.Cryptography.ProtectedData.Protect(syntheticClear, null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        try
        {
            profile.GetType().GetProperty("EncryptedPassword51")!.SetValue(profile, Convert.ToHexString(syntheticEncrypted));
            Call(service, "ApplyConnectionPassword");
            Check(client.Connected == 0 && profile.GetType().GetProperty("Password")!.GetValue(profile) == null,
                "Disconnected ActiveX must accept the imported password without a login or plaintext profile mutation.");
            ((MSTSCLib.IMsTscNonScriptable)client).ResetPassword();
        }
        finally
        {
            profile.GetType().GetProperty("EncryptedPassword51")!.SetValue(profile, null);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(syntheticClear);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(syntheticEncrypted);
        }
        var routingProperty = profile.GetType().GetProperty("LoadBalanceInfo")!;
        Check((bool)Call(service, "TryBeginDisplayRequest", 1200u, 800u, 100u), "A new desktop request must be permitted.");
        for (var repeat = 0; repeat < 12; repeat++)
            Check(!(bool)Call(service, "TryBeginDisplayRequest", 1200u, 800u, 100u), "An unacknowledged broker size must not be repeatedly requested.");
        Check((bool)Call(service, "TryBeginDisplayRequest", 1280u, 800u, 100u), "A real user resize must permit a new request.");
        Check((bool)Call(service, "TryBeginDisplayRequest", 1280u, 800u, 125u), "A genuine DPI change must permit a new request.");
        var fixtureCookie = "Cookie: msts=purerdp-fixture-only";
        routingProperty.SetValue(profile, fixtureCookie);
        Call(service, "ApplyRoutingSettings");
        Check(System.Text.Encoding.Unicode.GetBytes(client.AdvancedSettings9.LoadBalanceInfo).SequenceEqual(
            System.Text.Encoding.UTF8.GetBytes(fixtureCookie + " \r\n")),
            "Disconnected ActiveX must receive the imported routing cookie before Connect.");
        routingProperty.SetValue(profile, fixtureCookie + "\r\n");
        Call(service, "ApplyRoutingSettings");
        Check(System.Text.Encoding.Unicode.GetBytes(client.AdvancedSettings9.LoadBalanceInfo).SequenceEqual(
            System.Text.Encoding.UTF8.GetBytes(fixtureCookie + " \r\n")),
            "Routing must not duplicate an existing CRLF terminator.");
        routingProperty.SetValue(profile, null);
        Call(service, "ApplyRoutingSettings");
        Check(string.IsNullOrEmpty(client.AdvancedSettings9.LoadBalanceInfo),
            "Ordinary connections must not inherit an earlier session ticket.");
        client.DesktopWidth = 800;
        client.DesktopHeight = 600;
        service.GetType().GetField("_viewDesktop", Members)!.SetValue(service, new Size(800, 600));
        var viewport = (Control)Field(service, "_rdpControl");
        var container = (Control)Field(service, "_container");
        var canUpscale = (bool)Field(service, "_smartSizingCanUpscale");
        Size Fitted(Size desktop)
        {
            var ratio = Math.Min((double)container.ClientSize.Width / desktop.Width, (double)container.ClientSize.Height / desktop.Height);
            if (!canUpscale) ratio = Math.Min(1, ratio);
            return new Size((int)Math.Floor(desktop.Width * ratio), (int)Math.Floor(desktop.Height * ratio));
        }
        Call(service, "RefreshPictureLayout");
        Console.WriteLine("Pre-login regression: resizing local viewport.");
        Check(client.Connected == 0 && !(bool)Field(service, "_loginComplete"), "Pre-login regression must not connect or authenticate.");
        window.ClientSize = new Size(400, 300);
        Pump();
        Check(viewport.Bounds == new Rectangle(Point.Empty, Fitted(new Size(800, 600))), "Container resizing must update the native viewport before login completes.");
        window.ClientSize = new Size(1200, 900);
        Pump();
        var expectedSize = Fitted(new Size(800, 600));
        Console.WriteLine($"Pre-login upscale: supported={canUpscale}; container={window.ClientSize}; viewport={viewport.Size}; expected={expectedSize}");
        Check(viewport.Size == expectedSize, $"A larger logon viewport must enable native enlargement or use the safe native-size fallback: supported={canUpscale}, host={window.ClientSize}, container={((Control)Field(service, "_container")).ClientSize}, containerBounds={((Control)Field(service, "_container")).Bounds}, desktop={Field(service, "_viewDesktop")}, viewport={viewport.Size}, expected={expectedSize}.");
        Check(client.AdvancedSettings9.SmartSizing, "Pre-login viewport fitting must keep SmartSizing enabled.");
        var timer = (System.Windows.Forms.Timer)Field(service, "_displayTimer");
        Check(!timer.Enabled, "Typing credentials must not start remote resolution/reconnect retries.");
        Call(service, "SetInteractiveResize", true);
        window.ClientSize = new Size(600, 450);
        Pump();
        Check(viewport.Size == Fitted(new Size(800, 600)) && !timer.Enabled, "Interactive pre-login resizing must update locally without negotiation.");
        var pausedSize = viewport.Size;
        Call(service, "SetInteractiveResize", false);
        Call(service, "SetDisplayUpdatesSuspended", true, false);
        window.ClientSize = new Size(400, 300);
        Pump();
        Check(viewport.Size == pausedSize, "Suspended display updates must not repaint an intermediate viewport.");
        Call(service, "SetDisplayUpdatesSuspended", false, true);
        Check(viewport.Size == Fitted(new Size(800, 600)), "Restoring a pre-login host must catch up its local viewport.");
        Call(service, "OnRemoteDesktopSizeChange", null!, new AxMSTSCLib.IMsTscAxEvents_OnRemoteDesktopSizeChangeEvent(1024, 512));
        Check((Size)Field(service, "_viewDesktop") == new Size(1024, 512) && viewport.Size == Fitted(new Size(1024, 512)),
            "Server-reported logon dimensions must immediately replace stale profile aspect ratios.");
        service.GetType().GetField("_loginComplete", Members)!.SetValue(service, true);
        Call(service, "ContainerSizeChanged", null!, EventArgs.Empty);
        Check(timer.Enabled, "After login, the latest client size must enter normal dynamic-resolution negotiation.");
        timer.Stop();
        service.GetType().GetField("_loginComplete", Members)!.SetValue(service, false);
        Check(client.Connected == 0, "Viewport regressions must never establish or reconnect a real RDP session.");
        Console.WriteLine($"Pre-login native viewport: EnableZoom={canUpscale}; Connected={client.Connected}; password unset.");
        }
        finally
        {
            // No Connect was issued. Do not call Disconnect in teardown for
            // an ActiveX which was only initialized for a local sizing test.
            Call(service, "RemoveRdpControlAndClient");
        }
    }

    private static void TestSilentStartupVisibility()
    {
        // Use a session test host to avoid constructing the real console's
        // tray/scanner or accessing saved connections. Exercise the exact
        // production SetVisibleCore gate with a real native HWND.
        using var silent = Window(null);
        typeof(FormRdpClient).GetField("_suppressInitialConsoleVisibility", Members)!.SetValue(silent, true);
        var settings = Field(silent, "_appSettings");
        settings.GetType().GetProperty("SilentStartup")!.SetValue(settings, true);
        var visibleFrames = 0;
        var shownEvents = 0;
        silent.VisibleChanged += (_, _) => { if (silent.Visible) visibleFrames++; };
        silent.Shown += (_, _) => shownEvents++;
        _ = silent.Handle;
        Check(!IsWindowVisible(silent.Handle), "Creating the silent instance's HWND must not show it.");
        Call(silent, "SetVisibleCore", true); // Application.Run's initial show
        Pump();
        Check(!silent.Visible && !IsWindowVisible(silent.Handle) && visibleFrames == 0 && shownEvents == 0,
            "Silent startup must never become visible, even for one frame before a queued Hide.");
        Check(!(bool)Field(silent, "_suppressInitialConsoleVisibility"), "Only the initial automatic show may be suppressed.");
        for (var repeat = 0; repeat < 3; repeat++)
            Call(silent, "HandleSingleInstanceCommand", new object[] { new[] { "--silent" } });
        Pump();
        Check(!silent.Visible && visibleFrames == 0, "Repeated silent launches must not wake the existing console.");
        Check(ReferenceEquals(settings, Field(silent, "_appSettings")), "Silent relays must not replace interactive settings.");
        Call(silent, "HandleSingleInstanceCommand", new object[] { Array.Empty<string>() });
        Pump();
        Check(silent.Visible && IsWindowVisible(silent.Handle) && shownEvents == 1,
            "A normal second launch must still explicitly open the hidden console.");
        Pump();
        Check(silent.Visible, "An explicitly opened console must not be hidden by a delayed startup callback.");
        silent.Hide();
        Call(silent, "HandleSingleInstanceCommand", new object[] { new[] { "--silent-startup" } });
        Check(!silent.Visible, "The silent-startup alias must also remain hidden.");
        Call(silent, "ShowManagementWindow");
        Pump();
        Check(silent.Visible, "The tray's open-console path must still work after silent startup.");

        // Also exercise the real framework startup path, not just a direct
        // invocation of the visibility override.
        Exception? failure = null;
        var loopThread = new Thread(() =>
        {
            try
            {
                using var loopHost = Window(null);
                typeof(FormRdpClient).GetField("_suppressInitialConsoleVisibility", Members)!.SetValue(loopHost, true);
                var loopFrames = 0;
                loopHost.VisibleChanged += (_, _) => { if (loopHost.Visible) loopFrames++; };
                using var context = new ApplicationContext(loopHost);
                using var timer = new System.Windows.Forms.Timer { Interval = 30 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        Check(!loopHost.Visible && !IsWindowVisible(loopHost.Handle) && loopFrames == 0,
                            "Actual Application.Run startup must keep the main HWND hidden with zero visible transitions.");
                        Call(loopHost, "ShowManagementWindow");
                        Check(loopHost.Visible && IsWindowVisible(loopHost.Handle) && loopFrames == 1,
                            "Explicit opening must remain available while the hidden application's message loop runs.");
                    }
                    catch (Exception error) { failure = error; }
                    finally { context.ExitThread(); }
                };
                timer.Start();
                Application.Run(context);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        // ExitThread cleans up all windows on its STA. Keep that lifecycle
        // separate from the STA used by the remaining window regressions.
        loopThread.SetApartmentState(ApartmentState.STA);
        loopThread.Start();
        if (!loopThread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Silent startup UI loop did not finish.");
        if (failure != null) throw new InvalidOperationException("Silent Application.Run regression failed.", failure);
    }

    private static void TestFreeResizeAndMemory(FormRdpClient window, Control bottomLeft, Control bottomRight)
    {
        var work = Screen.PrimaryScreen!.WorkingArea;
        var original = new Rectangle(work.Left + 100, work.Top + 40, 640, 480);
        foreach (var delta in new[] { new Point(80, 0), new Point(0, 60), new Point(80, -40) })
        {
            window.Bounds = original;
            var origin = bottomRight.PointToScreen(new Point(2, 2));
            SendMessage(bottomRight.Handle, 0x0201, (IntPtr)1, Packed(new Point(2, 2)));
            var move = Packed(window.PointToClient(origin + new Size(delta)));
            SendMessage(window.Handle, 0x0200, (IntPtr)1, move);
            SendMessage(window.Handle, 0x0202, IntPtr.Zero, move);
            Check(window.Size == original.Size + new Size(delta), "Each resize axis must follow its own mouse delta without aspect locking.");
        }
        window.Bounds = original;
        var leftOrigin = bottomLeft.PointToScreen(new Point(2, 2));
        SendMessage(bottomLeft.Handle, 0x0201, (IntPtr)1, Packed(new Point(2, 2)));
        var leftMove = Packed(window.PointToClient(leftOrigin + new Size(-60, 20)));
        SendMessage(window.Handle, 0x0200, (IntPtr)1, leftMove);
        SendMessage(window.Handle, 0x0202, IntPtr.Zero, Packed(window.PointToClient(leftOrigin + new Size(-60, 20))));
        Check(window.Right == original.Right && window.Size == new Size(700, 500), "Bottom-left free resize must anchor the opposite edge.");
        var rememberedBounds = window.Bounds;
        var saved = Call(window, "CaptureWindowSettings");
        var json = System.Text.Json.JsonSerializer.Serialize(saved, saved.GetType());
        var restoredSettings = System.Text.Json.JsonSerializer.Deserialize(json, saved.GetType())!;
        using var reopened = Window(null);
        var connection = Field(reopened, "_appSettings").GetType().GetProperty("Connection")!.GetValue(Field(reopened, "_appSettings"))!;
        connection.GetType().GetProperty("Width")!.SetValue(connection, 1200);
        connection.GetType().GetProperty("Height")!.SetValue(connection, 800);
        Call(reopened, "ApplyPersistedWindowSettings", restoredSettings);
        Check(reopened.Bounds == rememberedBounds, "JSON-restored manual geometry must take priority over profile dimensions.");
        Check(!(bool)Field(reopened, "_applyConnectionWindowSizeAfterConnect"), "Connect completion must not reset restored manual dimensions.");
        Call(reopened, "SetRemoteConnectionChrome", true);
        Check(reopened.Bounds == rememberedBounds, "Removing startup window chrome must not shrink the saved geometry.");
        Call(reopened, "ApplyConnectionWindowSize", false);
        Check(reopened.Bounds == rememberedBounds, "Later profile-size application must preserve the last manual bounds.");
        connection.GetType().GetProperty("AutoMaximizeWindow")!.SetValue(connection, true);
        restoredSettings.GetType().GetProperty("FullScreen")!.SetValue(restoredSettings, true);
        reopened.Show();
        Call(reopened, "ApplyPersistedWindowSettings", restoredSettings);
        Pump();
        Check(reopened.WindowState == FormWindowState.Maximized && IsZoomed(reopened.Handle),
            "Explicit automatic window fill must override remembered manual geometry on startup.");
        Check(!(bool)Field(reopened, "_restoreFullScreenOnConnect") &&
            !(bool)Field(reopened, "_reconnectFullScreen"),
            "Window fill must not restore an old true-fullscreen preference.");
        Call(reopened, "RdpService_Connected", reopened, EventArgs.Empty);
        Pump();
        Check(reopened.WindowState == FormWindowState.Maximized && IsZoomed(reopened.Handle),
            "Connected chrome must preserve automatic window fill rather than restoring manual bounds.");
        Check(!(bool)Field(reopened, "_applyConnectionWindowSizeAfterConnect"),
            "Connection completion must not apply fixed profile dimensions over automatic window fill.");
        Call(reopened, "GlassChromeStrip_MaximizeRequested", reopened, EventArgs.Empty);
        Pump();
        Check(reopened.WindowState == FormWindowState.Normal && reopened.Size == rememberedBounds.Size,
            "Explicit unmaximize must still recover the remembered manual window size.");
        connection.GetType().GetProperty("AutoMaximizeWindow")!.SetValue(connection, false);
        restoredSettings.GetType().GetProperty("FullScreen")!.SetValue(restoredSettings, false);
        Call(reopened, "ApplyPersistedWindowSettings", restoredSettings);
        Check(reopened.WindowState == FormWindowState.Normal && reopened.Bounds == rememberedBounds,
            "Disabling automatic fill must restore manual geometry on the next startup.");
        var service = Field(window, "_rdpService");
        Call(service, "SetFullScreen", true, true);
        Call(service, "SetFullScreen", false, true);
        Check(window.Bounds == rememberedBounds, "Returning from true fullscreen must preserve the freely adjusted geometry.");
        var options = Field(window, "_appSettings");
        var settings = options.GetType().GetProperty("Window")!.GetValue(options)!;
        foreach (var name in new[] { "ManualWindowX", "ManualWindowY", "ManualWindowWidth", "ManualWindowHeight" })
            settings.GetType().GetProperty(name)!.SetValue(settings, null);
    }

    private static void TestCollapsedRail()
    {
        var stripType = typeof(FormRdpClient).Assembly.GetType("PureRDP.UI.GlassWindowControlStrip")!;
        var positionType = typeof(FormRdpClient).Assembly.GetType("PureRDP.Configuration.Enums.ControlBarPosition")!;
        using var rail = (Form)Activator.CreateInstance(stripType, true)!;
        Check(rail.Size == new Size(12, 44), "Initial rail must use the chosen medium capsule width.");
        rail.Location = Screen.PrimaryScreen!.WorkingArea.Location + new Size(200, 200);
        rail.Show();
        Check(rail.Size == new Size(12, 44), "First Show must not impose a different capsule width.");
        var evidence = Path.Combine("outputs", "ui-review");
        Directory.CreateDirectory(evidence);
        using var review = new Bitmap(400, 220);
        using (var graphics = Graphics.FromImage(review))
        {
            graphics.Clear(Color.FromArgb(245, 248, 249));
            using var dark = new SolidBrush(Color.FromArgb(29, 42, 52));
            graphics.FillRectangle(dark, 200, 0, 200, 220);
            for (var position = 0; position < 4; position++)
            {
                Call(rail, "SetPosition", Enum.ToObject(positionType, position));
                var collapsedSize = position < 2 ? new Size(44, 12) : new Size(12, 44);
                Check(rail.Size == collapsedSize, "All orientations must use the same medium capsule dimensions.");
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    Call(rail, "Expand");
                    Call(rail, "SetExpanded", false);
                    Check(rail.Size == collapsedSize, "Triggered collapse must match the initial capsule size without cumulative changes.");
                }
                rail.Hide(); rail.Show();
                Check(rail.Size == collapsedSize, "Showing a hidden rail must not change the collapsed size.");
                using var surface = (Bitmap)Call(rail, "CreateSurfaceBitmap");
                Check(surface.GetPixel(0, 0).A < surface.GetPixel(surface.Width / 2, surface.Height / 2).A,
                    "Capsule corners must be rounded/transparent, not a rectangular backing.");
                Check(surface.GetPixel(surface.Width / 2, surface.Height / 2).A > 96,
                    "Capsule center must be visibly luminous and natively hittable.");
                var point = rail.PointToScreen(new Point(rail.Width / 2, rail.Height / 2));
                Check(WindowFromPoint(point) == rail.Handle, "Collapsed hover target must remain natively hittable on all four orientations.");
                surface.Save(Path.Combine(evidence, $"ControlRail-{position}.png"), System.Drawing.Imaging.ImageFormat.Png);
                graphics.DrawImage(surface, 30 + (position % 2) * 85, 20 + (position / 2) * 95);
                graphics.DrawImage(surface, 230 + (position % 2) * 85, 20 + (position / 2) * 95);
            }
        }
        review.Save(Path.Combine(evidence, "ControlRail-Review.png"), System.Drawing.Imaging.ImageFormat.Png);
        Call(rail, "OnMouseEnter", EventArgs.Empty);
        Check((bool)stripType.GetProperty("IsExpanded")!.GetValue(rail)!, "Hover must still expand the compact capsule.");
        var expandedSize = rail.Size;
        rail.Hide(); rail.Show();
        Check(rail.Size == expandedSize, "Re-showing an expanded toolbar must preserve its buttons and dimensions.");
    }
}
