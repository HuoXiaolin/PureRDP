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
        if (!value) throw new InvalidOperationException(message);
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
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(SystemInformation.VirtualScreen.Right + 4096, 100);
        window.Size = new Size(800, 600);
        window.ShowInTaskbar = false;
        return window;
    }

    [STAThread]
    private static void Main()
    {
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
        Check(visibleGrip.Size == new Size(5, 5), "Resize corner must be approximately one third of its previous dimensions.");
        using (var cornerSurface = (Bitmap)Call(visibleGrip, "CreateSurfaceBitmap"))
            Check(cornerSurface.GetPixel(0, 0).A is > 0 and <= 32, "Resize corner must be nearly transparent but retain native input.");
        var nativePoint = visibleGrip.PointToScreen(new Point(2, 2));
        var nativeTarget = WindowFromPoint(nativePoint);
        Console.WriteLine($"Corner routing: grip={visibleGrip.Handle} target={nativeTarget} targetControl={Control.FromHandle(nativeTarget)?.GetType().Name} visible={visibleGrip.Visible} enabled={visibleGrip.Enabled} composition={visibleGrip.GetType().GetProperty("LastCompositionError", Members)!.GetValue(visibleGrip)} bounds={visibleGrip.Bounds}");
        Check(nativeTarget == visibleGrip.Handle, "A real bottom-right click must hit the resize grip, not the RDP surface or another window.");
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
