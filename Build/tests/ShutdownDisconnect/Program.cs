using System.Reflection;
using PureRDP.Events;
using PureRDP.Models;
using PureRDP.UI;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;
    private static object Field(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Members)!.SetValue(target, value);
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        _checks++;
    }

    private static FormRdpClient Session()
    {
        var type = typeof(FormRdpClient);
        var window = (FormRdpClient)Activator.CreateInstance(type, Members, null,
            new object?[] { Array.Empty<string>(), true, null }, null)!;
        // Remove settings persistence and startup, not disconnect handling.
        // These tests never log in, register shortcuts or save user settings.
        window.Shown -= (EventHandler)type.GetMethod("FormRdpClient_Shown", Members)!.CreateDelegate(typeof(EventHandler), window);
        window.FormClosing -= (FormClosingEventHandler)type.GetMethod("FormRdpClient_FormClosing", Members)!.CreateDelegate(typeof(FormClosingEventHandler), window);
        _ = window.Handle;
        return window;
    }

    [STAThread]
    private static void Main()
    {
        TestFallbackCancellation();
        TestInlineRetries();
        TestImportedPasswordFailure();
        foreach (var reason in new[] { DisconnectReasonCode.NoInfo, DisconnectReasonCode.ByServer, DisconnectReasonCode.ConnectionTimedOut })
            Check(new DisconnectedEventArgs(reason, DisconnectReasonCodeExt.ServerShutdown, null, false).IsRemoteShutdown,
                "Explicit shutdown must be recognized regardless of the basic transport reason.");
        foreach (var extended in new[] { DisconnectReasonCodeExt.NoInfo, DisconnectReasonCodeExt.ServerRestart,
            DisconnectReasonCodeExt.ServerIdleTimeout, DisconnectReasonCodeExt.ServerDeniedConnection, (DisconnectReasonCodeExt)999 })
            Check(!new DisconnectedEventArgs(DisconnectReasonCode.ByServer, extended, "shutdown", false).IsRemoteShutdown,
                "Descriptions, reboot, timeouts and unknown codes must not be mistaken for shutdown.");
        foreach (var pendingRetry in new[] { false, true })
        {
            using var other = Session();
            using var window = Session();
            Set(window, "_automaticReconnectActive", pendingRetry);
            Set(window, "_automaticReconnectAttempts", pendingRetry ? 4 : 0);
            Set(window, "_automaticReconnectAttemptInProgress", pendingRetry);
            object? dialog = null;
            if (pendingRetry)
            {
                dialog = Activator.CreateInstance(typeof(FormRdpClient).Assembly.GetType("PureRDP.UI.RdpReconnectDialog")!, Members,
                    null, new object[] { "loopback-test" }, null)!;
                Set(window, "_reconnectDialog", dialog);
            }
            var disconnected = typeof(FormRdpClient).GetMethod("RdpService_Disconnected", Members)!;
            var shutdown = new DisconnectedEventArgs(DisconnectReasonCode.ByServer, DisconnectReasonCodeExt.ServerShutdown, null, false);
            disconnected.Invoke(window, new object?[] { null, shutdown });
            Check((bool)Field(window, "_sessionClosing"), "Shutdown must mark the session closing before deferred close.");
            Check(!(bool)Field(window, "_automaticReconnectActive") && !(bool)Field(window, "_automaticReconnectAttemptInProgress"),
                "Shutdown must cancel pending and in-flight retry state.");
            Check((int)Field(window, "_automaticReconnectAttempts") == 0 &&
                typeof(FormRdpClient).GetField("_reconnectDialog", Members)!.GetValue(window) == null,
                "Shutdown must clear the retry count and dialog reference.");
            if (dialog != null) Check(((Control)dialog).IsDisposed, "Existing reconnect surface must be disposed.");
            disconnected.Invoke(window, new object?[] { null, shutdown });
            Application.DoEvents();
            Check(window.IsDisposed, "Shutdown must close the connection window.");
            Check(!other.IsDisposed && !(bool)Field(other, "_sessionClosing"), "Shutdown must not close another connection window.");
        }
        using var internalReconnect = Session();
        typeof(FormRdpClient).GetMethod("RdpService_Disconnected", Members)!.Invoke(internalReconnect, new object?[] { null,
            new DisconnectedEventArgs(DisconnectReasonCode.ByServer, DisconnectReasonCodeExt.ServerShutdown, null, true) });
        Check(!(bool)Field(internalReconnect, "_sessionClosing"), "Internal client reconnect notifications must remain ignored.");
        Console.WriteLine($"PASS: {_checks} shutdown-disconnect checks; simulated reason codes, no RDP login.");
    }

    private static void TestFallbackCancellation()
    {
        var fallbackType = typeof(FormRdpClient).Assembly.GetType("PureRDP.Services.NativeRdpFallback")!;
        foreach (var succeeded in new[] { false, true })
        {
            using var window = Session();
            var application = Field(window, "_appSettings");
            application.GetType().GetProperty("IsExternalConnection")!.SetValue(application, true);
            var profile = application.GetType().GetProperty("Connection")!.GetValue(application)!;
            profile.GetType().GetProperty("Hostname")!.SetValue(profile, "example.invalid");
            using var request = (IDisposable)fallbackType.GetMethod("Capture", Members | BindingFlags.Static)!
                .Invoke(null, new object[] { application, Array.Empty<string>() })!;
            profile.GetType().GetProperty("NativeFallback")!.SetValue(profile, request);
            if (succeeded)
                typeof(FormRdpClient).GetMethod("RdpService_LoginCompleted", Members)!.Invoke(window, new object?[] { null, EventArgs.Empty });
            else
                typeof(FormRdpClient).GetMethod("Disconnect", Members)!.Invoke(window, null);
            Check(!(bool)fallbackType.GetMethod("TryClaim", Members)!.Invoke(request, null)!,
                "Successful login and explicit user cancellation must suppress native fallback.");
            Check(!(bool)typeof(FormRdpClient).GetMethod("TrySwitchToNativeAfterFailure", Members)!.Invoke(window, null)!,
                "A later disconnect event must not open a duplicate native session after success or cancellation.");
        }
    }

    private static void TestImportedPasswordFailure()
    {
        using var session = Session();
        session.Show();
        var application = Field(session, "_appSettings");
        var profile = application.GetType().GetProperty("Connection")!.GetValue(application)!;
        profile.GetType().GetProperty("Hostname")!.SetValue(profile, "example.invalid");
        profile.GetType().GetProperty("EncryptedPassword51")!.SetValue(profile, "invalid-fixture");
        var start = typeof(FormRdpClient).GetMethod("StartConnection", Members, null, new[] { typeof(bool) }, null)!;
        start.Invoke(session, new object[] { false });
        Check((bool)Field(session, "_sessionClosing") && !(bool)Field(session, "_automaticReconnectActive"),
            "An invalid imported password must stop immediately instead of repeatedly trying network reconnects.");
        var service = Field(session, "_rdpService");
        var native = Field(service, "_rdpClient");
        Check(((MSTSCLib.MsRdpClient10NotSafeForScripting)native).Connected == 0,
            "Password decryption failure must occur before any native RDP login.");
        Application.DoEvents();
        Check(session.IsDisposed, "A failed credential import must release the connection window and native control.");
    }

    private static void TestInlineRetries()
    {
        using (var session = Session())
        using (var sibling = Session())
        {
            session.Show();
            var formsBefore = Application.OpenForms.Count;
            var ownedBefore = session.OwnedForms.Length;
            typeof(FormRdpClient).GetMethod("BeginAutomaticReconnect", Members)!.Invoke(session, new object[] { "synthetic failure" });
            var surface = (Control)Field(session, "_reconnectDialog");
            Check(surface.Parent == session && surface.Dock == DockStyle.Fill && !(surface is Form), "Retries must be embedded in the same session.");
            Check(Application.OpenForms.Count == formsBefore && session.OwnedForms.Length == ownedBefore, "Retries must not create a popup window.");
            Check(session.Controls.GetChildIndex(surface) == 0, "Retry surface must cover the native RDP host.");
            for (var attempt = 0; attempt < 10; attempt++)
            {
                Set(session, "_automaticReconnectAttempts", attempt);
                typeof(FormRdpClient).GetMethod("ScheduleAutomaticReconnect", Members)!.Invoke(session, new object[] { "synthetic failure" });
                Check((int)Field(surface, "_remainingSeconds") == (1 << (attempt + 1)), "Retries must use 2, 4, 8...1024 seconds.");
            }
            var timer = (System.Windows.Forms.Timer)Field(surface, "_timer");
            Check(timer.Enabled, "Waiting retry must have an active countdown.");
            ((Button)Field(surface, "_cancelButton")).PerformClick();
            Application.DoEvents();
            Check(session.IsDisposed && surface.IsDisposed && !timer.Enabled, "Cancel must close the current session and dispose its retry surface.");
            Check(!sibling.IsDisposed, "Cancel must not close a sibling connection.");
        }
        using (var exhausted = Session())
        {
            exhausted.Show();
            typeof(FormRdpClient).GetMethod("BeginAutomaticReconnect", Members)!.Invoke(exhausted, new object[] { "synthetic failure" });
            var surface = (Control)Field(exhausted, "_reconnectDialog");
            Set(exhausted, "_automaticReconnectAttempts", 10);
            typeof(FormRdpClient).GetMethod("ScheduleAutomaticReconnect", Members)!.Invoke(exhausted, new object[] { "synthetic failure" });
            Application.DoEvents();
            Check(exhausted.IsDisposed && surface.IsDisposed, "Ten failed retries must close the current connection.");
        }
    }
}
