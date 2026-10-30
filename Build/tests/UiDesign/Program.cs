using System.Collections;
using System.Drawing.Imaging;
using System.Reflection;
using PureRDP.UI;

internal static class Program
{
    private static readonly Assembly App = typeof(FormRdpClient).Assembly;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;
    private static object New(string name, params object?[] args) => Activator.CreateInstance(
        App.GetType("PureRDP." + name)!, Members, null, args, null)!;
    private static object Field(object o, string name) => o.GetType().GetField(name, Members)!.GetValue(o)!;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private static IEnumerable<Control> Children(Control control)
    {
        foreach (Control child in control.Controls)
        { yield return child; foreach (var descendant in Children(child)) yield return descendant; }
    }
    private static void Render(Control control, string name)
    {
        if (control is Form form && !form.Visible)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
        }
        control.CreateControl(); control.PerformLayout();
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size));
        image.Save(Path.Combine("outputs", "ui-review", name + ".png"), ImageFormat.Png);
        Check(image.Width > 0 && image.Height > 0, "Empty render: " + name);
    }
    [STAThread]
    private static void Main()
    {
        TestDeviceIsolation();
        Directory.CreateDirectory(Path.Combine("outputs", "ui-review"));
        foreach (var entry in new[] {
            ("ConnectionCardControl", "PreviewEnabled"), ("ConnectionCardControl", "PreviewSource"),
            ("ConnectionCardControl", "PreviewHost"), ("ConnectionCardControl", "PreviewOpen"),
            ("PreviewSwitchControl", "LabelText"), ("PreviewSwitchControl", "Checked"),
            ("ModernButton", "Kind"), ("ModernButton", "Selected"), ("ModernButton", "Bordered"),
            ("RemotePreviewControl", "SourceProvider"), ("RemotePreviewControl", "PreviewHostProvider"),
            ("ConnectionLibraryPanel", "PreviewSourceProvider"), ("ConnectionLibraryPanel", "PreviewHostProvider"),
            ("ConnectionLibraryPanel", "ConnectionQualityProvider"), ("ConnectionLibraryPanel", "StatusRefreshRequested"),
            ("SettingsPage", "NavigationButton") })
        {
            var property = App.GetType("PureRDP.UI." + entry.Item1)!.GetProperty(entry.Item2, Members)!;
            Check(property.GetCustomAttribute<System.ComponentModel.DesignerSerializationVisibilityAttribute>()?.Visibility ==
                System.ComponentModel.DesignerSerializationVisibility.Hidden ||
                property.GetCustomAttribute<System.ComponentModel.DefaultValueAttribute>() != null,
                "Missing designer serialization metadata: " + entry.Item1 + "." + entry.Item2);
        }
        var settings = New("Configuration.GlobalSettings");
        // This badge scenario starts from an explicitly saved disabled state.
        var proxyPreferences = settings.GetType().GetProperty("RdpProxy")!.GetValue(settings)!;
        proxyPreferences.GetType().GetProperty("UseCommonSettings")!.SetValue(proxyPreferences, false);
        using var global = (Form)New("UI.GlobalSettingsForm", settings);
        var pages = (IList)Field(global, "_pages");
        Check(pages.Count == 9, "Global settings must expose all nine pages including file opening.");
        foreach (Control page in pages)
        {
            global.GetType().GetMethod("SelectPage", Members)!.Invoke(global, new object[] { page });
            Render(global, page.GetType().Name);
        }
        var fileOpening = pages.Cast<Control>().Single(p => p.GetType().Name == "FileOpeningSettingsPage");
        global.GetType().GetMethod("SelectPage", Members)!.Invoke(global, new object[] { fileOpening });
        var proxyBadge = Children(fileOpening).OfType<Label>().Single(label => label.Name == "MstscProxyState");
        foreach (var enabled in new[] { false, true })
        {
            fileOpening.GetType().GetMethod("DisplayProxyState", Members)!.Invoke(fileOpening, new object[] { enabled });
            Check(proxyBadge.Visible && proxyBadge.Text.Contains(enabled ? "已开启" : "未开启"),
                "The proxy on/off state must be explicitly visible beside its actions.");
            Check(!enabled || proxyBadge.BackColor == Color.FromArgb(37, 124, 75) && proxyBadge.ForeColor == Color.White,
                "Enabled state must have a high contrast green badge, not depend on a footer message.");
            Render(global, enabled ? "ProxyEnabled" : "ProxyDisabled");
        }
        var commonCheck = Children(fileOpening).OfType<CheckBox>().Single(item => item.Name == "UseRdpCommonSettings");
        var commonBadge = Children(fileOpening).OfType<Label>().Single(label => label.Name == "RdpCommonState");
        commonCheck.Checked = true;
        Check(commonBadge.Text == "待保存：启用", "An unsaved template switch must not claim it is already active.");
        commonCheck.Checked = false;
        Check(commonBadge.Text == "未启用", "Reverting the checkbox must restore its actual saved state.");
        var nativeCheck = Children(fileOpening).OfType<CheckBox>().Single(item => item.Name == "PreferNativeRdpClient");
        nativeCheck.Checked = true;
        Check(!commonCheck.Enabled && commonBadge.Text == "原生连接不应用",
            "Native preference must clearly disable settings which cannot affect the native client.");
        var preferenceDraft = New("Configuration.GlobalSettings");
        fileOpening.GetType().GetMethod("ApplyTo", Members)!.Invoke(fileOpening, new[] { preferenceDraft });
        var proxyPreference = preferenceDraft.GetType().GetProperty("RdpProxy")!.GetValue(preferenceDraft)!;
        Check((bool)proxyPreference.GetType().GetProperty("PreferNativeClient")!.GetValue(proxyPreference)!,
            "The native compatibility choice must be included in the settings draft without writing actual preferences.");
        Render(global, "NativeCompatibilityEnabled");
        nativeCheck.Checked = false;
        Check(commonCheck.Enabled && commonBadge.Text == "未启用", "Returning to automatic routing must restore the common-setting controls.");
        Check(Children(fileOpening).OfType<Button>().Any(b => b.Name == "EnableMstscProxy") &&
            Children(fileOpening).OfType<Button>().Any(b => b.Name == "RestoreMstscProxy"),
            "Proxy settings must expose enable and recovery without invoking system registration in tests.");
        var windowPage = pages.Cast<Control>().Single(p => p.GetType().Name == "WindowSettingsPage");
        Check(Children(windowPage).OfType<CheckBox>().Single(c => c.Name == "RoundedConnectionCorners").Checked,
            "Rounded connection corners must be enabled by default.");
        Check(Children(fileOpening).OfType<Button>().Any(b => b.Name == "RegisterRdpAssociation") &&
            Children(fileOpening).OfType<Button>().Any(b => b.Name == "ChooseDefaultRdpApp"),
            "File opening settings must expose registration and Windows default-app selection without activating them in tests.");
        var data = pages.Cast<Control>().Single(p => p.GetType().Name == "DataStorageSettingsPage");
        var path = Children(data).OfType<TextBox>().Single(t => t.Name == "PrimaryDataDirectory");
        Check(path.ReadOnly, "Data directory must be selectable but read-only.");
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PureRDP");
        Check(path.Text == expected, "Displayed primary directory must match real global-settings storage.");
        var method = App.GetType("PureRDP.Services.ProductPaths")!.GetMethod("GetDataDirectories", BindingFlags.Public | BindingFlags.Static)!;
        var directories = (IEnumerable<string>)method.Invoke(null, null)!;
        Check(directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4, "Fallback locations must be complete and distinct.");
        Check(!Children(data).OfType<Button>().Any(b => b.Text.Contains("删除")), "Data page must not expose destructive cleanup.");
        global.ClientSize = new Size(1000, 660);
        global.GetType().GetMethod("SelectPage", Members)!.Invoke(global, new object[] { data });
        Render(global, "GlobalCompact");

        var application = New("Configuration.ApplicationSettings");
        using (var commonEditor = (Form)New("UI.FormSettings", application, null, Size.Empty, true))
        {
            Render(commonEditor, "RdpCommonSettings");
            Check(!((Control)Field(commonEditor, "grpConnection")).Visible && !((Control)Field(commonEditor, "btnConnect")).Visible,
                "Common settings must hide device identity and connection actions.");
            commonEditor.GetType().GetMethod("CompleteSettings", Members)!.Invoke(commonEditor, new object[] { false });
            Check(commonEditor.DialogResult == DialogResult.OK, "Common settings must save a target-free draft without device or credential writes.");
        }
        using var device = (Form)New("UI.FormSettings", application, null, new Size(1920, 1080));
        Render(device, "DeviceSettings");
        Check(device.FormBorderStyle == FormBorderStyle.Sizable, "Device settings must support resizing.");
        foreach (var name in new[] { "cmbHostname", "cmbUsername", "tbPassword", "_keyboardModeWindowFullscreen", "_keyboardModeFullscreen" })
            Check(((Control)Field(device, name)).Parent != null, "Existing input lost after layout: " + name);
        Check(((TextBox)Field(device, "tbPassword")).UseSystemPasswordChar, "Password protection must remain enabled.");
        Control FieldFrame(Control control) => control.Parent?.GetType().Name == "SettingsTextField" ? control.Parent : control;
        var deviceInputs = new[] { "_deviceName", "cmbHostname", "cmbUsername", "tbPassword" }
            .Select(name => FieldFrame((Control)Field(device, name))).ToArray();
        Check(deviceInputs.Select(c => c.Left).Distinct().Count() == 1,
            "Connection text and dropdown fields must share one aligned left edge.");
        Check(deviceInputs.Select(c => c.Width).Distinct().Count() == 1 &&
            deviceInputs.Select(c => c.Height).Distinct().Count() == 1,
            "Text and dropdown fields must have matching widths and heights.");
        Check(deviceInputs.Zip(deviceInputs.Skip(1), (a, b) => b.Top - a.Top).Distinct().Count() == 1,
            "Connection field rows must have uniform vertical spacing.");
        var keyboardRows = new[] { "cmbKeyboardMode", "_keyboardModeWindowFullscreen", "_keyboardModeFullscreen" }
            .Select(name => (Control)Field(device, name)).ToArray();
        Check(keyboardRows.All(c => c.Left == deviceInputs[0].Left && c.Height == deviceInputs[0].Height),
            "Resource keyboard modes must align with the other input columns.");
        Check(keyboardRows[1].Top - keyboardRows[0].Top == keyboardRows[2].Top - keyboardRows[1].Top &&
            keyboardRows[0].Bottom < keyboardRows[1].Top,
            "Keyboard-mode fields must have equal row gaps and must not touch.");
        var accentColor = (Color)App.GetType("PureRDP.UI.UiDesignTokens")!.GetField("Accent")!.GetValue(null)!;
        Check(Children(device).OfType<LinkLabel>().All(link => link.LinkColor == accentColor),
            "Credential links must use the green theme, not default Windows blue.");
        Check(Children(device).OfType<CheckBox>().All(check => check.GetType().Name == "SettingsCheckBox"),
            "All device checkbox glyphs must use the same theme.");
        var clipboard = (CheckBox)Field(device, "chkRedirectClipboard");
        var originalCheck = clipboard.CheckState;
        clipboard.GetType().GetMethod("OnClick", Members)!.Invoke(clipboard, new object[] { EventArgs.Empty });
        Check(clipboard.CheckState != originalCheck, "The themed checkbox must still toggle on click.");
        var clipboardDraft = device.GetType().GetMethod("GetConnectionSettingsFromUi", Members)!.Invoke(device, null)!;
        Check(Equals(clipboardDraft.GetType().GetProperty("RedirectClipboard")!.GetValue(clipboardDraft), clipboard.Checked),
            "The themed checkbox must still bind to the existing clipboard setting.");
        clipboard.CheckState = originalCheck;
        Check(Children(device).OfType<ComboBox>().All(c => c.GetType().Name == "ModernComboBox"),
            "All device dropdowns must share the modern palette.");
        Check(pages.Cast<Control>().SelectMany(Children).OfType<ComboBox>().All(c => c.GetType().Name == "ModernComboBox"),
            "All global dropdowns must share the modern palette.");
        var address = (ComboBox)Field(device, "cmbHostname");
        Check(address.DropDownStyle == ComboBoxStyle.DropDown, "Address input must stay editable.");
        address.Text = "192.0.2.18:3389";
        Check(address.Text == "192.0.2.18:3389", "Styling must not change editable input text.");
        var position = (ComboBox)Field(pages.Cast<Control>().First(p => p.GetType().Name == "WindowSettingsPage"), "_controlBarPosition");
        Check(position.DropDownStyle == ComboBoxStyle.DropDownList && position.Items.Count == 4,
            "Control-bar position options and noneditable behavior must remain unchanged.");
        position.SelectedIndex = 2;
        Check(position.SelectedItem!.ToString()!.Contains("左侧"), "Native dropdown selection must retain the original bound item.");
        device.ClientSize = new Size(780, 570);
        Render(device, "DeviceCompact");
        var panels = Children(device).OfType<Panel>().Where(p => p.AutoScroll).ToArray();
        Check(panels.Length > 0, "Device settings need a scrollable content region.");
        var password = (Control)Field(device, "tbPassword");
        Check(password.Width >= 120 && password.Right <= password.Parent!.ClientSize.Width, "Password field must stay inside its group.");
        var connectionGroup = (Control)Field(device, "grpConnection");
        var displayGroup = (Control)Field(device, "grpDisplay");
        var columns = (TableLayoutPanel)displayGroup.Parent!.Parent!;
        Check(columns.GetPositionFromControl(displayGroup.Parent).Column == 1 &&
            columns.GetPositionFromControl(displayGroup.Parent).Row == 0,
            "Display settings must remain in the right column in a narrow window.");
        Check(columns.GetPositionFromControl(connectionGroup.Parent!).Column == 0,
            "Connection and resource settings must remain in the left column.");
        Check(displayGroup.PointToScreen(Point.Empty).X > connectionGroup.PointToScreen(Point.Empty).X &&
            displayGroup.PointToScreen(Point.Empty).Y == connectionGroup.PointToScreen(Point.Empty).Y,
            "Both column headings must stay side by side and top-aligned.");
        foreach (var group in new[] { connectionGroup, (Control)Field(device, "grpRedirection"),
            displayGroup, (Control)Field(device, "grpExperience"), (Control)Field(device, "grpServerAuthentication") })
            Check(group.Controls.Cast<Control>().All(c => c.Left >= 0 && c.Top >= 0 && c.Right <= group.ClientSize.Width && c.Bottom <= group.ClientSize.Height),
                "Every settings row must remain inside its section: " + group.Name);
        device.ClientSize = new Size(1000, 780);
        ((TextBox)Field(device, "_deviceName")).Text = "示例工作站";
        ((ComboBox)Field(device, "cmbUsername")).Text = "Administrator";
        ((TextBox)Field(device, "tbPassword")).Text = "fixture-password";
        ((TextBox)Field(device, "tbBadgeColor")).Text = "#8000FF";
        var draft = device.GetType().GetMethod("GetConnectionSettingsFromUi", Members)!.Invoke(device, null)!;
        Check(Equals(draft.GetType().GetProperty("DisplayName")!.GetValue(draft), "示例工作站") &&
            Equals(draft.GetType().GetProperty("Password")!.GetValue(draft), "fixture-password"),
            "Reparented text fields must still feed settings values without saving.");
        ((Control)Field(device, "lbCredentialSave")).Visible = true;
        ((Control)Field(device, "lbCredentialRemove")).Visible = true;
        Render(device, "DeviceSettingsFilled");
        using var reconnect = (Control)New("UI.RdpReconnectDialog", "示例设备");
        using (var retryHost = new Form { ClientSize = new Size(800, 600) })
        {
            retryHost.Controls.Add(reconnect);
            Render(retryHost, "ReconnectHost");
            reconnect.GetType().GetMethod("WaitForRetry", Members)!.Invoke(reconnect, new object[] { 5, 32, "暂时无法连接远程计算机" });
            Check(!(reconnect is Form) && reconnect.Parent == retryHost && reconnect.Dock == DockStyle.Fill,
                "Reconnect must remain inside the current session, with no top-level dialog.");
            // Let the staggered crystal animation emerge before rendering.
            var animationWatch = System.Diagnostics.Stopwatch.StartNew();
            while (animationWatch.ElapsedMilliseconds < 900) { Application.DoEvents(); Thread.Sleep(10); }
            Render(reconnect, "Reconnect");
            retryHost.ClientSize = new Size(420, 320);
            Application.DoEvents();
            Check(reconnect.Controls.Cast<Control>().All(item => reconnect.ClientRectangle.Contains(item.Bounds)),
                "Reconnect controls must fit a small session window.");
            Render(reconnect, "ReconnectSmall");
        }
        using (var loadingHost = (Form)New("UI.FormRdpClient", Array.Empty<string>(), true, null))
        {
            var shown = loadingHost.GetType().GetMethod("FormRdpClient_Shown", Members)!;
            loadingHost.Shown -= (EventHandler)shown.CreateDelegate(typeof(EventHandler), loadingHost);
            var closing = loadingHost.GetType().GetMethod("FormRdpClient_FormClosing", Members)!;
            loadingHost.FormClosing -= (FormClosingEventHandler)closing.CreateDelegate(typeof(FormClosingEventHandler), loadingHost);
            loadingHost.StartPosition = FormStartPosition.Manual;
            loadingHost.Location = new Point(-20000, -20000);
            loadingHost.ShowInTaskbar = false;
            loadingHost.Show();
            loadingHost.GetType().GetMethod("ShowConnectingMessage", Members)!.Invoke(loadingHost,
                new object[] { "正在连接 示例工作站…" });
            var loader = (Control)Field(loadingHost, "_connectionLoader");
            var animationTimer = (System.Windows.Forms.Timer)Field(loader, "_timer");
            var connectingPanel = (Control)Field(loadingHost, "panelConnect");
            Check(loadingHost.Controls.GetChildIndex(connectingPanel) == 0,
                "Loading surface must stay above the visible native RDP host.");
            Check(animationTimer.Enabled, "Visible connecting surface must animate.");
            System.Threading.Thread.Sleep(1800);
            Application.DoEvents();
            foreach (var viewport in new[] { new Size(800, 560), new Size(420, 260) })
            {
                loadingHost.ClientSize = viewport;
                Application.DoEvents();
                var status = (Control)Field(loadingHost, "lbConnecting");
                var cancel = (Control)Field(loadingHost, "btnCancel");
                Check(status.Visible && cancel.Visible, "Loading status and cancellation must be immediately available.");
                Check(loader.Bottom <= status.Top && status.Bottom <= cancel.Top && cancel.Bottom <= connectingPanel.Height,
                    "Crystal loading layout must fit small and normal windows without overlapping controls.");
                // Capture the loading panel directly: Form.DrawToBitmap also
                // prints its native RDP sibling even when it is behind it.
                Render(connectingPanel, "ConnectingCrystals-" + viewport.Width);
            }
            using var frameA = new Bitmap(200, 200);
            using var frameB = new Bitmap(200, 200);
            using (var graphics = Graphics.FromImage(frameA))
                loader.GetType().GetMethod("PaintFrame", Members)!.Invoke(loader, new object[] { graphics, .8 });
            using (var graphics = Graphics.FromImage(frameB))
                loader.GetType().GetMethod("PaintFrame", Members)!.Invoke(loader, new object[] { graphics, 2.1 });
            var differences = 0;
            for (var y = 0; y < 200; y++)
                for (var x = 0; x < 200; x++)
                    if (frameA.GetPixel(x, y) != frameB.GetPixel(x, y)) differences++;
            Check(differences > 100, "Crystal phases must produce different visible frames.");
            frameB.Save(Path.Combine("outputs", "ui-review", "CrystalLoader-frame.png"), ImageFormat.Png);
            loadingHost.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            Check(!animationTimer.Enabled, "Minimized loading window must stop rendering ticks.");
            loadingHost.WindowState = FormWindowState.Normal;
            Application.DoEvents();
            Check(animationTimer.Enabled, "Restored loading window must resume its animation.");
            loadingHost.GetType().GetMethod("HideConnectingMessage", Members)!.Invoke(loadingHost, null);
            Check(!animationTimer.Enabled, "Connection completion must stop animation ticks.");
            loadingHost.Dispose();
            Check(!animationTimer.Enabled, "Disposed loading controls must not retain an active timer.");
        }
        using var canvas = new Form { ClientSize = new Size(1080, 720) };
        using var library = (Control)New("UI.ConnectionLibraryPanel");
        canvas.Controls.Add(library);
        // Load may read saved devices; immediately replace them with no devices
        // for deterministic snapshots. No preview provider or RDP login exists.
        canvas.StartPosition = FormStartPosition.Manual;
        canvas.Location = new Point(-20000, -20000);
        canvas.ShowInTaskbar = false;
        canvas.Show();
        ((IList)Field(library, "_connections")).Clear();
        library.GetType().GetMethod("RenderConnections", Members)!.Invoke(library, null);
        Render(canvas, "ConsoleEmpty");
        var fileRequests = 0;
        library.GetType().GetEvent("OpenRdpFileRequested")!.AddEventHandler(library, (Action)(() => fileRequests++));
        var openFileButton = (Button)Field(library, "_openRdpButton");
        openFileButton.PerformClick();
        Check(fileRequests == 1, "Open RDP button must route a single file-open request, not start a connection directly.");
        foreach (var headerWidth in new[] { 420, 640, 1080, 1280 })
        {
            canvas.ClientSize = new Size(headerWidth, 720);
            Application.DoEvents();
            var toolbar = openFileButton.Parent!;
            Check(openFileButton.Visible && openFileButton.Left >= 0 && openFileButton.Right <= toolbar.ClientSize.Width &&
                openFileButton.Bottom <= toolbar.ClientSize.Height,
                "Open RDP entry must remain inside the toolbar at width " + headerWidth);
            foreach (Control sibling in toolbar.Controls)
                if (sibling.Visible && sibling != openFileButton)
                    Check(!sibling.Bounds.IntersectsWith(openFileButton.Bounds),
                        "Open RDP entry must not overlap other header controls at width " + headerWidth);
            Render(canvas, "OpenRdpToolbar-" + headerWidth);
        }
        canvas.ClientSize = new Size(1080, 720);
        var connection = New("Configuration.ConnectionSettings");
        connection.GetType().GetProperty("DisplayName")!.SetValue(connection, "工程工作站");
        connection.GetType().GetProperty("Hostname")!.SetValue(connection, "192.0.2.18:3389");
        using var card = (Control)New("UI.ConnectionCardControl", connection);
        canvas.Controls.Clear(); canvas.Controls.Add(card); card.Location = new Point(24, 24);
        Render(canvas, "DeviceCard");
        card.GetType().GetMethod("SurfaceMouseEnter", Members)!.Invoke(card, new object?[] { card, EventArgs.Empty });
        Render(canvas, "DeviceCard-Hover");
        card.GetType().GetMethod("SurfaceMouseLeave", Members)!.Invoke(card, new object?[] { card, EventArgs.Empty });
        card.Focus();
        Render(canvas, "DeviceCard-Selected");
        ((Control)Field(card, "_editButton")).Focus();
        Render(canvas, "DeviceCard-ChildFocus");
        canvas.ActiveControl = null;
        foreach (var previewEnabled in new[] { false, true })
        foreach (var modeName in new[] { "Cards", "Horizontal", "List" })
        {
            card.GetType().GetProperty("PreviewEnabled", Members)!.SetValue(card, previewEnabled);
            var mode = Enum.Parse(App.GetType("PureRDP.Configuration.ConnectionLibraryViewMode")!, modeName);
            card.GetType().GetMethod("SetViewMode")!.Invoke(card, new[] { mode });
            foreach (var factor in new[] { 1.25F, .8F, 1.5F, 2F / 3F, 2F, .5F })
            {
                card.Width = modeName == "List" ? 1000 : 720;
                typeof(Control).GetMethod("OnDpiChangedBeforeParent", Members)!.Invoke(card, new object[] { EventArgs.Empty });
                card.Scale(new SizeF(factor, factor));
                card.GetType().GetMethod("OnDpiChangedAfterParent", Members)!.Invoke(card, new object[] { EventArgs.Empty });
                card.PerformLayout();
                var actions = (Control)Field(card, "_actionPanel");
                var favorite = (Control)Field(card, "_pinButton");
                Check(!favorite.Bounds.IntersectsWith(actions.Bounds),
                    "Top-right favorite must not overlap actions in any view: " + modeName);
                Check(actions.Bottom <= card.ClientSize.Height && actions.Top >= 0,
                    "DPI transition must keep actions inside card: " + modeName);
                foreach (Control action in actions.Controls)
                    if (action.Visible)
                        Check(action.Left >= 0 && action.Top >= 0 && action.Right <= actions.ClientSize.Width && action.Bottom <= actions.ClientSize.Height,
                            "DPI transition must not clip or overlap action controls: " + modeName);
                var visibleActions = actions.Controls.Cast<Control>().Where(action => action.Visible).ToArray();
                for (var i = 0; i < visibleActions.Length; i++)
                    for (var j = i + 1; j < visibleActions.Length; j++)
                        Check(!visibleActions[i].Bounds.IntersectsWith(visibleActions[j].Bounds),
                            "DPI transition must not overlap action buttons: " + modeName);
            }
            card.Width = modeName == "List" ? 1000 : 560;
            Render(canvas, "DpiRecovered-" + modeName + (previewEnabled ? "-Preview" : ""));
        }
        card.GetType().GetProperty("PreviewEnabled", Members)!.SetValue(card, true);
        card.GetType().GetMethod("SetViewMode")!.Invoke(card, new[] {
            Enum.Parse(App.GetType("PureRDP.Configuration.ConnectionLibraryViewMode")!, "Cards") });
        connection.GetType().GetProperty("DisplayName")!.SetValue(connection, "TBEA");
        card.GetType().GetMethod("UpdateSettings", Members)!.Invoke(card, new[] { connection });
        var quality = App.GetType("PureRDP.Services.ConnectionQualitySnapshot")!
            .GetProperty("Measuring", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        card.GetType().GetMethod("SetConnectionQuality", Members)!.Invoke(card, new[] { quality });
        foreach (var previewOpen in new[] { false, true })
        foreach (var cardWidth in new[] { 308, 440, 560, 900 })
        {
            card.Width = cardWidth;
            card.GetType().GetProperty("PreviewOpen", Members)!.SetValue(card, previewOpen);
            var title = (Control)Field(card, "_titleLabel");
            var host = (Control)Field(card, "_hostLabel");
            var telemetry = (Control)Field(card, "_qualityLabel");
            var preview = (Control)Field(card, "_preview");
            var actions = (Control)Field(card, "_actionPanel");
            Check(title.Width >= 176 && title.Height == 30, "Card title needs a full single-line header.");
            var star = (Control)Field(card, "_pinButton");
            Check(star.Left > title.Right && star.Right <= card.Width - 30,
                "Favorite star must sit in the top-right without overlapping title or status.");
            Check(host.Top - title.Bottom <= 12, "Device details must not leave an empty band below identity.");
            Check(telemetry.Bottom <= actions.Top, "Compact telemetry must not overlap actions.");
            Check(((Label)telemetry).Text.Split('\n').Length == 2, "Card telemetry must use two compact rows.");
            if (previewOpen)
            {
                Check(Math.Abs(preview.Top - host.Top) <= 2 && preview.Bottom <= actions.Top, "Preview must align with the details region.");
                Check(!telemetry.Bounds.IntersectsWith(preview.Bounds), "Telemetry must remain beside the preview.");
                Check(Math.Abs(preview.Width * 9 / 16 - preview.Height) <= 1, "Preview must retain 16:9 proportions.");
            }
            Render(card, $"CompactCard-{cardWidth}" + (previewOpen ? "-Preview" : ""));
        }
        foreach (var stableMode in new[] { "Cards", "Horizontal", "List" })
        foreach (var stableWidth in new[] { 308, 560, 900, 1200 })
        {
            card.GetType().GetMethod("SetViewMode")!.Invoke(card, new[] {
                Enum.Parse(App.GetType("PureRDP.Configuration.ConnectionLibraryViewMode")!, stableMode) });
            card.Width = stableWidth;
            var fixedSize = card.Size;
            var fixedActions = ((Control)Field(card, "_actionPanel")).Bounds;
            foreach (var previewsAllowed in new[] { false, true })
            foreach (var open in new[] { false, true, false })
            foreach (var connected in new[] { false, true, false })
            {
                card.GetType().GetProperty("PreviewEnabled", Members)!.SetValue(card, previewsAllowed);
                card.GetType().GetProperty("PreviewOpen", Members)!.SetValue(card, open);
                card.GetType().GetMethod("SetConnectionQuality", Members)!.Invoke(card,
                    new object?[] { connected ? quality : null });
                var state = Enum.Parse(App.GetType("PureRDP.Services.DeviceAvailabilityState")!, connected ? "Online" : "Offline");
                card.GetType().GetMethod("SetAvailability", Members)!.Invoke(card, new[] { state });
                Check(card.Size == fixedSize, "Preview/connect/disconnect must not resize device: " + stableMode);
                Check(((Control)Field(card, "_actionPanel")).Bounds == fixedActions,
                    "Session state must not move the card action row: " + stableMode);
            }
        }
        var listMode = Enum.Parse(App.GetType("PureRDP.Configuration.ConnectionLibraryViewMode")!, "List");
        card.GetType().GetMethod("SetViewMode")!.Invoke(card, new[] { listMode });
        foreach (var rowWidth in new[] { 560, 720, 900, 1200, 1500 })
        {
            card.Width = rowWidth;
            var actions = (Control)Field(card, "_actionPanel");
            var rowColumns = new[] { "_titleLabel", "_hostLabel", "_metaLabel", "_hintLabel", "_qualityLabel" }
                .Select(name => (Control)Field(card, name)).Where(c => c.Visible).ToArray();
            foreach (var column in rowColumns)
                Check(column.Width > 0 && column.Right < actions.Left && column.Top == rowColumns[0].Top && column.Bottom <= card.Height,
                    "List columns must fit on one baseline before actions at width " + rowWidth);
            for (var i = 1; i < rowColumns.Length; i++)
                Check(rowColumns[i - 1].Right <= rowColumns[i].Left,
                    "Responsive list columns must not overlap at width " + rowWidth);
            Render(card, "ResponsiveList-" + rowWidth);
        }
        using (var responsiveLibrary = (Control)New("UI.ConnectionLibraryPanel"))
        using (var row = (Control)New("UI.ConnectionCardControl", connection))
        {
            responsiveLibrary.GetType().GetField("_viewMode", Members)!.SetValue(responsiveLibrary, listMode);
            row.GetType().GetMethod("SetViewMode")!.Invoke(row, new[] { listMode });
            var items = (FlowLayoutPanel)Field(responsiveLibrary, "_itemsPanel");
            items.Controls.Add(row);
            foreach (var viewport in new[] { 640, 800, 1000, 1500 })
            {
                responsiveLibrary.Size = new Size(viewport, 480);
                responsiveLibrary.PerformLayout();
                responsiveLibrary.GetType().GetMethod("UpdateCardSizes", Members)!.Invoke(responsiveLibrary, null);
                Check(row.Width + row.Margin.Horizontal <= items.ClientSize.Width - items.Padding.Horizontal,
                    "List must not force a 1200px horizontal overflow at viewport " + viewport);
            }
        }
        var tokens = App.GetType("PureRDP.UI.UiDesignTokens")!;
        var accent = (Color)tokens.GetField("Accent")!.GetValue(null)!;
        Check(Contrast(accent, Color.White) >= 4.5, "Primary button contrast must meet AA.");
        var muted = (Color)tokens.GetField("TextMuted")!.GetValue(null)!;
        var soft = (Color)tokens.GetField("SurfaceSoft")!.GetValue(null)!;
        Check(Contrast(muted, soft) >= 4.5, "Helper and disabled text contrast must meet AA.");
        Color Token(string name) => (Color)tokens.GetField(name)!.GetValue(null)!;
        foreach (var foreground in new[] { "TextPrimary", "TextSecondary", "TextMuted" })
        foreach (var background in new[] { "PageBackground", "Surface", "SurfaceSubtle", "SurfaceSoft", "AccentSoft" })
            Check(Contrast(Token(foreground), Token(background)) >= 4.5,
                $"Unreadable color pairing: {foreground} / {background}.");
        Check(Contrast(Token("BorderStrong"), Token("SurfaceSubtle")) >= 3,
            "Input boundaries must remain visible on pale mint surfaces.");
        Check(Contrast(Token("Accent"), Token("AccentSoft")) >= 4.5, "Selected dropdown text must remain readable.");
        Check(Contrast(Token("Warning"), Token("Surface")) >= 4.5, "Small warning status text must remain readable.");
        Check(Token("Surface").ToArgb() == Color.White.ToArgb() &&
            Token("PageBackground").ToArgb() == Color.FromArgb(241, 249, 246).ToArgb(),
            "The reference palette must retain white cards and a pale mint page.");
        Check(Token("HeroSky").ToArgb() == Color.FromArgb(218, 244, 252).ToArgb() &&
            Token("HeroGrass").ToArgb() == Color.FromArgb(226, 245, 225).ToArgb(),
            "The console header must use the reference sky-to-mint gradient.");
        Console.WriteLine($"PASS: {_checks} UI checks; rendered native controls, no login, data deletion or settings save.");
    }
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte b) { var c = b / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double L(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        var x = L(a); var y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    private static void TestDeviceIsolation()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var applicationType = App.GetType("PureRDP.Configuration.ApplicationSettings")!;
        var profileType = App.GetType("PureRDP.Configuration.ConnectionSettings")!;
        var commonType = App.GetType("PureRDP.Configuration.GlobalRdpProxyPreferences")!;
        var proxyService = App.GetType("PureRDP.Services.RdpProxySettingsService")!;
        var fileService = App.GetType("PureRDP.Services.RdpFileService")!;
        var appA = Activator.CreateInstance(applicationType, true)!;
        var appB = Activator.CreateInstance(applicationType, true)!;
        var a = applicationType.GetProperty("Connection")!.GetValue(appA)!;
        var b = applicationType.GetProperty("Connection")!.GetValue(appB)!;
        profileType.GetProperty("Hostname")!.SetValue(a, "device-a.example.invalid");
        profileType.GetProperty("Hostname")!.SetValue(b, "device-b.example.invalid");
        profileType.GetProperty("Width")!.SetValue(a, 1280);
        profileType.GetProperty("Height")!.SetValue(a, 720);
        profileType.GetProperty("Width")!.SetValue(b, 1600);
        profileType.GetProperty("Height")!.SetValue(b, 1000);
        var directory = Path.Combine("outputs", "ci-tests", "device-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pathA = Path.Combine(directory, "a.rdp");
        var pathB = Path.Combine(directory, "b.rdp");
        try
        {
            fileService.GetMethod("SaveToFile", statics)!.Invoke(null, new[] { a, pathA });
            fileService.GetMethod("SaveToFile", statics)!.Invoke(null, new[] { b, pathB });
            var bytesB = File.ReadAllBytes(pathB);
            var external = profileType.GetMethod("Clone")!.Invoke(a, null)!;
            var preferences = Activator.CreateInstance(commonType, true)!;
            commonType.GetProperty("UseCommonSettings")!.SetValue(preferences, true);
            var common = profileType.GetMethod("Clone")!.Invoke(b, null)!;
            profileType.GetProperty("Width")!.SetValue(common, 1920);
            commonType.GetProperty("Connection")!.SetValue(preferences, common);
            proxyService.GetMethod("Apply", statics)!.Invoke(null, new[] { external, preferences });
            Check((int)profileType.GetProperty("Width")!.GetValue(a)! == 1280 && (int)profileType.GetProperty("Width")!.GetValue(b)! == 1600,
                "Applying external common settings must not mutate either saved device object.");
            applicationType.GetProperty("Connection")!.SetValue(appA, external);
            applicationType.GetProperty("IsExternalConnection")!.SetValue(appA, true);
            Check(!(bool)applicationType.GetProperty("CanAutoSaveDeviceProfile", Members)!.GetValue(appA)!,
                "An external connection using common settings must never automatically overwrite its matching saved device.");
            Check((bool)applicationType.GetProperty("CanAutoSaveDeviceProfile", Members)!.GetValue(appB)!,
                "An ordinary device connection must retain its own persistence path.");
            profileType.GetProperty("Width")!.SetValue(a, 1440);
            fileService.GetMethod("SaveToFile", statics)!.Invoke(null, new[] { a, pathA });
            var reloadedB = fileService.GetMethod("ReadFromFile", statics)!.Invoke(null, new[] { pathB })!;
            Check(File.ReadAllBytes(pathB).SequenceEqual(bytesB) && (int)profileType.GetProperty("Width")!.GetValue(reloadedB)! == 1600,
                "Saving device A's resolution must leave device B's file byte-for-byte unchanged.");
            var privatePaths = (IEnumerable<string>)App.GetType("PureRDP.Services.RecentConnectionsService")!
                .GetMethod("GetCandidateDirectories", statics)!.Invoke(null, null)!;
            var privateFile = Path.Combine(privatePaths.First(), "fixture.rdp");
            var isSaved = App.GetType("PureRDP.Services.RecentConnectionsService")!.GetMethod("IsSavedConnectionFile", statics)!;
            Check((bool)isSaved.Invoke(null, new object[] { privateFile })! && !(bool)isSaved.Invoke(null, new object[] { Path.GetFullPath(pathA) })!,
                "Saved device files and external files must have distinct preference scopes.");
        }
        finally
        {
            foreach (var path in new[] { pathA, pathB }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
