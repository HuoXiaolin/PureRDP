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
        Check((bool)Field(manager, "_allSessionsMinimized"), "Repro requires the stale group flag after external restore.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(IsIconic(first.Handle) && IsIconic(second.Handle), "First command after taskbar restore must minimize, not redundantly restore.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(!IsIconic(first.Handle) && !IsIconic(second.Handle), "Next command restores the externally restored pair.");

        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        first.WindowState = FormWindowState.Maximized; Pump();
        Check(IsIconic(second.Handle), "Partial external restore must leave the other connection minimized before the command.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(IsIconic(first.Handle) && IsIconic(second.Handle), "Partial taskbar restore must also minimize all on the first command.");
        Call(manager, "ToggleAllSessionMinimize", false); Pump();
        Check(first.WindowState == FormWindowState.Maximized && second.WindowState == FormWindowState.Normal,
            "Restoring after partial external restore must preserve both visible window states.");
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
}
