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
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--native-fixture")
        {
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new {
                Arguments = args.Skip(2).ToArray(),
                Guard = Environment.GetEnvironmentVariable("PURERDP_NATIVE_HANDOFF_DEPTH"),
                Debugged = IsDebuggerPresent(), Directory = Environment.CurrentDirectory
            }));
            Environment.ExitCode = 37;
            return;
        }
        TestNativeCompatibility();
        TestRemoteStartup();
        TestEncryptedRdpPassword();
        TestProxy();
        TestProxyRegistry();
        TestRelayAcknowledgement();
        var associationType = App.GetType("PureRDP.Services.RdpFileAssociationService")!;
        var sampleExecutable = Path.Combine(Path.GetTempPath(), "PureRDP 示例 folder", "PureRDP.exe");
        var registrationPlan = (System.Collections.IEnumerable)associationType.GetMethod("BuildRegistrationPlan", Static)!
            .Invoke(null, [sampleExecutable])!;
        var plannedValues = registrationPlan.Cast<object>().Select(entry => new {
            Key = (string)entry.GetType().GetProperty("KeyPath")!.GetValue(entry)!,
            Name = (string)entry.GetType().GetProperty("ValueName")!.GetValue(entry)!,
            Value = (string)entry.GetType().GetProperty("Value")!.GetValue(entry)! }).ToArray();
        Check(plannedValues.Where(entry => entry.Key.EndsWith(@"shell\open\command")).All(entry =>
            entry.Value == $"\"{sampleExecutable}\" \"%1\""),
            "Association commands must quote executable and file paths containing spaces and Unicode.");
        Check(plannedValues.Any(entry => entry.Key == @"Software\RegisteredApplications" && entry.Name == "PureRDP") &&
            plannedValues.Any(entry => entry.Key.EndsWith(@"Capabilities\FileAssociations") && entry.Name == ".rdp" && entry.Value == "PureRDP.RdpFile"),
            "Default Apps discovery must include application registration and its RDP capability.");
        Check(plannedValues.Any(entry => entry.Key == @"Software\Classes\.rdp\OpenWithProgids" && entry.Name == "PureRDP.RdpFile"),
            "Open With must discover the PureRDP file handler.");
        Check(plannedValues.All(entry => !entry.Key.Contains("UserChoice", StringComparison.OrdinalIgnoreCase) &&
            entry.Key != @"Software\Classes\.rdp" && !entry.Key.Contains("mstsc", StringComparison.OrdinalIgnoreCase)),
            "Registration must not overwrite Windows user choices, extension ownership or the native RDP handler.");
        Check((string)associationType.GetField("DefaultAppsUri", Static)!.GetRawConstantValue()! ==
            "ms-settings:defaultapps?registeredAppUser=PureRDP", "Default selection must target the current-user Windows settings page.");
        var routingDirectory = Path.Combine(Path.GetTempPath(), "purerdp-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(routingDirectory);
        try
        {
            var sourcePath = Path.Combine(routingDirectory, "session.rdp");
            var exportedPath = Path.Combine(routingDirectory, "export.rdp");
            var fixtureCookie = "Cookie: msts=fixture-only:route=example  ";
            var fileContent = "full address:s:192.0.2.18:3391\r\nLoAdBaLaNcEiNfO:s:" + fixtureCookie + "\r\n";
            File.WriteAllText(sourcePath, fileContent, System.Text.Encoding.Unicode);
            var routingFileService = App.GetType("PureRDP.Services.RdpFileService")!;
            var imported = routingFileService.GetMethod("ReadFromFile", Static)!.Invoke(null, [sourcePath])!;
            var ticketProperty = imported.GetType().GetProperty("LoadBalanceInfo")!;
            Check((string?)ticketProperty.GetValue(imported) == fixtureCookie,
                "Import must retain the complete routing cookie, including colons and trailing spaces.");
            Check((bool?)imported.GetType().GetProperty("PreserveImportedDesktop")!.GetValue(imported) == true,
                "Broker compatibility must also apply when the assistant omits explicit desktop dimensions.");
            var cloned = imported.GetType().GetMethod("Clone")!.Invoke(imported, null)!;
            Check((string?)ticketProperty.GetValue(cloned) == fixtureCookie,
                "Session-window profile clones must retain the routing cookie in memory.");
            var parsed = App.GetType("PureRDP.CommandLine.CommandLineOptions")!.GetMethod("Parse", Static)!
                .Invoke(null, [new[] { sourcePath }])!;
            var parsedProfile = parsed.GetType().GetProperty("Connection")!.GetValue(parsed)!;
            Check((string?)ticketProperty.GetValue(parsedProfile) == fixtureCookie,
                "Opening a positional RDP file must retain its routing data through command-line merges.");
            routingFileService.GetMethod("SaveToFile", Static)!.Invoke(null, [imported, exportedPath]);
            var exportedText = File.ReadAllText(exportedPath, System.Text.Encoding.Unicode);
            Check(!exportedText.Contains(fixtureCookie) && !exportedText.Contains("loadbalanceinfo", StringComparison.OrdinalIgnoreCase),
                "Saving a reusable profile must not persist an imported ephemeral routing ticket.");
            Check(!System.Text.Json.JsonSerializer.Serialize(imported, imported.GetType()).Contains(fixtureCookie),
                "JSON export must not contain an ephemeral routing ticket.");
            var ordinary = routingFileService.GetMethod("ReadFromFile", Static)!.Invoke(null, [exportedPath])!;
            Check(ticketProperty.GetValue(ordinary) == null, "An ordinary profile must not acquire a routing ticket.");
            Check(File.ReadAllText(sourcePath, System.Text.Encoding.Unicode) == fileContent,
                "Import and export must leave the source session file unchanged.");
        }
        finally
        {
            // Only delete the two synthetic files created by this test.
            foreach (var fixtureName in new[] { "session.rdp", "export.rdp" })
            {
                var path = Path.Combine(routingDirectory, fixtureName);
                if (File.Exists(path)) File.Delete(path);
            }
            Directory.Delete(routingDirectory);
        }
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
        // Own a unique synthetic credential only; never enumerate/read real
        // passwords or replace a user's existing TERMSRV entry.
        var credentialType = App.GetType("PureRDP.Services.RdpCredentialService")!;
        object? Credential(string method, params object?[] args)
            => credentialType.GetMethod(method, Static)!.Invoke(null, args);
        var testHost = "purerdp-regression-" + Guid.NewGuid().ToString("N") + ".invalid:3391";
        var secret = "fixture-only-密码-123";
        var vaultAvailable = true;
        try
        {
            Credential("SaveConnectionPassword", testHost, "EXAMPLE\\Demo", secret);
            for (var attempt = 0; attempt < 3; attempt++)
                Check((string?)Credential("ReadConnectionPassword", testHost, "EXAMPLE\\Demo") == secret,
                    "Repeated connections must reload the saved password.");
            Check(Credential("ReadConnectionPassword", testHost, "OtherUser") == null,
                "A different account must not reuse a previous account's secret.");
            Check(Credential("ReadConnectionPassword", testHost.Replace(":3391", ":3392"), "EXAMPLE\\Demo") == null,
                "Different endpoint ports must not share private credentials.");
            Check((string?)Credential("CredentialsExist", testHost) == "EXAMPLE\\Demo",
                "Settings must recognize the protected saved credential.");
            var rdpPath = Path.Combine(Path.GetTempPath(), "purerdp-password-" + Guid.NewGuid().ToString("N") + ".rdp");
            try
            {
                var profileType = App.GetType("PureRDP.Configuration.ConnectionSettings")!;
                var profile = Activator.CreateInstance(profileType, true)!;
                profileType.GetProperty("Hostname")!.SetValue(profile, testHost);
                profileType.GetProperty("Username")!.SetValue(profile, "EXAMPLE\\Demo");
                profileType.GetProperty("Password")!.SetValue(profile, secret);
                var rdpService = App.GetType("PureRDP.Services.RdpFileService")!;
                rdpService.GetMethod("SaveToFile", Static)!.Invoke(null, [profile, rdpPath]);
                var reloaded = rdpService.GetMethod("ReadFromFile", Static)!.Invoke(null, [rdpPath])!;
                Check(profileType.GetProperty("Password")!.GetValue(reloaded) == null,
                    "Reloaded device must not contain a plaintext password.");
                Check(!File.ReadAllText(rdpPath, System.Text.Encoding.Unicode).Contains(secret),
                    "Saved device file must not expose the secret.");
                Check((string?)Credential("ReadConnectionPassword", profileType.GetProperty("Hostname")!.GetValue(reloaded),
                    profileType.GetProperty("Username")!.GetValue(reloaded)) == secret,
                    "A reopened device must still resolve its protected password.");
            }
            finally { if (File.Exists(rdpPath)) File.Delete(rdpPath); }
            Credential("SaveConnectionPassword", testHost, "EXAMPLE\\Demo", "replacement-fixture");
            Check((string?)Credential("ReadConnectionPassword", testHost, "EXAMPLE\\Demo") == "replacement-fixture",
                "An explicit replacement password must persist.");
        }
        catch (TargetInvocationException exception) when
            (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1312 })
        {
            // Service/CI tokens may have no Credential Manager logon session.
            // This is an explicit capability skip, not a passed vault check.
            vaultAvailable = false;
            Console.WriteLine("SKIP: native credential-vault checks unavailable (Win32 1312); rerun under an interactive user token.");
        }
        finally { Credential("RemoveConnectionPassword", testHost); }
        if (vaultAvailable)
            Check(Credential("ReadConnectionPassword", testHost, "EXAMPLE\\Demo") == null,
                "Synthetic credential must be removed after regression.");
        Console.WriteLine($"PASS: {_checks} history/credential checks; unique synthetic credential removed, no real credentials modified.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    private static void TestNativeCompatibility()
    {
        var compatibility = App.GetType("PureRDP.Services.RdpCompatibilityService")!;
        var launcher = App.GetType("PureRDP.Services.NativeMstscLauncher")!;
        object? Plan(bool prefer, params string[] arguments) => compatibility.GetMethod("Plan", Static)!.Invoke(null, [arguments, prefer]);
        string[] Arguments(object plan) => (string[])plan.GetType().GetProperty("Arguments", Instance)!.GetValue(plan)!;
        var directory = Path.Combine(Path.GetTempPath(), "purerdp-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "远程 session.rdp");
        var report = Path.Combine(directory, "synthetic-child.json");
        try
        {
            var ordinary = "full address:s:example.invalid\r\nusername:s:fixture-user\r\n";
            File.WriteAllText(file, ordinary, System.Text.Encoding.Unicode);
            Check(Plan(false, file) == null, "A covered ordinary file must remain embedded.");
            Check(Plan(false, "--silent") == null && Plan(true, "--hostname=example.invalid") == null,
                "Compatibility must not redirect PureRDP's own console, silent startup or CLI.");
            Check(Arguments(Plan(true, file)!).SequenceEqual(new[] { file }), "Explicit native preference must retain the source file path.");
            Check(Plan(false, "--mstsc-proxy", "mstsc.exe", "/v:example.invalid", "/w:1000", "/h:700", "/f") == null,
                "Covered ordinary MSTSC options must remain embedded.");
            foreach (var options in new[] {
                new[] { "/g:gateway.example.invalid", "/v:example.invalid" }, new[] { "/public", "/v:example.invalid" },
                new[] { "/span", "/v:example.invalid" }, new[] { "/multimon", "/v:example.invalid" },
                new[] { "/l" }, new[] { "/restrictedAdmin", "/v:example.invalid" },
                new[] { "/remoteGuard", "/v:example.invalid" },
                new[] { "/shadow:17", "/control", "/noConsentPrompt", "/v:example.invalid" },
                new[] { "/?" }, new[] { "/edit", file }, new[] { "/future-vendor-option=synthetic-private" },
                new[] { "/future-vendor-option", "\"literal-quoted-value\"", "C:\\trailing\\" }
            })
            {
                var plan = Plan(false, new[] { "--mstsc-proxy", "mstsc.exe" }.Concat(options).ToArray());
                Check(plan != null && Arguments(plan).SequenceEqual(options), "Native-only options must pass through without deletion or authentication downgrade.");
                Check(!plan!.ToString()!.Contains("synthetic-private"), "Native plan diagnostics must omit argument payloads.");
            }
            foreach (var field in new[] {
                "gatewayhostname:s:gateway.example.invalid", "gatewayaccesstoken:s:fixture-ticket", "remoteapplicationmode:i:1",
                "remoteapplicationprogram:s:||fixture", "selectedmonitors:s:0,1\r\nuse multimon:i:1", "signscope:s:Full Address",
                "signature:s:fixture-signature", "pcb:s:fixture-ticket", "enablerdsaadauth:i:1"
            })
            {
                var original = ordinary + field + "\r\n";
                File.WriteAllText(file, original, System.Text.Encoding.Unicode);
                var plan = Plan(false, "--mstsc-proxy", "mstsc.exe", file);
                Check(plan != null && Arguments(plan).SequenceEqual(new[] { file }), "Active native-only fields must preserve native parsing: " + field.Split(':')[0]);
                Check(File.ReadAllText(file, System.Text.Encoding.Unicode) == original, "Native routing must leave every source field and signature unchanged.");
            }
            File.WriteAllText(file, "full address:s:example.invalid\r\n", System.Text.Encoding.Unicode);
            Check(Plan(false, file) == null, "Missing credentials alone must not bypass the embedded client.");
            foreach (var endpoint in new[] { ("example.invalid", "example.invalid:3391"),
                ("example.invalid:3392", "example.invalid:3392"), ("[2001:db8::7]", "[2001:db8::7]:3391") })
            {
                File.WriteAllText(file, "full address:s:" + endpoint.Item1 + "\r\nserver port:i:3391\r\n", System.Text.Encoding.Unicode);
                var profile = App.GetType("PureRDP.Services.RdpFileService")!.GetMethod("ReadFromFile", Static)!.Invoke(null, [file])!;
                Check((string?)profile.GetType().GetProperty("Hostname")!.GetValue(profile) == endpoint.Item2 && Plan(false, file) == null,
                    "A separate standard server port must work inside PureRDP and must not replace an explicit address port.");
            }
            foreach (var handoff in new[] {
                "loadbalanceinfo:s:fixture-routing-cookie\r\n",
                "username:s:fixture-user\r\npassword 51:b:00\r\n",
                "alternate shell:s:fixture-startup-token\r\n"
            })
            {
                File.WriteAllText(file, "full address:s:example.invalid\r\n" + handoff +
                    "winposstr:s:0,1,0,0,1600,900\r\nremoteapplicationmode:i:0\r\ngatewayhostname:s:\r\n" +
                    "gatewayusagemethod:i:4\r\nselectedmonitors:s:\r\nnetworkautodetect:i:1\r\n" +
                    "disable wallpaper:i:0\r\nunknown vendor field:s:\r\n", System.Text.Encoding.Unicode);
                Check(Plan(false, "--mstsc-proxy", "mstsc.exe", file) == null,
                    "NSFOCUS routing, ORAssistant encrypted-password and H3C startup handoffs must try embedded despite routine or empty fields.");
            }
            TestDeferredFallback(file);
            var executable = Environment.ProcessPath!;
            var sample = new[] { "", "space and 中文", "quote\"inside", "C:\\trailing directory\\", "slashes\\\\\"quote", "&|<>;$(literal)", "--credential=synthetic-only" };
            var childArgs = new[] { "--native-fixture", report }.Concat(sample).ToArray();
            var originalGuard = Environment.GetEnvironmentVariable("PURERDP_NATIVE_HANDOFF_DEPTH");
            var code = (int)launcher.GetMethod("RunDetachedProcess", Static)!.Invoke(null, [executable, childArgs, null])!;
            Check(code == 37, "The native helper must wait for the child and preserve its exit code.");
            using var childReport = System.Text.Json.JsonDocument.Parse(File.ReadAllText(report));
            Check(childReport.RootElement.GetProperty("Arguments").EnumerateArray().Select(item => item.GetString()).SequenceEqual(sample),
                "Native launch must round-trip empty, Unicode, quoted, trailing-backslash and literal shell characters without shell interpretation.");
            Check(!childReport.RootElement.GetProperty("Debugged").GetBoolean(), "The real child must resume detached, not under an ongoing debugger.");
            Check(childReport.RootElement.GetProperty("Guard").GetString() == "1" &&
                Environment.GetEnvironmentVariable("PURERDP_NATIVE_HANDOFF_DEPTH") == originalGuard,
                "The recursion guard belongs only to the child environment, never the console's global environment.");
            Check(childReport.RootElement.GetProperty("Directory").GetString() == Environment.CurrentDirectory,
                "Native handoff must retain the launching working directory for relative paths.");
            var resolved = (string)launcher.GetMethod("ResolveSystemExecutable", Static)!.Invoke(null, [Path.Combine(directory, "mstsc.exe")])!;
            Check(resolved == Path.Combine(Environment.SystemDirectory, "mstsc.exe"), "Untrusted executable paths must never replace the actual system client.");
            TestNativeHelp(launcher);
        }
        finally
        {
            foreach (var path in new[] { file, report }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void TestDeferredFallback(string file)
    {
        var fallbackType = App.GetType("PureRDP.Services.NativeRdpFallback")!;
        var applicationType = App.GetType("PureRDP.Configuration.ApplicationSettings")!;
        var application = Activator.CreateInstance(applicationType)!;
        applicationType.GetProperty("RdpFilePath")!.SetValue(application, file);
        var source = File.ReadAllBytes(file);
        object Capture() => fallbackType.GetMethod("Capture", Static)!.Invoke(null,
            [application, new[] { file, "--width=1000", "--height=700", "--fullscreen" }])!;
        bool Claim(object request) => (bool)fallbackType.GetMethod("TryClaim", Instance)!.Invoke(request, null)!;
        using var request = (IDisposable)Capture();
        var backup = file + ".test-original";
        File.Move(file, backup);
        string? transient = null;
        try
        {
            Check(Claim(request) && !Claim(request), "Only the first actual failure may claim a fallback.");
            // Construct the exact internal Action<T> type without executing a
            // real remote login. The callback inspects only synthetic bytes.
            var inspect = typeof(Program).GetMethod(nameof(InspectFallbackLaunch), Static)!;
            _fallbackExpected = source;
            _fallbackVisited = null;
            var callbackType = typeof(Action<>).MakeGenericType(App.GetType("PureRDP.Services.NativeMstscLaunch")!);
            var callback = Delegate.CreateDelegate(callbackType, inspect);
            fallbackType.GetMethod("Run", Instance)!.Invoke(request, new object?[] { callback });
            transient = _fallbackVisited;
            Check(transient != null && !File.Exists(transient) && !Directory.Exists(Path.GetDirectoryName(transient)),
                "Fallback must remove its own transient file and directory after the native session ends.");
            Check(!Claim(request), "Completed fallback must never recursively retry itself.");
        }
        finally { File.Move(backup, file); _fallbackExpected = null; }
        using var succeeded = (IDisposable)Capture();
        fallbackType.GetMethod("CancelIfPending", Instance)!.Invoke(succeeded, null);
        Check(!Claim(succeeded), "Successful login or user cancellation must permanently suppress fallback for this launch.");
    }
    private static byte[]? _fallbackExpected;
    private static string? _fallbackVisited;
    private static void InspectFallbackLaunch(object launch)
    {
        var arguments = (string[])launch.GetType().GetProperty("Arguments", Instance)!.GetValue(launch)!;
        _fallbackVisited = arguments[0];
        Check(File.ReadAllBytes(arguments[0]).SequenceEqual(_fallbackExpected!),
            "A helper-deleted handoff must be reconstructed byte-for-byte only when fallback runs.");
        Check(arguments.Skip(1).SequenceEqual(new[] { "/w:1000", "/h:700", "/f" }),
            "The native fallback must retain explicit display overrides.");
    }

    private delegate bool WindowEnumerator(IntPtr window, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowEnumerator callback, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    private static void TestNativeHelp(Type launcher)
    {
        // Real Windows client, /? only: no host, file, credentials or session.
        // The entire test process is on the existing isolated desktop.
        uint created = 0;
        var closedWindow = false;
        var executable = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
        Action<uint> started = process => Volatile.Write(ref created, process);
        var run = Task.Run(() => (int)launcher.GetMethod("RunDetachedProcess", Static)!
            .Invoke(null, [executable, new[] { "/?" }, started])!);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!run.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(12))
            {
                var ownProcess = Volatile.Read(ref created);
                if (ownProcess != 0)
                    EnumWindows((window, _) => {
                        GetWindowThreadProcessId(window, out var process);
                        if (process == ownProcess)
                        {
                            // Only windows of the exact child we just created.
                            PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                            closedWindow = true;
                        }
                        return true;
                    }, IntPtr.Zero);
                Thread.Sleep(50);
            }
            Check(run.IsCompletedSuccessfully && created != 0 && closedWindow,
                "Actual system MSTSC help must open and close on the isolated desktop without proxy recursion or any RDP connection.");
            Console.WriteLine("Native MSTSC help exit code: " + run.Result);
            Check(run.Result is 0 or 1, "Native help dismissal must return a normal status, not crash or remain attached. Exit=" + run.Result);
        }
        finally
        {
            if (!run.IsCompleted && created != 0)
            {
                // Failure cleanup only for this exact freshly spawned /? child.
                try { using var child = System.Diagnostics.Process.GetProcessById((int)created); child.Kill(); }
                catch (ArgumentException) { }
                run.Wait(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static void TestRemoteStartup()
    {
        var profileType = App.GetType("PureRDP.Configuration.ConnectionSettings")!;
        var fileService = App.GetType("PureRDP.Services.RdpFileService")!;
        var directory = Path.Combine(Path.GetTempPath(), "purerdp-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "session.rdp");
        var export = Path.Combine(directory, "export.rdp");
        const string startup = "  fixture-remote-command --ticket=example:token  ";
        const string workdir = "C:\\fixture remote directory ";
        try
        {
            var original = "full address:s:example.invalid\r\nAlTeRnAtE ShElL:s:" + startup +
                "\r\nshell working directory:s:" + workdir + "\r\nnegotiate security layer:i:1\r\nenablecredsspsupport:i:0\r\nprompt for credentials:i:0\r\n";
            File.WriteAllText(source, original, System.Text.Encoding.Unicode);
            var imported = fileService.GetMethod("ReadFromFile", Static)!.Invoke(null, [source])!;
            var shell = profileType.GetProperty("AlternateShell")!;
            var working = profileType.GetProperty("ShellWorkingDirectory")!;
            Check((string?)shell.GetValue(imported) == startup && (string?)working.GetValue(imported) == workdir,
                "Remote startup import must preserve case-insensitive fields, colons, quotes and spaces without interpreting them.");
            Check((bool?)profileType.GetProperty("NegotiateSecurityLayer")!.GetValue(imported) == true &&
                (bool?)profileType.GetProperty("EnableCredSsp")!.GetValue(imported) == false,
                "External files must retain their explicit security negotiation settings.");
            Check((bool?)profileType.GetProperty("PreserveImportedDesktop")!.GetValue(imported) == true,
                "Remote startup broker sessions must retain compatible desktop sizing.");
            var clone = profileType.GetMethod("Clone")!.Invoke(imported, null)!;
            Check((string?)shell.GetValue(clone) == startup, "Session cloning must retain the remote startup payload.");
            var parsed = App.GetType("PureRDP.CommandLine.CommandLineOptions")!.GetMethod("Parse", Static)!
                .Invoke(null, [new[] { "--proxy-connection", "--rdp-file=" + source }])!;
            var merged = parsed.GetType().GetProperty("Connection")!.GetValue(parsed)!;
            Check((string?)shell.GetValue(merged) == startup && (string?)working.GetValue(merged) == workdir,
                "MSTSC proxy command-line merges must retain remote startup properties.");
            var application = Activator.CreateInstance(App.GetType("PureRDP.Configuration.ApplicationSettings")!)!;
            application.GetType().GetProperty("Connection")!.SetValue(application, imported);
            Check(!(bool)application.GetType().GetProperty("CanAutoSaveDeviceProfile", Instance)!.GetValue(application)!,
                "Startup tickets must block implicit device auto-save even without an external marker.");
            Check(!System.Text.Json.JsonSerializer.Serialize(imported, profileType).Contains("fixture-remote"),
                "JSON must not contain remote startup tickets or working directories.");
            var common = App.GetType("PureRDP.Services.RdpProxySettingsService")!.GetMethod("Sanitize", Static)!.Invoke(null, [imported])!;
            Check(shell.GetValue(common) == null && working.GetValue(common) == null,
                "Shared connection templates must not acquire remote startup payloads.");
            File.WriteAllText(export, original + "custom flag:i:42\r\n", System.Text.Encoding.Unicode);
            fileService.GetMethod("SaveToFile", Static)!.Invoke(null, [imported, export]);
            var exported = File.ReadAllText(export, System.Text.Encoding.Unicode);
            Check(!exported.Contains(startup) && !exported.Contains("alternate shell", StringComparison.OrdinalIgnoreCase) &&
                !exported.Contains("shell working directory", StringComparison.OrdinalIgnoreCase) && exported.Contains("custom flag:i:42"),
                "Updating an export must remove startup payloads while retaining unrelated fields.");
            Check(File.ReadAllText(source, System.Text.Encoding.Unicode) == original, "The assistant's original file must remain unchanged.");
        }
        finally
        {
            foreach (var path in new[] { source, export }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void TestEncryptedRdpPassword()
    {
        var profileType = App.GetType("PureRDP.Configuration.ConnectionSettings")!;
        var fileService = App.GetType("PureRDP.Services.RdpFileService")!;
        var passwords = App.GetType("PureRDP.Services.RdpFilePasswordService")!;
        object Import(string path) => fileService.GetMethod("ReadFromFile", Static)!.Invoke(null, [path])!;
        string? Read(object profile) => (string?)passwords.GetMethod("GetPassword", Static)!.Invoke(null, [profile]);
        const string fixtureSecret = "  fixture-Unicode-\u03a9-password  ";
        var clear = System.Text.Encoding.Unicode.GetBytes(fixtureSecret + "\0");
        var encrypted = System.Security.Cryptography.ProtectedData.Protect(clear, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var encoded = Convert.ToHexString(encrypted);
        var directory = Path.Combine(Path.GetTempPath(), "purerdp-encrypted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "assistant.tmp");
        var export = Path.Combine(directory, "export.rdp");
        try
        {
            var original = "full address:s:example.invalid\r\nusername:s:fixture-user\r\ndesktopwidth:i:1600\r\ndesktopheight:i:900\r\nPASSWORD 51:b:" + encoded + "\r\nenablecredsspsupport:i:0\r\n";
            File.WriteAllText(source, original, System.Text.Encoding.Unicode);
            var imported = Import(source);
            Check((bool?)profileType.GetProperty("PreserveImportedDesktop")!.GetValue(imported) == true,
                "Encrypted assistant files with explicit dimensions must preserve their native desktop geometry by default.");
            var cipherProperty = profileType.GetProperty("EncryptedPassword51")!;
            var plainProperty = profileType.GetProperty("Password")!;
            Check((string?)cipherProperty.GetValue(imported) == encoded && plainProperty.GetValue(imported) == null,
                "File reading must retain the DPAPI blob without decrypting it into profile properties.");
            Check(Read(imported) == fixtureSecret, "Password import must preserve Unicode and meaningful leading/trailing spaces.");
            Check(plainProperty.GetValue(imported) == null, "Decryption must not write plaintext back onto the profile.");
            var cloned = profileType.GetMethod("Clone")!.Invoke(imported, null)!;
            Check(Read(cloned) == fixtureSecret, "Session clones must carry the original encrypted credentials.");
            var options = App.GetType("PureRDP.CommandLine.CommandLineOptions")!;
            var parsed = options.GetMethod("Parse", Static)!.Invoke(null, [new[] { "--proxy-connection", "--rdp-file=" + source }])!;
            var profile = parsed.GetType().GetProperty("Connection")!.GetValue(parsed)!;
            Check(Read(profile) == fixtureSecret, "A .tmp proxy import must deliver its encrypted password through command-line merging.");
            Check(!(bool)parsed.GetType().GetProperty("CanAutoSaveDeviceProfile", Instance)!.GetValue(parsed)!,
                "A broker session containing an encrypted password must not auto-save a device.");
            plainProperty.SetValue(cloned, "explicit-fixture-password");
            Check(Read(cloned) == "explicit-fixture-password", "An explicitly entered password must override an imported password.");
            var json = System.Text.Json.JsonSerializer.Serialize(cloned, profileType);
            Check(!json.Contains(encoded) && !json.Contains("explicit-fixture-password"), "JSON must omit both plaintext and imported encrypted passwords.");
            File.WriteAllText(export, "full address:s:example.invalid\r\npassword 51:b:" + encoded + "\r\npassword:s:" + fixtureSecret + "\r\ncustom flag:i:42\r\n", System.Text.Encoding.Unicode);
            fileService.GetMethod("SaveToFile", Static)!.Invoke(null, [imported, export]);
            var exported = File.ReadAllText(export, System.Text.Encoding.Unicode);
            Check(!exported.Contains(encoded) && !exported.Contains(fixtureSecret) && !exported.Contains("password", StringComparison.OrdinalIgnoreCase),
                "Even an existing export must not preserve an earlier password field as an unknown property.");
            Check(exported.Contains("custom flag:i:42"), "Removing secret fields must preserve unrelated RDP properties.");
            Check(File.ReadAllText(source, System.Text.Encoding.Unicode) == original, "Import and export must leave the assistant's source file unchanged.");
            var common = App.GetType("PureRDP.Services.RdpProxySettingsService")!.GetMethod("Sanitize", Static)!.Invoke(null, [imported])!;
            Check(cipherProperty.GetValue(common) == null, "Shared proxy templates must exclude the imported credential.");
            foreach (var invalid in new[] { "not-hex", "0", "00", new string('A', 131074) })
            {
                cipherProperty.SetValue(imported, invalid);
                try { Read(imported); Check(false, "Invalid encrypted data must not silently fall back to saved credentials."); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
                { Check(!error.InnerException.Message.Contains(fixtureSecret) && !error.InnerException.Message.Contains(encoded), "Credential failures must not expose data in diagnostics."); }
            }
            var foreignEntropy = System.Security.Cryptography.ProtectedData.Protect(clear, new byte[] { 7, 9 }, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            cipherProperty.SetValue(imported, Convert.ToHexString(foreignEntropy));
            try { Read(imported); Check(false, "An undecryptable blob must produce an explicit failure."); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { _checks++; }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(foreignEntropy); }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(clear);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(encrypted);
            foreach (var path in new[] { source, export }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void TestProxy()
    {
        var proxy = App.GetType("PureRDP.Services.MstscProxyService")!;
        var executable = Path.Combine(Path.GetTempPath(), "proxy 示例", "PureRDP.exe");
        Check((string)proxy.GetMethod("BuildCommand", Static)!.Invoke(null, [executable])! == $"\"{executable}\" --mstsc-proxy",
            "The reversible startup proxy must quote its executable and use its own dedicated entry point.");
        string[] Normalize(params string[] arguments) => (string[])proxy.GetMethod("NormalizeArguments", Static)!.Invoke(null, [arguments])!;
        var file = Path.Combine(Path.GetTempPath(), "proxy 示例", "session.rdp");
        Check(Normalize("--mstsc-proxy", @"C:\Windows\System32\mstsc.exe", file).SequenceEqual(new[] { "--proxy-connection", file }),
            "IFEO's original mstsc.exe argument must be removed without changing the session path.");
        Check(Normalize("--mstsc-proxy", @"C:\Windows\System32\mstsc", file).SequenceEqual(new[] { "--proxy-connection", file }),
            "A caller may omit the executable extension in its original command line.");
        Check(Normalize("--mstsc-proxy", file).SequenceEqual(new[] { "--proxy-connection", file }),
            "A launcher may supply the RDP file directly without an executable token.");
        Check(Normalize("--mstsc-proxy", "/v:example.invalid:3391").SequenceEqual(new[] { "--proxy-connection", "--hostname=example.invalid:3391" }),
            "A direct endpoint argument must not be mistaken for the executable name.");
        Check(Normalize("--mstsc-proxy", "\"mstsc.exe\"", "\"" + file + "\"").SequenceEqual(new[] { "--proxy-connection", file }),
            "Retained outer quotes from a wrapper must not prevent executable and file recognition.");
        Check(Normalize("--mstsc-proxy", "mstsc.exe", @"C:\Windows\System32\mstsc.exe", file).SequenceEqual(new[] { "--proxy-connection", file }),
            "Repeated executable tokens from an assistant wrapper must not be mistaken for a C: switch.");
        Check(Normalize("--mstsc-proxy", "/v", "example.invalid:3391", "-w=1400", "/h", "800", "/fullscreen").SequenceEqual(
            new[] { "--proxy-connection", "--hostname=example.invalid:3391", "--width=1400", "--height=800", "--fullscreen" }),
            "Colon, equals, separated values, dash prefixes and fullscreen aliases must share one parser.");
        Check(Normalize("--mstsc-proxy", "\"C:\\Windows\\System32\\mstsc.exe\" \"" + file + "\"").SequenceEqual(new[] { "--proxy-connection", file }),
            "A whole Windows command line passed as one argument must retain its quoted file path.");
        Check(Normalize("--mstsc-proxy", new Uri(file).AbsoluteUri).SequenceEqual(new[] { "--proxy-connection", file }),
            "File URLs must decode to local paths, without fetching network URLs.");
        var temporarySession = Path.Combine(Path.GetTempPath(), "purerdp-proxy-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporarySession, "full address:s:example.invalid\r\ndesktopwidth:i:1280\r\n", System.Text.Encoding.Unicode);
            Check(Normalize("--mstsc-proxy", "mstsc.exe", temporarySession).SequenceEqual(new[] { "--proxy-connection", "--rdp-file=" + temporarySession }),
                "Temporary session files without the .rdp suffix must be validated by content and passed explicitly.");
            File.WriteAllText(temporarySession, "not an RDP file");
            try { Normalize("--mstsc-proxy", temporarySession); Check(false, "An arbitrary temporary file must not be accepted as RDP."); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { _checks++; }
        }
        finally { File.Delete(temporarySession); }
        Check(Normalize("--mstsc-proxy", "mstsc.exe", "/v:example.invalid:3391", "/w:1000", "/h:700", "/f").SequenceEqual(
            new[] { "--proxy-connection", "--hostname=example.invalid:3391", "--width=1000", "--height=700", "--fullscreen" }),
            "Standard MSTSC display and endpoint arguments must translate to PureRDP options.");
        Check(Normalize(file).SequenceEqual(new[] { file }), "Normal file launches must remain unchanged.");
        try { Normalize("--mstsc-proxy", "mstsc.exe", "/unsupported"); Check(false, "Unsupported switches must not be ignored."); }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { _checks++; }
        var profileType = App.GetType("PureRDP.Configuration.ConnectionSettings")!;
        var source = Activator.CreateInstance(profileType, true)!;
        foreach (var property in new[] { "Hostname", "Username", "Domain", "Password", "LoadBalanceInfo" })
            profileType.GetProperty(property)!.SetValue(source, "synthetic-private");
        profileType.GetProperty("Width")!.SetValue(source, 1110);
        var commonService = App.GetType("PureRDP.Services.RdpProxySettingsService")!;
        var sanitized = commonService.GetMethod("Sanitize", Static)!.Invoke(null, [source])!;
        foreach (var property in new[] { "Hostname", "Username", "Domain", "Password", "LoadBalanceInfo" })
            Check(profileType.GetProperty(property)!.GetValue(sanitized) == null, "Shared preferences must exclude identity and tickets: " + property);
        var preferencesType = App.GetType("PureRDP.Configuration.GlobalRdpProxyPreferences")!;
        var preferences = Activator.CreateInstance(preferencesType, true)!;
        preferencesType.GetProperty("Connection")!.SetValue(preferences, sanitized);
        commonService.GetMethod("Apply", Static)!.Invoke(null, [source, preferences]);
        Check((int)profileType.GetProperty("Width")!.GetValue(source)! == 1110, "Disabled preferences must leave the connection unchanged.");
        profileType.GetProperty("Width")!.SetValue(source, 900);
        preferencesType.GetProperty("UseCommonSettings")!.SetValue(preferences, true);
        commonService.GetMethod("Apply", Static)!.Invoke(null, [source, preferences]);
        Check((int)profileType.GetProperty("Width")!.GetValue(source)! == 1110 &&
            (string)profileType.GetProperty("LoadBalanceInfo")!.GetValue(source)! == "synthetic-private",
            "Enabled common display settings must preserve the original session identity and ticket.");
    }

    private static void TestProxyRegistry()
    {
        // A unique HKCU scratch root is passed to the registration engine.
        // This is NOT the system IFEO location and cannot intercept a process.
        var scratchPath = @"Software\PureRDP\RegressionFixtures\" + Guid.NewGuid().ToString("N");
        const string targetPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\mstsc.exe";
        var proxy = App.GetType("PureRDP.Services.MstscProxyService")!;
        var engine = proxy.GetMethod("ConfigureRegistry", Static)!;
        var executable = Path.GetFullPath("PureRDP/bin/Release/net10.0-windows/PureRDP.exe");
        try
        {
            using var scratch = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratchPath);
            engine.Invoke(null, [scratch, true, executable]);
            using (var target = scratch.OpenSubKey(targetPath, true)!)
            {
                var owned = target.GetValue("Debugger") as string;
                Check(owned?.EndsWith(" --mstsc-proxy") == true, "Enable must write the owned startup command under the supplied root only.");
                target.SetValue("UnrelatedSetting", 17);
                engine.Invoke(null, [scratch, true, executable]);
                engine.Invoke(null, [scratch, false, executable]);
                Check(target.GetValue("Debugger") == null && Equals(target.GetValue("UnrelatedSetting"), 17),
                    "Restore must remove only the owned debugger value and preserve unrelated settings.");
                target.SetValue("Debugger", "", Microsoft.Win32.RegistryValueKind.ExpandString);
                engine.Invoke(null, [scratch, true, executable]);
                engine.Invoke(null, [scratch, false, executable]);
                Check((string?)target.GetValue("Debugger") == "" && target.GetValueKind("Debugger") == Microsoft.Win32.RegistryValueKind.ExpandString,
                    "Restore must preserve the previous empty value and its registry type.");
                target.SetValue("Debugger", "other-tool-fixture");
                try { engine.Invoke(null, [scratch, true, executable]); Check(false, "An existing third-party proxy must not be overwritten."); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { _checks++; }
                Check((string?)target.GetValue("Debugger") == "other-tool-fixture", "Conflict must leave the third-party value intact.");
                target.DeleteValue("Debugger");
                engine.Invoke(null, [scratch, true, executable]);
                owned = target.GetValue("Debugger") as string;
                target.SetValue("Debugger", "changed-after-install-fixture");
                try { engine.Invoke(null, [scratch, false, executable]); Check(false, "Restore must not overwrite a later third-party change."); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { _checks++; }
                target.SetValue("Debugger", owned!);
                engine.Invoke(null, [scratch, false, executable]);
                Check(target.GetValue("Debugger") == null, "Owned recovery must remain usable after a conflict is resolved.");
            }
        }
        finally
        {
            // Delete only the exact GUID root this method created.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(scratchPath, false);
        }
    }

    private static void TestRelayAcknowledgement()
    {
        var relayType = App.GetType("PureRDP.Program+SingleInstanceRelay")!;
        var pipe = "PureRDP.fixture." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string[], Task> callback = arguments =>
        {
            if (arguments.SequenceEqual(new[] { "synthetic-session.rdp" })) received.TrySetResult();
            return consumed.Task;
        };
        using var relay = (IDisposable)Activator.CreateInstance(relayType, Instance, null, [pipe, callback], null)!;
        var forward = Task.Run(() => relayType.GetMethod("TryForward", Static)!.Invoke(null, [pipe, new[] { "synthetic-session.rdp" }]));
        try
        {
            Check(received.Task.Wait(TimeSpan.FromSeconds(3)), "The primary instance must receive the launch request.");
            Check(!forward.IsCompleted, "The proxy child must wait until the primary instance has consumed its temporary file request.");
            consumed.TrySetResult();
            Check(forward.Wait(TimeSpan.FromSeconds(3)), "The proxy may exit after the primary instance acknowledges consumption.");
        }
        finally { consumed.TrySetResult(); }
    }
}
