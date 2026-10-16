using System.Collections;
using System.Drawing.Imaging;
using System.Reflection;
using PureRDP.UI;

// Render actual application controls with fictional data, without a connection
// manager, network scanners, preview sessions, Save clicks or update checks.
internal static class Program
{
    private static readonly Assembly App = typeof(FormSettings).Assembly;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Type(string name) => App.GetType("PureRDP." + name)!;
    private static object New(string name, params object?[] args) => Activator.CreateInstance(Type(name), Members, null, args, null)!;
    private static object Field(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, Members)!.SetValue(target, value);
    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
    private static void Show(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
    }
    private static void Save(Form form, string folder, string name)
    {
        SanitizeVisiblePaths(form);
        form.PerformLayout();
        Application.DoEvents();
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
        Console.WriteLine($"Rendered {name}.png ({image.Width}x{image.Height}); fictional fixtures only.");
    }
    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void SanitizeVisiblePaths(Form form)
    {
        var replacements = new[] {
            (Path.GetTempPath(), @"C:\Users\ExampleUser\AppData\Local\Temp\"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"C:\Users\ExampleUser\AppData\Roaming"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"C:\Users\ExampleUser\AppData\Local"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"C:\Users\ExampleUser"),
            (AppContext.BaseDirectory, @"C:\Apps\PureRDP\"),
            (Directory.GetCurrentDirectory(), @"C:\Projects\PureRDP"),
        };
        foreach (var control in Descendants(form).Where(control => control.Visible))
        {
            var text = control.Text;
            foreach (var (actual, example) in replacements)
                if (!string.IsNullOrWhiteSpace(actual)) text = text.Replace(actual, example, StringComparison.OrdinalIgnoreCase);
            if (text != control.Text) control.Text = text;
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)C:\\Users\\(?!ExampleUser(?:\\|$))[^\\\s]+"))
                throw new InvalidOperationException("A non-example user path remains in the screenshot.");
        }
    }
    private static object Device(string title, string endpoint, string user, Color color, int minutes)
    {
        var defaults = Type("Configuration.ConnectionSettings").GetField("Default")!.GetValue(null)!;
        var device = Call(defaults, "Clone")!;
        Set(device, "DisplayName", title); Set(device, "Hostname", endpoint);
        Set(device, "Username", user); Set(device, "Domain", null);
        Set(device, "BadgeColor", color); Set(device, "LastConnected", DateTime.UtcNow.AddMinutes(-minutes));
        return device;
    }
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        var output = Path.GetFullPath(args.Length == 0 ? "outputs/documentation-screenshots" : args[0]);
        Directory.CreateDirectory(output);
        using (var pathProbe = new Form())
        {
            var probe = new Label { Dock = DockStyle.Fill, Text = Path.GetTempPath() + "screenshot.png\n" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) };
            pathProbe.Controls.Add(probe); Show(pathProbe);
            SanitizeVisiblePaths(pathProbe);
            if (!probe.Text.Contains(@"C:\Users\ExampleUser\AppData\Local\Temp\screenshot.png") ||
                !probe.Text.Contains(@"C:\Users\ExampleUser\AppData\Roaming"))
                throw new InvalidOperationException("Generic user/temporary path sanitization failed.");
            Console.WriteLine("PASS: user and temporary paths replaced with generic examples.");
        }
        var fixtures = new[] {
            Device("开发工作站", "192.0.2.10", "example-user", Color.FromArgb(128, 0, 255), 8),
            Device("测试服务器", "198.51.100.20:3390", "example-user", Color.FromArgb(37, 124, 75), 45),
            Device("运维跳板机", "rdp-gateway.example:3389", "example-user", Color.FromArgb(31, 115, 165), 120),
            Device("备用工作站", "203.0.113.30", "example-user", Color.FromArgb(190, 112, 25), 1440),
        };
        var appIcon = (Icon)App.GetType("PureRDP.Properties.Resources")!.GetProperty("AppIcon", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        using (var host = new Form { Text = "PureRDP · 功能演示（测试数据）", ClientSize = new Size(1160, 820), Icon = appIcon })
        using (var library = (Control)New("UI.ConnectionLibraryPanel"))
        {
            host.Controls.Add(library); Show(host);
            ((System.Windows.Forms.Timer)Field(library, "_qualityRefreshTimer")).Stop();
            ((TextBox)Field(library, "_searchBox")).Clear();
            var connections = (IList)Field(library, "_connections");
            connections.Clear(); foreach (var fixture in fixtures) connections.Add(fixture);
            ((IDictionary)Field(library, "_availabilityStates")).Clear();
            Call(library, "ApplyPreviewEnabled", true);
            Call(library, "ShowStatus", "演示数据（全部虚构） · 状态和网络指标仅作示例 · 未发起远程连接");
            var notice = (Label)Field(library, "_statusLabel");
            notice.ForeColor = Color.FromArgb(61, 96, 86);
            notice.BackColor = Color.FromArgb(228, 245, 234);
            foreach (var mode in new[] { "Cards", "List" })
            {
                SetField(library, "_viewMode", Enum.Parse(Type("Configuration.ConnectionLibraryViewMode"), mode));
                Call(library, "SetComboBoxViewMode"); Call(library, "RenderConnections");
                for (var index = 0; index < fixtures.Length; index++)
                    Call(library, "UpdateAvailability", fixtures[index].GetType().GetProperty("Hostname")!.GetValue(fixtures[index]),
                        Enum.ToObject(Type("Services.DeviceAvailabilityState"), index < 2 ? 1 : index == 2 ? 2 : 0));
                var cards = ((Control)Field(library, "_itemsPanel")).Controls.Cast<Control>().ToArray();
                for (var index = 0; index < 2; index++)
                    Call(cards[index], "SetConnectionQuality", New("Services.ConnectionQualitySnapshot", true,
                        Enum.ToObject(Type("Services.ConnectionQualityGrade"), index == 0 ? 4 : 3), index == 0 ? 48.6 : 18.2, index == 0 ? 12 : 36, 0.0));
                Save(host, output, mode == "Cards" ? "console-cards" : "console-list");
            }
        }
        var application = New("Configuration.ApplicationSettings");
        var draft = fixtures[0]; Set(draft, "Password", "documentation-fixture-only");
        Set(draft, "Width", 1920); Set(draft, "Height", 1080);
        Set(application, "Connection", draft);
        using (var device = (Form)New("UI.FormSettings", application, null, new Size(1920, 1080)))
        {
            device.ClientSize = new Size(1040, 1000);
            device.Icon = appIcon;
            Show(device);
            device.Text = "设备连接设置 · PureRDP（测试数据）";
            ((ComboBox)Field(device, "cmbHostname")).Items.Clear();
            ((ComboBox)Field(device, "cmbHostname")).Text = "192.0.2.10";
            ((ComboBox)Field(device, "cmbUsername")).Items.Clear();
            ((ComboBox)Field(device, "cmbUsername")).Text = "example-user";
            ((TextBox)Field(device, "tbPassword")).Text = "sample";
            ((TextBox)Field(device, "_deviceName")).Select(0, 0);
            ((ComboBox)Field(device, "cmbWidth")).Select(0, 0);
            ((ComboBox)Field(device, "cmbHeight")).Select(0, 0);
            Save(device, output, "device-settings");
        }
        var preferences = New("Configuration.GlobalSettings");
        var shortcuts = preferences.GetType().GetProperty("Shortcuts")!.GetValue(preferences)!;
        Set(shortcuts, "EffectMode", Enum.ToObject(shortcuts.GetType().GetProperty("EffectMode")!.PropertyType, 1));
        var monitoring = preferences.GetType().GetProperty("Monitoring")!.GetValue(preferences)!;
        Set(monitoring, "AvailabilityScanIntervalSeconds", 60);
        Set(monitoring, "ConnectionQualityIntervalSeconds", 3);
        using (var global = (Form)New("UI.GlobalSettingsForm", preferences))
        {
            global.ClientSize = new Size(1160, 900); Show(global);
            global.Text = "全局设置 · PureRDP（演示配置）";
            foreach (var entry in new[] { ("WindowSettingsPage", "global-windows"), ("ShortcutSettingsPage", "global-shortcuts"), ("DeviceMonitoringSettingsPage", "global-monitoring") })
            {
                var page = ((IList)Field(global, "_pages")).Cast<Control>().Single(item => item.GetType().Name == entry.Item1);
                Call(global, "SelectPage", page); Save(global, output, entry.Item2);
            }
        }
        if (Directory.GetFiles(output, "*.png").Length != 6) throw new InvalidOperationException("Expected six documentation screenshots.");
    }
}
