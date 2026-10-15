using System.Reflection;
using PureRDP.UI;

internal static class Program
{
    private static readonly Assembly App = typeof(FormSettings).Assembly;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static int _checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); _checks++; }
    private static object Call(string method, params object[] args) => App.GetType("PureRDP.Services.WindowsRdpHistoryService")!.GetMethod(method, Static)!.Invoke(null, args)!;
    private static object Field(object item, string name) => item.GetType().GetField(name, Instance)!.GetValue(item)!;
    [STAThread]
    private static void Main()
    {
        var values = new Dictionary<string, object?> {
            ["MRU10"] = "last.example", ["MRU2"] = "host.example:9850", ["MRU0"] = "192.0.2.18",
            ["MRU1"] = " HOST.EXAMPLE:9850 ", ["MRU3"] = "[2001:db8::1]:3389",
            ["MRU4"] = "2001:db8::2", ["MRU5"] = "bad host", ["MRU6"] = 123,
            ["MRU7"] = "host:0", ["MRU8"] = "", ["MRU9"] = null,
            ["UsernameHint"] = "ignored.example", ["MRU-1"] = "ignored.example", ["MRUX"] = "ignored.example" };
        var addresses = (string[])Call("ParseAddresses", values);
        Check(addresses.Length == 5, "invalid values filtered and duplicates removed");
        Check(addresses[0] == "192.0.2.18", "most recent first");
        Check(addresses[1] == "HOST.EXAMPLE:9850", "port and trimmed spelling retained");
        Check(addresses[2] == "[2001:db8::1]:3389", "bracket IPv6 retained");
        Check(addresses[3] == "2001:db8::2", "plain IPv6 retained");
        Check(addresses[4] == "last.example", "MRU indexes sort numerically");
        Check(((string[])Call("ParseAddresses", new Dictionary<string, object?>())).Length == 0, "empty history");
        Check(((string[])Call("UsernameHintTargets", "host.example:9850")).SequenceEqual(new[] { "host.example:9850", "host.example" }), "username hint endpoint before host fallback");
        Check(((string[])Call("UsernameHintTargets", "2001:db8::2")).SequenceEqual(new[] { "2001:db8::2" }), "plain IPv6 username target not truncated");
        Check(((string[])Call("UsernameHintTargets", "[2001:db8::2]:3389")).Last() == "2001:db8::2", "IPv6 username hint variants");
        var additional = (string[])Call("AdditionalAddresses", new string?[] { " host.example:9850 ", null }, addresses);
        Check(additional.Length == 4 && !additional.Contains("HOST.EXAMPLE:9850"), "merge prefers existing PureRDP profiles");
        // Read the real current-user hive without logging addresses or modifying history.
        var native = (string[])Call("LoadAddresses");
        Check(native.Distinct(StringComparer.OrdinalIgnoreCase).Count() == native.Length, "live registry read deduplicated");
        var config = Activator.CreateInstance(App.GetType("PureRDP.Configuration.ApplicationSettings")!, true)!;
        using var form = (Form)Activator.CreateInstance(typeof(FormSettings), Instance, null, [config, null, new Size(1920, 1080)], null)!;
        var hostname = (ComboBox)Field(form, "cmbHostname");
        var username = (ComboBox)Field(form, "cmbUsername");
        var name = (TextBox)Field(form, "_deviceName");
        form.CreateControl(); _ = form.Handle;
        hostname.Text = "typed.example:9850"; username.Text = "TypedUser"; name.Text = "Unsaved name";
        var task = (Task)typeof(FormSettings).GetMethod("QueueRecentConnectionsRefreshAsync", Instance)!.Invoke(form, null)!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Check(task.IsCompletedSuccessfully, "background history refresh completed");
        Check(hostname.Text == "typed.example:9850" && username.Text == "TypedUser" && name.Text == "Unsaved name", "background read preserves in-progress edits");
        foreach (var address in native)
            Check(hostname.Items.Cast<object>().Any(item => string.Equals(item.ToString(), address, StringComparison.OrdinalIgnoreCase)), "native address present in dropdown");
        hostname.Items.Add("migration.example:3389"); hostname.SelectedIndex = hostname.Items.Count - 1;
        typeof(FormSettings).GetMethod("cmbHostname_SelectionChangeCommitted", Instance)!.Invoke(form, [hostname, EventArgs.Empty]);
        Check(name.Text == "Unsaved name" && username.Text == "TypedUser", "native selection does not load default profile");
        var populate = typeof(FormSettings).GetMethod("PopulateNativeUsername", Instance)!;
        populate.Invoke(form, ["DOMAIN\\SavedUser", "HintUser", "TypedUser"]);
        Check(username.Text == "DOMAIN\\SavedUser", "saved credential username takes precedence");
        populate.Invoke(form, [null, "HintUser", username.Text]);
        Check(username.Text == "HintUser", "history username fallback");
        populate.Invoke(form, [null, null, username.Text]);
        Check(username.Text == "HintUser", "missing history user keeps manual field");
        username.Text = "LaterEdit";
        populate.Invoke(form, ["SavedUser", null, "HintUser"]);
        Check(username.Text == "LaterEdit", "late lookup cannot override manual edit");
        username.Text = "";
        populate.Invoke(form, ["SavedUser", null, "HintUser"]);
        Check(username.Text == "", "late lookup cannot override manual clearing");
        Check(((TextBox)Field(form, "tbPassword")).Text == "", "no plaintext password imported");
        Console.WriteLine($"PASS: {_checks} history checks; current-user registry read only, no credentials copied or settings saved.");
    }
}
