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
        using var global = (Form)New("UI.GlobalSettingsForm", settings);
        var pages = (IList)Field(global, "_pages");
        Check(pages.Count == 8, "Global settings must expose all eight pages.");
        foreach (Control page in pages)
        {
            global.GetType().GetMethod("SelectPage", Members)!.Invoke(global, new object[] { page });
            Render(global, page.GetType().Name);
        }
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
        using var reconnect = (Form)New("UI.RdpReconnectDialog", "示例设备");
        Render(reconnect, "Reconnect");
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
        var connection = New("Configuration.ConnectionSettings");
        connection.GetType().GetProperty("DisplayName")!.SetValue(connection, "工程工作站");
        connection.GetType().GetProperty("Hostname")!.SetValue(connection, "192.0.2.18:3389");
        using var card = (Control)New("UI.ConnectionCardControl", connection);
        canvas.Controls.Clear(); canvas.Controls.Add(card); card.Location = new Point(24, 24);
        Render(canvas, "DeviceCard");
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
                Check(column.Width > 0 && column.Right < actions.Left && column.Top == 14,
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
}
