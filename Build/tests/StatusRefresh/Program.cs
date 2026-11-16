using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
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
        CheckLanDiscovery();
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

    private static void CheckLanDiscovery()
    {
        var discoveryType = AppAssembly.GetType("PureRDP.Services.LanDiscoveryService")!;
        var parseRange = discoveryType.GetMethod("TryParseRange", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var (text, expected) in new (string, string?)[] {
            ("192.168.1.93/24", "192.168.1.1–192.168.1.254"),
            ("192.168.1.10-50", "192.168.1.10–192.168.1.50"),
            ("192.168.1.254 - 192.168.2.2", "192.168.1.254–192.168.2.2"),
            ("192.168.1.12/31", "192.168.1.12–192.168.1.13"),
            ("127.0.0.1", "127.0.0.1"),
            ("192.168.1.50-10", null),
            ("192.168.0.0/16", null),
            ("192.168.1/24", null),
            ("224.0.0.1", null),
            ("not-an-ip", null),
        })
        {
            object?[] args = [text, null, null];
            var valid = (bool)parseRange.Invoke(null, args)!;
            Check(expected == null ? !valid && args[2] is string { Length: > 0 } : valid && args[1]?.ToString() == expected,
                "Invalid IPv4 range handling: " + text);
        }

        var formType = AppAssembly.GetType("PureRDP.UI.LanDiscoveryDialog")!;
        var networkType = AppAssembly.GetType("PureRDP.Services.LanScanNetwork")!;
        const BindingFlags serviceMembers = BindingFlags.NonPublic | BindingFlags.Static;
        var isLocalAddress = (Func<IPAddress, bool>)discoveryType.GetMethod("CreateLocalAddressFilter", serviceMembers)!.Invoke(null, null)!;
        var adapterNetworks = (Array)discoveryType.GetMethod("GetAdapterNetworks", serviceMembers)!.Invoke(null, null)!;
        Check(isLocalAddress(IPAddress.Loopback) && isLocalAddress(IPAddress.Parse("127.25.0.1")) &&
            adapterNetworks.Cast<object>().All(network =>
                isLocalAddress((IPAddress)networkType.GetProperty("LocalAddress")!.GetValue(network)!)),
            "Local discovery must exclude the loopback block and every assigned scan-adapter IPv4 address.");
        object Network(string name, string address, int prefix) => Activator.CreateInstance(networkType,
            InstanceMembers, null, new object[] { "test-switch-id", name, IPAddress.Parse(address), prefix }, null)!;
        var hyperV = Network("vEthernet (Default Switch)", "172.25.0.1", 20);
        var natRange = networkType.GetProperty("ScanRange", InstanceMembers)!.GetValue(hyperV);
        Check(natRange?.ToString() == "172.25.0.1–172.25.15.254",
            "Hyper-V NAT must keep the adapter's entire /20, including guests outside the gateway's /24.");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = $"127.0.0.1:{port}";

        Form OpenScan(string[] saved, Delegate? readNetworks = null, bool allowLoopbackFixture = true)
        {
            Func<IPAddress, bool>? localAddressOverride = allowLoopbackFixture ? _ => false : null;
            object?[] arguments = [null, saved, readNetworks, localAddressOverride];
            var dialog = (Form)Activator.CreateInstance(formType, InstanceMembers, null, arguments, null)!;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-20000, -20000);
            dialog.Show();
            return dialog;
        }

        void ScanLoopback(Form dialog)
        {
            ((TextBox)Field(dialog, "_range")).Text = "127.0.0.1";
            ((TextBox)Field(dialog, "_port")).Text = port.ToString();
            var scan = (Button)Field(dialog, "_scan");
            scan.PerformClick();
            WaitUntil(() => scan.Text == "扫描指定范围");
        }

        using (var dialog = OpenScan([], allowLoopbackFixture: false))
        {
            ScanLoopback(dialog);
            Check(((DataGridView)Field(dialog, "_results")).Rows.Count == 0 && !listener.Pending(),
                "An open port on a local address must be neither probed nor shown in results.");
        }

        using (var dialog = OpenScan([endpoint]))
        {
            var portInput = (TextBox)Field(dialog, "_port");
            var results = (DataGridView)Field(dialog, "_results");
            var scan = (Button)Field(dialog, "_scan");
            Check(portInput.Text == "3389" && results.Rows.Count == 0,
                "Opening discovery must preset TCP 3389 and wait for Start.");
            ((TextBox)Field(dialog, "_range")).Text = "127.0.0.1";
            portInput.Text = "0";
            scan.PerformClick();
            Check(((Label)Field(dialog, "_status")).Text.Contains("65535") && results.Rows.Count == 0,
                "Invalid port must be reported before scanning.");

            ScanLoopback(dialog);
            Check(results.Rows.Count == 1 && (string?)results.Rows[0].Tag == endpoint &&
                (string?)results.Rows[0].Cells[2].Value == "已保存",
                "A real TCP result must retain its custom port and identify a saved endpoint.");
            results.Rows[0].Selected = true;
            Check(!((Button)Field(dialog, "_save")).Enabled, "A saved device must not be overwritten from discovery.");
            var temporary = (Button)Field(dialog, "_temporary");
            Check(temporary.Enabled, "A saved endpoint must still allow a temporary connection.");
            temporary.PerformClick();
            Check(dialog.DialogResult == DialogResult.OK &&
                (bool)formType.GetProperty("ConnectTemporarily", InstanceMembers)!.GetValue(dialog)! &&
                (string?)formType.GetProperty("SelectedAddress", InstanceMembers)!.GetValue(dialog) == endpoint,
                "Temporary action did not return the selected endpoint.");
        }

        var networkReads = 0;
        var networkArrayType = networkType.MakeArrayType();
        Func<Array> readNextNetwork = () =>
        {
            var networks = Array.CreateInstance(networkType, 1);
            var address = networkReads++ == 0 ? "127.0.0.2" : "127.0.0.1";
            networks.SetValue(Network("vEthernet (测试交换机)", address, 32), 0);
            return networks;
        };
        var refreshNetworks = Expression.Lambda(typeof(Func<>).MakeGenericType(networkArrayType),
            Expression.Convert(Expression.Invoke(Expression.Constant(readNextNetwork)), networkArrayType)).Compile();

        using (var dialog = OpenScan([], refreshNetworks))
        {
            var networks = (ComboBox)Field(dialog, "_networks");
            Check(networks.Items.Count == 2 && networks.Items[1].ToString()!.Contains("127.0.0.2/32"),
                "Quick scan must list adapter-derived IPv4 choices.");
            networks.SelectedIndex = 1;
            ((TextBox)Field(dialog, "_port")).Text = port.ToString();
            var quickScan = (Button)Field(dialog, "_quickScan");
            quickScan.PerformClick();
            WaitUntil(() => quickScan.Text == "快速扫描");
            var results = (DataGridView)Field(dialog, "_results");
            Check(networkReads == 2 && networks.SelectedIndex == 1 &&
                networks.SelectedItem!.ToString()!.Contains("127.0.0.1/32") &&
                results.Rows.Count == 1 && (string?)results.Rows[0].Tag == endpoint,
                "Quick scan must reread the selected adapter after a DHCP change and use the new address.");
            results.Rows[0].Selected = true;
            var save = (Button)Field(dialog, "_save");
            Check(save.Enabled && ((Button)Field(dialog, "_temporary")).Enabled,
                "A new result must offer both temporary connection and save.");
            Directory.CreateDirectory(Path.Combine("outputs", "ui-review"));
            using (var screenshot = new Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(screenshot, new Rectangle(Point.Empty, dialog.Size));
                screenshot.Save(Path.Combine("outputs", "ui-review", "LanDiscovery.png"));
            }
            save.PerformClick();
            Check(dialog.DialogResult == DialogResult.OK &&
                !(bool)formType.GetProperty("ConnectTemporarily", InstanceMembers)!.GetValue(dialog)! &&
                (string?)formType.GetProperty("SelectedAddress", InstanceMembers)!.GetValue(dialog) == endpoint,
                "Save action did not return the selected endpoint for editing.");
        }

        listener.Stop();
        using (var dialog = OpenScan([]))
        {
            ScanLoopback(dialog);
            Check(((DataGridView)Field(dialog, "_results")).Rows.Count == 0 &&
                ((Label)Field(dialog, "_status")).Text.Contains("未发现"),
                "A closed port must not appear as a discovered device.");
        }

        var settingsType = AppAssembly.GetType("PureRDP.Configuration.ApplicationSettings")!;
        var appSettings = Activator.CreateInstance(settingsType, true)!;
        using var editor = (Form)Activator.CreateInstance(typeof(FormSettings), InstanceMembers, null,
            new object?[] { appSettings, null, Size.Empty, false, true }, null)!;
        editor.StartPosition = FormStartPosition.Manual;
        editor.Location = new Point(-20000, -20000);
        editor.ShowInTaskbar = false;
        editor.Show();
        var discovery = (Button)Field(editor, "_lanDiscoveryButton");
        var header = discovery.Parent!;
        var heading = header.Controls.OfType<Label>().Single(label => label.Text == "设备连接设置");
        var initialClientSize = editor.ClientSize;
        foreach (var width in new[] { editor.MinimumSize.Width, initialClientSize.Width })
        {
            editor.ClientSize = new Size(width, initialClientSize.Height);
            Application.DoEvents();
            Check(discovery.Visible && discovery.Right == header.ClientSize.Width - discovery.Margin.Right &&
                header.ClientRectangle.Contains(discovery.Bounds) && !discovery.Bounds.IntersectsWith(heading.Bounds),
                "Discovery must stay on the right side of the settings heading at every tested window width.");
        }
        using (var screenshot = new Bitmap(editor.Width, editor.Height))
        {
            editor.DrawToBitmap(screenshot, new Rectangle(Point.Empty, editor.Size));
            screenshot.Save(Path.Combine("outputs", "ui-review", "NewDeviceScanHeader.png"));
        }

        var hostname = (ComboBox)Field(editor, "cmbHostname");
        var username = (ComboBox)Field(editor, "cmbUsername");
        var password = (TextBox)Field(editor, "tbPassword");
        hostname.Text = "192.0.2.1";
        username.Text = "example-user";
        password.Text = "synthetic-password";
        var saveDraft = (Task)Invoke(editor, "ApplyLanSelectionAsync", "192.0.2.2:3390", false);
        WaitUntil(() => saveDraft.IsCompleted);
        saveDraft.GetAwaiter().GetResult();
        Check(hostname.Text == "192.0.2.2:3390" && editor.DialogResult == DialogResult.None &&
            username.Text.Length == 0 && password.Text.Length == 0,
            "Save must prefill the new address for confirmation without carrying credentials across hosts.");

        var temporaryDraft = (Task)Invoke(editor, "ApplyLanSelectionAsync", "192.0.2.3:3391", true);
        WaitUntil(() => temporaryDraft.IsCompleted);
        temporaryDraft.GetAwaiter().GetResult();
        var updated = editor.GetType().GetProperty("UpdatedSettings", InstanceMembers)!.GetValue(editor)!;
        var connection = settingsType.GetProperty("Connection", InstanceMembers)!.GetValue(updated)!;
        Check(editor.DialogResult == DialogResult.OK &&
            (bool)editor.GetType().GetProperty("ConnectRequested", InstanceMembers)!.GetValue(editor)! &&
            (string?)SettingsType.GetProperty("Hostname", InstanceMembers)!.GetValue(connection) == "192.0.2.3:3391" &&
            (string?)SettingsType.GetProperty("Username", InstanceMembers)!.GetValue(connection) == "Administrator" &&
            SettingsType.GetProperty("PromptForCredentials", InstanceMembers)!.GetValue(connection) is true &&
            !(bool)settingsType.GetProperty("CanAutoSaveDeviceProfile", InstanceMembers)!.GetValue(updated)!,
            "Temporary selection must use Administrator for a blank username and prompt without saving a device.");
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
