using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using PureRDP.UI;

internal static class Program
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly AppAssembly = typeof(FormRdpClient).Assembly;
    private static readonly Type SettingsType = AppAssembly.GetType("PureRDP.Configuration.ConnectionSettings")!;
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        CheckScanner();
        CheckButton();
        CheckDisconnectedSampling();
        Console.WriteLine($"PASS: {_checks} status-refresh checks; loopback TCP and offscreen controls only.");
    }

    private static object Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethod(method, InstanceMembers)!.Invoke(target, args)!;

    private static object Field(object target, string name)
        => target.GetType().GetField(name, InstanceMembers)!.GetValue(target)!;

    private static object Settings(string endpoint)
    {
        var settings = Activator.CreateInstance(SettingsType, true)!;
        SettingsType.GetProperty("Hostname", InstanceMembers)!.SetValue(settings, endpoint);
        return settings;
    }

    private static Array SettingsArray(params string[] endpoints)
    {
        var devices = Array.CreateInstance(SettingsType, endpoints.Length);
        for (var index = 0; index < endpoints.Length; index++) devices.SetValue(Settings(endpoints[index]), index);
        return devices;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Refresh did not complete.");
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void CheckScanner()
    {
        using var scanner = (IDisposable)Activator.CreateInstance(AppAssembly.GetType("PureRDP.Services.DeviceAvailabilityScanner")!, true)!;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        Invoke(scanner, "SetDevices", SettingsArray(endpoint));
        var online = (Task)Invoke(scanner, "ScanNowAsync");
        WaitUntil(() => online.IsCompleted);
        online.GetAwaiter().GetResult();
        var states = (IDictionary)Field(scanner, "_states");
        Check(states[endpoint]?.ToString() == "Online", "Manual scan must detect a listening TCP port.");
        listener.Stop();
        var offline = (Task)Invoke(scanner, "ScanNowAsync");
        WaitUntil(() => offline.IsCompleted);
        offline.GetAwaiter().GetResult();
        Check(states[endpoint]?.ToString() == "Offline", "Manual scan must update the same endpoint to offline.");
        var first = (Task)Invoke(scanner, "ScanNowAsync");
        var queued = (Task)Invoke(scanner, "ScanNowAsync");
        WaitUntil(() => first.IsCompleted && queued.IsCompleted);
        Task.WhenAll(first, queued).GetAwaiter().GetResult();
        Check(!first.IsFaulted && !queued.IsFaulted, "Concurrent requests must complete without overlapping scan failures.");
        Invoke(scanner, "SetDevices", SettingsArray());
        var empty = (Task)Invoke(scanner, "ScanNowAsync");
        WaitUntil(() => empty.IsCompleted);
        Check(states.Count == 0, "Empty libraries must refresh safely.");
        scanner.Dispose();
        Check(((Task)Invoke(scanner, "ScanNowAsync")).IsCompletedSuccessfully, "Disposed scanners must ignore refresh safely.");
    }

    private static void CheckButton()
    {
        using var library = (UserControl)Activator.CreateInstance(AppAssembly.GetType("PureRDP.UI.ConnectionLibraryPanel")!, true)!;
        var button = (Button)Field(library, "_refreshButton");
        var callback = library.GetType().GetProperty("StatusRefreshRequested", InstanceMembers)!;
        var pending = new TaskCompletionSource();
        var calls = 0;
        callback.SetValue(library, (Func<Task>)(() => { calls++; return pending.Task; }));
        var items = (FlowLayoutPanel)Field(library, "_itemsPanel");
        using var sentinel = new Panel();
        items.Controls.Add(sentinel);
        Invoke(library, "RefreshButton_Click", button, EventArgs.Empty);
        Check(!button.Enabled && button.Text == "刷新中…", "Button must show busy feedback during a real refresh.");
        Invoke(library, "RefreshButton_Click", button, EventArgs.Empty);
        Check(calls == 1, "Repeat clicks must not queue redundant refreshes.");
        pending.SetResult();
        WaitUntil(() => button.Enabled);
        Check(button.Text == "刷新状态", "Button must reset after refresh completes.");
        Check(items.Controls.Contains(sentinel) && !sentinel.IsDisposed, "Refreshing must not rebuild cards or preview surfaces.");
        foreach (var width in new[] { 640, 820, 1200 })
        {
            library.Size = new Size(width, 700);
            library.PerformLayout();
            var header = button.Parent!;
            Check(button.Right == header.ClientSize.Width - header.Padding.Right && button.Top == 70,
                "Refresh button must remain aligned at the upper right on resize.");
        }
        callback.SetValue(library, (Func<Task>)(() => Task.FromException(new IOException("test"))));
        Invoke(library, "RefreshButton_Click", button, EventArgs.Empty);
        WaitUntil(() => button.Enabled);
        Check(((Label)Field(library, "_statusLabel")).Text.Contains("刷新失败"), "Errors must be handled without leaving the button disabled.");
        pending = new TaskCompletionSource();
        callback.SetValue(library, (Func<Task>)(() => pending.Task));
        Invoke(library, "RefreshButton_Click", button, EventArgs.Empty);
        library.Dispose();
        pending.SetResult();
        Application.DoEvents();
        Check(library.IsDisposed, "Closing the console during refresh must be safe.");
    }

    private static void CheckDisconnectedSampling()
    {
        using var form = new Form();
        using var surface = new Panel();
        form.Controls.Add(surface);
        var type = AppAssembly.GetType("PureRDP.Services.RdpClientService")!;
        using var service = (IDisposable)Activator.CreateInstance(type, InstanceMembers, null, new object[] { surface }, null)!;
        Check(((Task)Invoke(service, "RefreshConnectionQualityAsync")).IsCompletedSuccessfully,
            "Refresh must not connect a disconnected RDP session.");
        var pending = new TaskCompletionSource();
        type.GetField("_qualityProbeInFlight", InstanceMembers)!.SetValue(service, true);
        type.GetField("_qualityProbeTask", InstanceMembers)!.SetValue(service, pending.Task);
        Check(ReferenceEquals(Invoke(service, "RefreshConnectionQualityAsync"), pending.Task),
            "Manual refresh must await an existing periodic probe.");
        pending.SetResult();
        type.GetField("_qualityProbeInFlight", InstanceMembers)!.SetValue(service, false);
        service.Dispose();
        Check(((Task)Invoke(service, "RefreshConnectionQualityAsync")).IsCompletedSuccessfully,
            "Disposed RDP services must ignore refresh safely.");
        using var preview = (IDisposable)Activator.CreateInstance(AppAssembly.GetType("PureRDP.UI.PreviewSessionHost")!, InstanceMembers,
            null, new object[] { Settings("127.0.0.1") }, null)!;
        Check(((Task)Invoke(preview, "RefreshConnectionQualityAsync")).IsCompletedSuccessfully,
            "Refresh must not start a closed preview session.");
    }
}
