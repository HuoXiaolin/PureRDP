using System;
using System.Runtime.InteropServices;

namespace PureRDP.Platform;

internal static class WindowChromeInterop
{
    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr window, ref System.Drawing.Point point);
    [DllImport("user32.dll")]
    public static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>
    /// Activates a restored window even when the shortcut was pressed while
    /// another application's window was foreground. Windows can reject a
    /// direct SetForegroundWindow call in that case; temporarily sharing the
    /// foreground thread's input queue gives the activation the same input
    /// context without changing the window or remote display state.
    /// </summary>
    public static bool SetForegroundWindowReliable(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;

        var foreground = GetForegroundWindow();
        if (foreground == window)
            return true;

        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            return SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

    [DllImport("user32.dll")]
    public static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

}
