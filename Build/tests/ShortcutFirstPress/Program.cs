using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PureRDP.UI;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;
    private static object Field(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Members)!.SetValue(target, value);
    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
        _checks++;
    }
    private static void Pump()
    {
        for (var index = 0; index < 6; index++) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static FormRdpClient Window(FormRdpClient? manager)
    {
        var window = (FormRdpClient)Activator.CreateInstance(typeof(FormRdpClient), Members, null,
            new object?[] { Array.Empty<string>(), true, manager }, null)!;
        var profile = Field(window, "_appSettings").GetType().GetProperty("Connection")!.GetValue(Field(window, "_appSettings"))!;
        // The assertions below deliberately restore normal-sized windows.
        profile.GetType().GetProperty("AutoMaximizeWindow")!.SetValue(profile, false);
        _ = window.Handle;
        return window;
    }
    private static IMessageFilter Filter(FormRdpClient manager)
    {
        var type = typeof(FormRdpClient).Assembly.GetType("PureRDP.UI.GlobalShortcutMessageFilter")!;
        // Simulate a nonzero hook handle whose callback missed the first chord.
        // No global keyboard hook or actual key injection is installed here.
        var filter = RuntimeHelpers.GetUninitializedObject(type);
        Set(filter, "managementWindow", manager);
        Set(filter, "_hook", new IntPtr(1));
        Set(filter, "_consumed", new HashSet<Keys>());
        Set(filter, "_modifierKeysDown", new HashSet<int>());
        Set(filter, "_availabilityTimer", new System.Windows.Forms.Timer());
        Set(filter, "_remoteInputRefreshQueued", true);
        return (IMessageFilter)filter;
    }
    private static bool Send(IMessageFilter filter, FormRdpClient window, int message, Keys key, long flags = 0)
    {
        var input = Message.Create(window.Handle, message, new IntPtr((int)key), new IntPtr(flags));
        return filter.PreFilterMessage(ref input);
    }
    private static void ChordDown(IMessageFilter filter, FormRdpClient window)
    {
        Send(filter, window, 0x0100, Keys.ControlKey);
        Send(filter, window, 0x0104, Keys.Menu);
    }
    private static void ChordUp(IMessageFilter filter, FormRdpClient window)
    {
        Send(filter, window, 0x0101, Keys.Z);
        Send(filter, window, 0x0105, Keys.Menu);
        Send(filter, window, 0x0101, Keys.ControlKey);
    }


    [STAThread]
    private static void Main()
    {
        using var manager = Window(null);
        using var first = Window(manager);
        using var second = Window(manager);
        ((IList)Field(manager, "_sessionWindows")).Add(first);
        ((IList)Field(manager, "_sessionWindows")).Add(second);
        var preferences = Field(manager, "_globalShortcutPreferences");
        preferences.GetType().GetProperty("EffectMode")!.SetValue(preferences,
            Enum.ToObject(preferences.GetType().GetProperty("EffectMode")!.PropertyType, 1));
        ((IDictionary)Field(manager, "_shortcutAvailability"))[Keys.Control | Keys.Alt | Keys.Z] = (Environment.TickCount64, true);
        Set(Field(first, "_rdpService"), "_fullScreen", true);
        var filter = Filter(manager);
        using var cleanup = (IDisposable)filter;

        ChordDown(filter, first);
        Check(Send(filter, first, 0x0104, Keys.Z, 1L << 29), "First chord was ignored merely because the hook handle is nonzero.");
        Check((bool)Field(manager, "_allSessionMinimizePending"), "First chord must queue minimize immediately.");
        Check(Send(filter, first, 0x0104, Keys.Z, (1L << 29) | (1L << 30)), "Held-key repeats must be consumed.");
        Pump();
        Check(first.WindowState == FormWindowState.Minimized && second.WindowState == FormWindowState.Minimized,
            "The first chord must minimize both connections, including true fullscreen.");
        Check((bool)Field(Field(first, "_rdpService"), "_fullScreen"), "Minimize must not discard fullscreen state.");
        Send(filter, first, 0x0104, Keys.Z, (1L << 29) | (1L << 30)); Pump();
        Check(first.WindowState == FormWindowState.Minimized, "A repeat must not immediately restore the minimized window.");
        ChordUp(filter, first); ChordDown(filter, first);
        Check(Send(filter, first, 0x0104, Keys.Z, 1L << 29), "The next distinct chord must restore normally.");
        Pump();
        Check(first.WindowState == FormWindowState.Normal && second.WindowState == FormWindowState.Normal,
            "The second distinct chord must restore both connections.");
        Check((bool)Field(Field(first, "_rdpService"), "_fullScreen"), "Restore must retain true fullscreen state.");
        ChordUp(filter, first);

        // A chord already handled in the low-level hook must not also execute
        // through the queued-message fallback, even if both routes see it.
        ChordDown(filter, first);
        var data = Marshal.AllocHGlobal(32);
        try
        {
            for (var offset = 0; offset < 32; offset += 4) Marshal.WriteInt32(data, offset, 0);
            Marshal.WriteInt32(data, (int)Keys.Z);
            Marshal.WriteInt32(data, 8, 0x20);
            var callback = filter.GetType().GetMethod("KeyboardCallback", Members)!;
            Check((IntPtr)callback.Invoke(filter, new object[] { 0, new IntPtr(0x0104), data })! == new IntPtr(1),
                "Low-level shortcut must be consumed.");
            Pump();
            Check((IntPtr)callback.Invoke(filter, new object[] { 0, new IntPtr(0x0104), data })! == new IntPtr(1),
                "A repeated swallowed hook key must stay consumed even when async key state is up.");
            Pump();
            Check(first.WindowState == FormWindowState.Minimized, "Hook auto-repeat must not undo minimization.");
            Check(Send(filter, first, 0x0104, Keys.Z, 1L << 29), "Duplicate queue message must be consumed without redispatch.");
            Pump();
            Check(first.WindowState == FormWindowState.Minimized, "Dual input routes must execute only one minimize, not a restore.");
        }
        finally { Marshal.FreeHGlobal(data); }
        ChordUp(filter, first);
        Check(!Send(filter, first, 0x0100, Keys.F8), "Unconfigured keys must remain available to RDP.");
        ((IDictionary)Field(manager, "_shortcutAvailability"))[Keys.Control | Keys.Alt | Keys.Z] = (Environment.TickCount64, false);
        ChordDown(filter, first);
        Check(!Send(filter, first, 0x0104, Keys.Z, 1L << 29), "Conflicting shortcuts must not be captured by the fallback.");
        ChordUp(filter, first);
        // Native child focus may bypass both form activation and mouse filters.
        // Exercise that notification without installing a real keyboard hook.
        var focusCallback = filter.GetType().GetMethod("RemoteFocusChanged", Members)!;
        var remotePanel = (Control)Field(first, "panelRdp");
        using var remoteChild = new Control();
        remotePanel.Controls.Add(remoteChild);
        _ = remoteChild.Handle;
        void FocusEvent(IntPtr handle) => focusCallback.Invoke(filter,
            new object[] { IntPtr.Zero, (uint)0x8005, handle, 0, 0, (uint)0, (uint)0 });
        Set(filter, "_remoteInputRefreshQueued", false);
        FocusEvent(manager.Handle);
        Check(!(bool)Field(filter, "_remoteInputRefreshQueued"), "Console focus must not schedule a remote hook refresh.");
        FocusEvent(remoteChild.Handle);
        Check((bool)Field(filter, "_remoteInputRefreshQueued"), "Native remote child focus must schedule a refresh without opening the control bar.");
        FocusEvent(remoteChild.Handle);
        Check((bool)Field(filter, "_remoteInputRefreshQueued"), "Repeated native focus notifications must stay coalesced.");
        Set(filter, "_disposed", true);
        Pump();
        Check(!(bool)Field(filter, "_remoteInputRefreshQueued"), "Queued focus refresh must drain safely after disposal.");
        Check(!Send(filter, first, 0x0100, Keys.Z), "Disposed filter must not process input.");
        Set(filter, "_disposed", false);
        Console.WriteLine($"PASS: {_checks} first-shortcut checks; simulated input routes, no key injection or RDP login.");
    }
}
