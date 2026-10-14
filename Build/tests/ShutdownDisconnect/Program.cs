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
            if (dialog != null) Check(((Form)dialog).IsDisposed, "Existing reconnect dialog must be disposed.");
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
}
