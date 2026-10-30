using PureRDP.Configuration;
using PureRDP.Configuration.Enums;
using PureRDP.Extensions;
using PureRDP.Platform;
using PureRDP.Services;
using PureRDP.UI.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PureRDP.UI;

public partial class FormSettings : Form
{
    private readonly Size _currentRdpSize;
    private readonly TextBox _deviceName = new() { PlaceholderText = "例如：办公室电脑（选填）" };
    private readonly ComboBox _tileScale = new ModernComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _viewScale = new ModernComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _keyboardModeWindowFullscreen = new ModernComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _keyboardModeFullscreen = new ModernComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _keyboardModeWindowFullscreenLabel = new() { AutoSize = true, Text = "全屏窗口：" };
    private readonly Label _keyboardModeFullscreenLabel = new() { AutoSize = true, Text = "真全屏：" };
    private readonly CheckBox _hideControlBarWindowed = new SettingsCheckBox() { AutoSize = true, Text = "普通窗口（含全屏窗口）隐藏控制栏" };
    private readonly CheckBox _hideControlBarFullscreen = new SettingsCheckBox() { AutoSize = true, Text = "真全屏隐藏控制栏" };
    private static readonly List<Size> _displayResolutions = Interop.GetAvailableDisplayResolutions();
    private bool _loadingConnectionSettings;
    private bool _credentialHintLoaded;
    private string _credentialHintHostname = string.Empty;
    private string? _savedCredentialUsername;
    private int _credentialHintGeneration;
    private int _recentConnectionsGeneration;
    private bool _nativeUsernamePending;
    private readonly Dictionary<TextBox, SettingsTextField> _textFields = new();

    internal ApplicationSettings UpdatedSettings { get; private set; }
    internal bool ConnectRequested { get; private set; }
    private readonly bool _commonSettings;

    internal FormSettings(ApplicationSettings settings, Icon? icon, Size currentRdpSize)
        : this(settings, icon, currentRdpSize, false) { }

    internal FormSettings(ApplicationSettings settings, Icon? icon, Size currentRdpSize, bool commonSettings)
    {
        _commonSettings = commonSettings;
        ArgumentNullException.ThrowIfNull(settings);
        _currentRdpSize = currentRdpSize;

        InitializeComponent();
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        SuspendLayout();
        try
        {
            cmbHostname.Leave += cmbHostname_Leave;
            AddKeyboardModeFields();
            RemoveLegacyConnectionBarSettings();
            ApplyDesignSystem();
            AddDeviceNameField();
            AddTileScaleField();
            AddControlBarFields();
            InitializeEnumComboBoxes();

            Icon = icon;
            UpdatedSettings = new ApplicationSettings
            {
                Connection = settings.Connection.Clone(), Window = settings.Window,
                NoAutoConnect = settings.NoAutoConnect, NoCloseOnDisconnect = settings.NoCloseOnDisconnect,
                RdpFilePath = settings.RdpFilePath, EditRdpFile = settings.EditRdpFile,
                IsExternalConnection = settings.IsExternalConnection,
            };
            btnConnect.DialogResult = DialogResult.None;
            btnConnect.Click += (_, _) => CompleteSettings(connect: true);
            LoadConnectionSettingsToUi(UpdatedSettings.Connection);
            BuildSettingsLayout();
        }
        finally
        {
            ResumeLayout(true);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_commonSettings) return;
        _ = QueueCredentialHintRefreshAsync();
        _ = QueueRecentConnectionsRefreshAsync();
    }

    private void AddTileScaleField()
    {
        lblScaleFactor.Text = "DPI 设置：";
        var gap = cmbColorDepth.Top - cmbScaleFactor.Top;
        var top = chkAutoMaximizeWindow.Top;
        foreach (Control control in grpDisplay.Controls)
            if (control.Top >= top) control.Top += gap * 2;
        grpDisplay.Height += gap * 2;
        grpExperience.Top += gap * 2;
        grpServerAuthentication.Top += gap * 2;
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + gap);
        _viewScale.Font = cmbScaleFactor.Font;
        _viewScale.SetBounds(cmbScaleFactor.Left, top, cmbScaleFactor.Width, cmbScaleFactor.Height);
        grpDisplay.Controls.Add(new Label { Text = "默认画面缩放：", AutoSize = true,
            Font = lblScaleFactor.Font, ForeColor = lblScaleFactor.ForeColor,
            Location = new Point(lblScaleFactor.Left, top + 3) });
        grpDisplay.Controls.Add(_viewScale);
        top += gap;
        _tileScale.Font = cmbScaleFactor.Font;
        _tileScale.SetBounds(cmbScaleFactor.Left, top, cmbScaleFactor.Width, cmbScaleFactor.Height);
        grpDisplay.Controls.Add(new Label { Text = "宫格画面缩放：", AutoSize = true,
            Font = lblScaleFactor.Font, ForeColor = lblScaleFactor.ForeColor,
            Location = new Point(lblScaleFactor.Left, top + 3) });
        grpDisplay.Controls.Add(_tileScale);
        _tileScale.Items.Add(new ComboBoxItem<uint?>(0, "适应当前窗口"));
        _viewScale.Items.Add(new ComboBoxItem<uint?>(0, "适应当前窗口"));
        foreach (uint scale in new uint[] { 50, 60, 70, 80, 90, 100, 125, 150, 200 })
        {
            _tileScale.Items.Add(new ComboBoxItem<uint?>(scale, $"{scale}%"));
            _viewScale.Items.Add(new ComboBoxItem<uint?>(scale, $"{scale}%"));
        }
    }

    private void RemoveLegacyConnectionBarSettings()
    {
        // PureRDP uses its own in-window control strip and always disables the
        // native RDP connection bar.  Remove the obsolete controls from the
        // settings surface so users cannot change a setting that has no
        // effect.
        foreach (var control in new Control[]
                 {
                     chkDisplayConnectionBar,
                     chkPinConnectionBar,
                     comboBox1,
                 })
        {
            grpExperience.Controls.Remove(control);
            control.Dispose();
        }
    }

    private void AddDeviceNameField()
    {
        var gap = Math.Max(32, cmbUsername.Top - cmbHostname.Top);
        var top = cmbHostname.Top;
        foreach (Control control in grpConnection.Controls) control.Top += gap;
        grpConnection.Height += gap;
        grpRedirection.Top += gap;
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + gap);
        grpExperience.Height = grpExperience.Controls.Cast<Control>().Max(c => c.Bottom) + 16;
        grpServerAuthentication.Top = grpExperience.Bottom + 8;
        var label = new Label { Text = "设备名称：", AutoSize = true,
            Location = new Point(8, top + 3), Font = Font, ForeColor = UiDesignTokens.TextSecondary };
        _deviceName.SetBounds(cmbHostname.Left, top, cmbHostname.Width, cmbHostname.Height);
        _deviceName.Font = Font;
        _deviceName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        grpConnection.Controls.Add(label);
        grpConnection.Controls.Add(_deviceName);
    }

    private void AddControlBarFields()
    {
        var oldHeight = grpExperience.Height;
        var top = grpExperience.Controls.Cast<Control>().Max(control => control.Bottom) + 7;
        _hideControlBarWindowed.Location = new Point(8, top);
        _hideControlBarFullscreen.Location = new Point(8, top + 25);
        foreach (var checkBox in new[] { _hideControlBarWindowed, _hideControlBarFullscreen })
        {
            checkBox.Font = new Font(UiDesignTokens.FontFamily, 9F);
            checkBox.ForeColor = UiDesignTokens.TextSecondary;
            checkBox.BackColor = UiDesignTokens.Surface;
            grpExperience.Controls.Add(checkBox);
        }

        var newHeight = Math.Max(oldHeight, _hideControlBarFullscreen.Bottom + 12);
        var delta = newHeight - oldHeight;
        grpExperience.Height = newHeight;
        grpServerAuthentication.Top += delta;
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + delta);
    }

    private void ApplyDesignSystem()
    {
        BackColor = UiDesignTokens.PageBackground;
        ForeColor = UiDesignTokens.TextPrimary;
        Font = new Font(UiDesignTokens.FontFamily, 9F);

        var groups = new[]
        {
            grpConnection,
            grpDisplay,
            grpRedirection,
            grpServerAuthentication,
            grpExperience,
        };
        foreach (var group in groups)
        {
            group.BackColor = UiDesignTokens.Surface;
            group.ForeColor = UiDesignTokens.TextPrimary;
            group.Font = new Font(UiDesignTokens.FontFamily, 9F, FontStyle.Bold);
        }

        foreach (var control in EnumerateControls(this))
        {
            if (control is Label label && control is not LinkLabel)
            {
                label.ForeColor = UiDesignTokens.TextSecondary;
                label.Font = new Font(UiDesignTokens.FontFamily, 9F);
            }
            else if (control is LinkLabel link)
            {
                link.LinkColor = UiDesignTokens.Accent;
                link.ActiveLinkColor = UiDesignTokens.AccentHover;
                link.VisitedLinkColor = UiDesignTokens.Accent;
                link.Font = new Font(UiDesignTokens.FontFamily, 9F, FontStyle.Underline);
            }
            else if (control is TextBox textBox)
            {
                textBox.BackColor = UiDesignTokens.Surface;
                textBox.ForeColor = UiDesignTokens.TextPrimary;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                textBox.Font = new Font(UiDesignTokens.FontFamily, 9F);
            }
            else if (control is ComboBox comboBox)
            {
                comboBox.BackColor = UiDesignTokens.SurfaceSubtle;
                comboBox.ForeColor = UiDesignTokens.TextPrimary;
                comboBox.Font = new Font(UiDesignTokens.FontFamily, 9F);
                comboBox.FormattingEnabled = true;
            }
            else if (control is CheckBox checkBox)
            {
                checkBox.BackColor = UiDesignTokens.Surface;
                checkBox.ForeColor = UiDesignTokens.TextSecondary;
                checkBox.Font = new Font(UiDesignTokens.FontFamily, 9F);
            }
        }

        StyleButton(btnSave, UiDesignTokens.Accent, Color.White, UiDesignTokens.AccentHover);
        StyleButton(btnConnect, UiDesignTokens.Accent, Color.White, UiDesignTokens.AccentHover);
        StyleButton(btnSaveAs, UiDesignTokens.Surface, UiDesignTokens.TextSecondary, UiDesignTokens.SurfaceSubtle);
        StyleButton(btnCancel, UiDesignTokens.Surface, UiDesignTokens.TextSecondary, UiDesignTokens.SurfaceSubtle);

        btnSaveAs.Width = 96;
        btnSave.Width = 84;
        btnCancel.Width = 84;
        btnConnect.Width = 84;
        btnSaveAs.Height = UiDesignTokens.ControlHeight;
        btnSave.Height = UiDesignTokens.ControlHeight;
        btnCancel.Height = UiDesignTokens.ControlHeight;
        btnConnect.Height = UiDesignTokens.ControlHeight;
        panelBadgeColor.Cursor = Cursors.Hand;
        panelBadgeColor.AccessibleName = "选择设备标记颜色";
        panelBadgeColor.AccessibleRole = AccessibleRole.PushButton;
    }

    private static void StyleButton(Button button, Color background, Color foreground, Color hoverBackground)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = background == UiDesignTokens.Accent ? 0 : 1;
        button.FlatAppearance.BorderColor = UiDesignTokens.BorderStrong;
        button.FlatAppearance.MouseOverBackColor = hoverBackground;
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(hoverBackground, 0.08F);
        button.Font = new Font(UiDesignTokens.FontFamily, 9F, FontStyle.Bold);
        button.Padding = new Padding(8, 0, 8, 0);
        button.Cursor = Cursors.Hand;
        button.TabStop = true;
        button.AccessibleRole = AccessibleRole.PushButton;
    }

    private void BuildSettingsLayout()
    {
        // Reparent the existing controls, preserving values, event handlers,
        // credentials and all connection-setting behavior.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        ClientSize = new Size(1000, 780);
        MinimumSize = new Size(780, 600);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            BackColor = UiDesignTokens.PageBackground, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        shell.Controls.Add(new Label { Text = "设备连接设置", Dock = DockStyle.Fill,
            Padding = new Padding(24, 16, 0, 0), Font = new Font(UiDesignTokens.FontFamily, 16F, FontStyle.Bold),
            BackColor = UiDesignTokens.SurfaceSubtle, ForeColor = UiDesignTokens.TextPrimary }, 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
        var columns = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        left.BackColor = right.BackColor = columns.BackColor = UiDesignTokens.PageBackground;
        foreach (var editor in new[] { _deviceName, tbPassword, tbRedirectDrives, tbRedirectCameras, tbBadgeColor })
        {
            var parent = editor.Parent!;
            var field = new SettingsTextField(editor) { TabStop = false };
            _textFields.Add(editor, field);
            parent.Controls.Add(field);
        }
        foreach (var group in new[] { grpConnection, grpRedirection, grpDisplay, grpExperience, grpServerAuthentication })
        {
            group.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            group.Margin = new Padding(8, 0, 8, 16);
            group.Height = Math.Max(group.Height, group.Controls.Cast<Control>().Max(control => control.Bottom) + 18);
        }
        left.Controls.AddRange(_commonSettings ? new Control[] { grpRedirection } : new Control[] { grpConnection, grpRedirection });
        if (_commonSettings)
        {
            grpConnection.Visible = false;
            grpServerAuthentication.Controls.Add(chkEnableCredSsp);
        }
        right.Controls.AddRange(new Control[] { grpDisplay, grpExperience, grpServerAuthentication });
        left.TabIndex = 0;
        right.TabIndex = 1;
        grpConnection.TabIndex = grpDisplay.TabIndex = 0;
        grpRedirection.TabIndex = grpExperience.TabIndex = 1;
        grpServerAuthentication.TabIndex = 2;
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);
        scroll.Controls.Add(columns);
        shell.Controls.Add(scroll, 0, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(18, 10, 18, 10), BackColor = UiDesignTokens.Surface };
        foreach (var button in new[] { btnConnect, btnSave, btnCancel, btnSaveAs })
        {
            button.Anchor = AnchorStyles.None;
            button.Margin = new Padding(8, 0, 0, 0);
            footer.Controls.Add(button);
        }
        shell.Controls.Add(footer, 0, 2);
        Controls.Add(shell);
        shell.BringToFront();
        if (_commonSettings)
        {
            ((Label)shell.GetControlFromPosition(0, 0)!).Text = ".rdp 通用连接设置";
            btnConnect.Visible = btnSaveAs.Visible = false;
            Text = ".rdp 通用连接设置 · PureRDP";
        }
        var arranging = false;
        void LayoutGroups()
        {
            if (arranging) return;
            arranging = true;
            try
            {
            var scale = DeviceDpi / 96F;
            int Px(int value) => (int)Math.Round(value * scale);
            // Keep the original left/right grouping at every window size.
            // A narrow viewport scrolls rather than moving display settings
            // underneath the connection/resource fields.
            scroll.AutoScrollMinSize = new Size(Px(760), 0);
            columns.MinimumSize = new Size(Px(728), 0);
            var width = Math.Max(Px(348), columns.ClientSize.Width / 2 - Px(16));
            foreach (var group in new[] { grpConnection, grpRedirection, grpDisplay, grpExperience, grpServerAuthentication })
            {
                group.Width = width;
                LayoutDeviceGroup(group);
            }
            }
            finally { arranging = false; }
        }
        columns.SizeChanged += (_, _) => LayoutGroups();
        LayoutGroups();
    }

    private void LayoutDeviceGroup(GroupBox group)
    {
        var scale = DeviceDpi / 96F;
        int Px(int value) => (int)Math.Round(value * scale);
        var inset = Px(16);
        var inputX = Px(148);
        var inputWidth = Math.Max(Px(120), group.ClientSize.Width - inputX - inset);
        var fieldHeight = Math.Max(Px(26), cmbScreenMode.PreferredHeight);
        var rowHeight = Math.Max(Px(32), fieldHeight + Px(6));
        var top = Math.Max(Px(34), group.Font.Height + Px(16));
        var row = 0;
        Control Field(Control control) => control is TextBox text ? _textFields[text] : control;
        Label NamedLabel(string caption) => group.Controls.OfType<Label>().First(label => label.Text == caption);
        void Position(Control control, int x, int y, int width, int height)
        {
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            control.SetBounds(x, y, width, height);
        }
        void LabelAt(Label label, int y)
        {
            label.AutoSize = false;
            label.TextAlign = ContentAlignment.MiddleLeft;
            Position(label, inset, y, inputX - inset - Px(8), rowHeight);
        }
        void Pair(Label label, Control control)
        {
            var y = top + row++ * rowHeight;
            LabelAt(label, y);
            Position(Field(control), inputX, y + (rowHeight - fieldHeight) / 2, inputWidth, fieldHeight);
            Field(control).TabIndex = row * 4;
        }
        void Option(CheckBox check)
        {
            var y = top + row++ * rowHeight;
            check.AutoSize = true;
            var preferred = check.GetPreferredSize(Size.Empty);
            Position(check, inset, y + (rowHeight - preferred.Height) / 2, preferred.Width, preferred.Height);
            check.TabIndex = row * 4;
        }
        if (group == grpConnection)
        {
            Pair(NamedLabel("设备名称："), _deviceName);
            Pair(lblHostname, cmbHostname);
            Pair(lblUsername, cmbUsername);
            Pair(lblPassword, tbPassword);
            var y = top + row++ * rowHeight;
            LabelAt(lbCredentials, y);
            foreach (var link in new[] { lbCredentialSave, lbCredentialRemove })
            {
                link.AutoSize = false;
                link.TextAlign = ContentAlignment.MiddleLeft;
                Position(link, inputX + (link == lbCredentialRemove ? Px(72) : 0), y, Px(64), rowHeight);
                link.TabIndex = row * 4 + (link == lbCredentialRemove ? 1 : 0);
            }
            Option(chkEnableCredSsp);
        }
        else if (group == grpRedirection)
        {
            Pair(lblKeyboardMode, cmbKeyboardMode);
            Pair(_keyboardModeWindowFullscreenLabel, _keyboardModeWindowFullscreen);
            Pair(_keyboardModeFullscreenLabel, _keyboardModeFullscreen);
            Pair(lblAudioPlaybackMode, cmbAudioMode);
            Pair(lblRedirectAudioCapture, cmbRedirectAudioCapture);
            Pair(lblRedirectDrives, tbRedirectDrives);
            Option(chkRedirectClipboard);
            Option(chkRedirectPrinters);
            Option(chkRedirectSmartCards);
            Pair(lblRedirectCameras, tbRedirectCameras);
        }
        else if (group == grpDisplay)
        {
            Pair(lblScreenMode, cmbScreenMode);
            var y = top + row++ * rowHeight;
            LabelAt(lblWidthHeight, y);
            var half = (inputWidth - Px(24)) / 2;
            Position(cmbWidth, inputX, y + (rowHeight - fieldHeight) / 2, half, fieldHeight);
            lblHeight.AutoSize = false;
            lblHeight.TextAlign = ContentAlignment.MiddleCenter;
            Position(lblHeight, inputX + half, y, Px(24), rowHeight);
            Position(cmbHeight, inputX + half + Px(24), y + (rowHeight - fieldHeight) / 2, half, fieldHeight);
            cmbWidth.TabIndex = row * 4;
            cmbHeight.TabIndex = row * 4 + 1;
            Pair(NamedLabel("默认画面缩放："), _viewScale);
            Pair(NamedLabel("宫格画面缩放："), _tileScale);
            Option(chkAutoMaximizeWindow);
            y = top + row++ * rowHeight;
            LabelAt(lblSizing, y);
            var checkHeight = chkAutoResize.GetPreferredSize(Size.Empty).Height;
            Position(chkAutoResize, inputX, y + (rowHeight - checkHeight) / 2,
                chkAutoResize.GetPreferredSize(Size.Empty).Width, checkHeight);
            Position(chkSmartSizing, inputX + chkAutoResize.Width + Px(12), chkAutoResize.Top,
                chkSmartSizing.GetPreferredSize(Size.Empty).Width, checkHeight);
            chkAutoResize.TabIndex = row * 4;
            chkSmartSizing.TabIndex = row * 4 + 1;
            Pair(lblScaleFactor, cmbScaleFactor);
            Pair(lblColorDepth, cmbColorDepth);
            Option(chkEnableCompression);
            Option(chkEnableBitmapPersistence);
        }
        else if (group == grpExperience)
        {
            if (!_commonSettings)
            {
                var y = top + row++ * rowHeight;
                LabelAt(lbBadgeColor, y);
                var swatchWidth = Px(52);
                Position(Field(tbBadgeColor), inputX, y + (rowHeight - fieldHeight) / 2,
                    inputWidth - swatchWidth - Px(12), fieldHeight);
                Field(tbBadgeColor).TabIndex = row * 4;
                Position(panelBadgeColor, group.Width - inset - swatchWidth,
                    y + (rowHeight - fieldHeight) / 2, swatchWidth, fieldHeight);
            }
            else
            {
                lbBadgeColor.Visible = tbBadgeColor.Visible = panelBadgeColor.Visible = false;
                Field(tbBadgeColor).Visible = false;
            }
            Option(_hideControlBarWindowed);
            Option(_hideControlBarFullscreen);
        }
        else if (group == grpServerAuthentication)
        {
            Pair(lblAuthenticationLevel, cmbAuthenticationLevel);
            if (_commonSettings) Option(chkEnableCredSsp);
        }
        group.Height = top + row * rowHeight + Px(12);
    }

    private static IEnumerable<Control> EnumerateControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateControls(child))
                yield return descendant;
        }
    }
    private void InitializeEnumComboBoxes()
    {
        InitializeKeyboardModeCombo(cmbKeyboardMode);
        InitializeKeyboardModeCombo(_keyboardModeWindowFullscreen);
        InitializeKeyboardModeCombo(_keyboardModeFullscreen);

        cmbAudioMode.Items.Add(new ComboBoxItem<AudioPlaybackMode?>(AudioPlaybackMode.PlayOnLocal, "在本地播放"));
        cmbAudioMode.Items.Add(new ComboBoxItem<AudioPlaybackMode?>(AudioPlaybackMode.PlayOnRemote, "在远端播放"));
        cmbAudioMode.Items.Add(new ComboBoxItem<AudioPlaybackMode?>(AudioPlaybackMode.DoNotPlay, "不播放"));
        cmbAudioMode.SelectedIndex = 0;

        cmbRedirectAudioCapture.Items.Add(new ComboBoxItem<bool?>(false, "不使用麦克风"));
        cmbRedirectAudioCapture.Items.Add(new ComboBoxItem<bool?>(true, "使用本地麦克风"));
        cmbRedirectAudioCapture.SelectedIndex = 0;

        cmbScreenMode.Items.Add(new ComboBoxItem<ScreenModeOption>(ScreenModeOption.Windowed, "窗口模式"));
        cmbScreenMode.Items.Add(new ComboBoxItem<ScreenModeOption>(ScreenModeOption.FullScreen, "全屏"));
        cmbScreenMode.Items.Add(new ComboBoxItem<ScreenModeOption>(ScreenModeOption.UseAllMonitors, "使用所有显示器"));
        cmbScreenMode.SelectedIndex = 0;

        var resolutionsWidths = _displayResolutions.Select(res => res.Width).Distinct().ToList();
        var resolutionsHeights = _displayResolutions.Select(res => res.Height).Distinct().ToList();
        foreach (var width in resolutionsWidths)
            cmbWidth.Items.Add(new ComboBoxItem<int>(width, width.ToString()));
        foreach (var height in resolutionsHeights)
            cmbHeight.Items.Add(new ComboBoxItem<int>(height, height.ToString()));

        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(null, "跟随本地缩放"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(100, "100%"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(125, "125%"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(150, "150%"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(175, "175%"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(200, "200%"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(250, "250%"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(300, "300%"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(400, "400%"));
        cmbScaleFactor.Items.Add(new ComboBoxItem<uint?>(500, "500%"));
        cmbScaleFactor.SelectedIndex = 0;

        cmbColorDepth.Items.Add(new ComboBoxItem<int?>(15, "增强色（15 位）"));
        cmbColorDepth.Items.Add(new ComboBoxItem<int?>(16, "增强色（16 位）"));
        cmbColorDepth.Items.Add(new ComboBoxItem<int?>(24, "真彩色（24 位）"));
        cmbColorDepth.Items.Add(new ComboBoxItem<int?>(32, "最高质量（32 位）"));
        cmbColorDepth.SelectedIndex = 0;

        cmbAuthenticationLevel.Items.Add(new ComboBoxItem<AuthenticationLevel?>(AuthenticationLevel.ConnectWithoutWarning, "直接连接，不警告"));
        cmbAuthenticationLevel.Items.Add(new ComboBoxItem<AuthenticationLevel?>(AuthenticationLevel.DoNotConnect, "拒绝连接"));
        cmbAuthenticationLevel.Items.Add(new ComboBoxItem<AuthenticationLevel?>(AuthenticationLevel.WarnUser, "提示用户"));
        cmbAuthenticationLevel.Items.Add(new ComboBoxItem<AuthenticationLevel?>(AuthenticationLevel.NoRequirement, "不作要求"));
        cmbAuthenticationLevel.SelectedIndex = 0;
    }

    private void AddKeyboardModeFields()
    {
        const int rowHeight = 29;
        var firstInsertedRow = cmbAudioMode.Top;
        foreach (Control control in grpRedirection.Controls)
            if (control != cmbKeyboardMode && control.Top >= firstInsertedRow)
                control.Top += rowHeight * 2;

        lblKeyboardMode.Text = "普通窗口：";
        _keyboardModeWindowFullscreen.SetBounds(cmbKeyboardMode.Left, cmbKeyboardMode.Top + rowHeight,
            cmbKeyboardMode.Width, cmbKeyboardMode.Height);
        _keyboardModeWindowFullscreenLabel.Location = new Point(lblKeyboardMode.Left,
            _keyboardModeWindowFullscreen.Top + 3);
        _keyboardModeFullscreen.SetBounds(cmbKeyboardMode.Left, cmbKeyboardMode.Top + rowHeight * 2,
            cmbKeyboardMode.Width, cmbKeyboardMode.Height);
        _keyboardModeFullscreenLabel.Location = new Point(lblKeyboardMode.Left,
            _keyboardModeFullscreen.Top + 3);

        grpRedirection.Controls.Add(_keyboardModeWindowFullscreen);
        grpRedirection.Controls.Add(_keyboardModeWindowFullscreenLabel);
        grpRedirection.Controls.Add(_keyboardModeFullscreen);
        grpRedirection.Controls.Add(_keyboardModeFullscreenLabel);
        grpRedirection.Height += rowHeight * 2;
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + rowHeight * 2);
    }

    private static void InitializeKeyboardModeCombo(ComboBox comboBox)
    {
        comboBox.Items.Add(new ComboBoxItem<KeyboardMode?>(KeyboardMode.OnLocalComputer, "在本地计算机上"));
        comboBox.Items.Add(new ComboBoxItem<KeyboardMode?>(KeyboardMode.OnRemoteComputer, "在远端计算机上"));
        comboBox.SelectedIndex = 0;
    }

    private void LoadConnectionSettingsToUi(ConnectionSettings settings)
    {
        _loadingConnectionSettings = true;
        try
        {
            _credentialHintGeneration++;
            _credentialHintLoaded = false;
            _savedCredentialUsername = null;
            Text = UpdatedSettings.RdpFilePath != null
                ? $"设置（{Path.GetFileName(UpdatedSettings.RdpFilePath)}）- PureRDP"
                : "设置 - PureRDP";

            // Recent files are filled after the dialog is visible. Reading a
            // damaged or locked RDP file must never delay or break dialog creation.
            cmbHostname.SelectedIndex = -1;
            cmbUsername.SelectedIndex = -1;

            cmbHostname.Text = settings.Hostname ?? string.Empty;
            _deviceName.Text = settings.DisplayName ?? string.Empty;
            cmbUsername.Text = settings.Username.CombineUsernameAndDomain(settings.Domain) ?? string.Empty;
            chkEnableCredSsp.CheckState = GetCheckState(settings.EnableCredSsp);

            SetComboBoxValue(cmbKeyboardMode, settings.KeyboardModeWindowed);
            SetComboBoxValue(_keyboardModeWindowFullscreen, settings.KeyboardModeWindowFullscreen);
            SetComboBoxValue(_keyboardModeFullscreen, settings.KeyboardModeFullscreen);
            SetComboBoxValue(cmbAudioMode, settings.AudioPlaybackMode);
            SetComboBoxValue(cmbRedirectAudioCapture, settings.RedirectAudioCapture);
            tbRedirectDrives.Text = settings.RedirectDrives ?? string.Empty;
            tbRedirectCameras.Text = settings.RedirectCameras ?? string.Empty;
            chkRedirectClipboard.CheckState = GetCheckState(settings.RedirectClipboard);
            chkRedirectPrinters.CheckState = GetCheckState(settings.RedirectPrinters);
            chkRedirectSmartCards.CheckState = GetCheckState(settings.RedirectSmartCards);

            SetScreenModeComboBoxToUi(settings.ScreenMode, settings.UseAllMonitors);
            chkAutoMaximizeWindow.CheckState = GetCheckState(settings.AutoMaximizeWindow);
            cmbWidth.Text = settings.Width.ToString();
            cmbHeight.Text = settings.Height.ToString();
            chkAutoResize.CheckState = GetCheckState(settings.AutoResize);
            chkSmartSizing.CheckState = GetCheckState(settings.SmartSizing);
            SetComboBoxValue(cmbScaleFactor, settings.ScaleFactor is < 100 ? null : settings.ScaleFactor);
            SetComboBoxValue<uint?>(_tileScale, settings.TileScaleFactor is > 200 ? 0u : settings.TileScaleFactor ?? 0u);
            SetComboBoxValue<uint?>(_viewScale, settings.ViewScaleFactor ?? 0u);
            SetComboBoxValue(cmbColorDepth, settings.ColorDepth);
            chkEnableCompression.CheckState = GetCheckState(settings.EnableCompression);
            chkEnableBitmapPersistence.CheckState = GetCheckState(settings.EnableBitmapPersistence);
            _hideControlBarWindowed.Checked = settings.ControlBarHiddenWindowed;
            _hideControlBarFullscreen.Checked = settings.ControlBarHiddenFullscreen;

            tbBadgeColor.Text = settings.BadgeColor?.ToColorString() ?? string.Empty;

            SetComboBoxValue(cmbAuthenticationLevel, settings.AuthenticationLevel);
        }
        finally
        {
            _loadingConnectionSettings = false;
            if (!IsDisposed)
            {
                try { UpdateSavedCredentialHints(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
            if (IsHandleCreated)
                _ = QueueCredentialHintRefreshAsync();
        }
    }

    private ConnectionSettings GetConnectionSettingsFromUi()
    {
        var screenModeOption = GetComboBoxValue<ScreenModeOption>(cmbScreenMode);
        var (screenMode, useAllMonitors) = GetScreenModeValuesFromUi(screenModeOption);

        var updatedConnectionSettings = UpdatedSettings.Connection.Clone();
        updatedConnectionSettings.DisplayName = string.IsNullOrWhiteSpace(_deviceName.Text) ? null :
            _deviceName.Text.Trim().Replace("\r", " ").Replace("\n", " ");
        var hostname = cmbHostname.Text.Trim();
        updatedConnectionSettings.Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname;

        var (username, domain) = cmbUsername.Text.ParseUsernameAndDomain();
        updatedConnectionSettings.Username = string.IsNullOrWhiteSpace(username) ? null : username;
        updatedConnectionSettings.Domain = string.IsNullOrWhiteSpace(domain) ? null : domain;
        updatedConnectionSettings.Password = string.IsNullOrWhiteSpace(tbPassword.Text) ? updatedConnectionSettings.Password : tbPassword.Text;
        if (!string.IsNullOrWhiteSpace(tbPassword.Text) ||
            !string.Equals(UpdatedSettings.Connection.Hostname, updatedConnectionSettings.Hostname, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(UpdatedSettings.Connection.Username.CombineUsernameAndDomain(UpdatedSettings.Connection.Domain),
                updatedConnectionSettings.Username.CombineUsernameAndDomain(updatedConnectionSettings.Domain), StringComparison.OrdinalIgnoreCase))
        {
            updatedConnectionSettings.EncryptedPassword51 = null;
            updatedConnectionSettings.NativeFallback?.CancelIfPending();
            updatedConnectionSettings.NativeFallback = null;
        }
        updatedConnectionSettings.EnableCredSsp = GetBoolValue(chkEnableCredSsp.CheckState);

        updatedConnectionSettings.KeyboardModeWindowed = GetComboBoxValue<KeyboardMode?>(cmbKeyboardMode);
        updatedConnectionSettings.KeyboardModeWindowFullscreen = GetComboBoxValue<KeyboardMode?>(_keyboardModeWindowFullscreen);
        updatedConnectionSettings.KeyboardModeFullscreen = GetComboBoxValue<KeyboardMode?>(_keyboardModeFullscreen);
        updatedConnectionSettings.AudioPlaybackMode = GetComboBoxValue<AudioPlaybackMode?>(cmbAudioMode);
        updatedConnectionSettings.RedirectAudioCapture = GetComboBoxValue<bool?>(cmbRedirectAudioCapture);
        updatedConnectionSettings.RedirectDrives = string.IsNullOrWhiteSpace(tbRedirectDrives.Text) ? null : tbRedirectDrives.Text;
        updatedConnectionSettings.RedirectCameras = string.IsNullOrWhiteSpace(tbRedirectCameras.Text) ? null : tbRedirectCameras.Text.Trim();
        updatedConnectionSettings.RedirectClipboard = GetBoolValue(chkRedirectClipboard.CheckState);
        updatedConnectionSettings.RedirectPrinters = GetBoolValue(chkRedirectPrinters.CheckState);
        updatedConnectionSettings.RedirectSmartCards = GetBoolValue(chkRedirectSmartCards.CheckState);

        updatedConnectionSettings.ScreenMode = screenMode;
        updatedConnectionSettings.UseAllMonitors = useAllMonitors;
        updatedConnectionSettings.Width = string.IsNullOrWhiteSpace(cmbWidth.Text) ? null : int.Parse(cmbWidth.Text);
        updatedConnectionSettings.Height = string.IsNullOrWhiteSpace(cmbHeight.Text) ? null : int.Parse(cmbHeight.Text);
        updatedConnectionSettings.AutoMaximizeWindow = GetBoolValue(chkAutoMaximizeWindow.CheckState);
        // Width/height define the initial connection window size. Dynamic
        // resolution remains available so a later manual window resize can
        // update the remote desktop resolution to match the new client area.
        updatedConnectionSettings.AutoResize = GetBoolValue(chkAutoResize.CheckState);
        updatedConnectionSettings.SmartSizing = GetBoolValue(chkSmartSizing.CheckState);
        updatedConnectionSettings.ScaleFactor = GetComboBoxValue<uint?>(cmbScaleFactor);
        updatedConnectionSettings.TileScaleFactor = GetComboBoxValue<uint?>(_tileScale);
        updatedConnectionSettings.ViewScaleFactor = GetComboBoxValue<uint?>(_viewScale);
        updatedConnectionSettings.ColorDepth = GetComboBoxValue<int?>(cmbColorDepth);
        updatedConnectionSettings.EnableCompression = GetBoolValue(chkEnableCompression.CheckState);
        updatedConnectionSettings.EnableBitmapPersistence = GetBoolValue(chkEnableBitmapPersistence.CheckState);
        updatedConnectionSettings.ControlBarHiddenWindowed = _hideControlBarWindowed.Checked;
        updatedConnectionSettings.ControlBarHiddenFullscreen = _hideControlBarFullscreen.Checked;

        updatedConnectionSettings.BadgeColor = string.IsNullOrWhiteSpace(tbBadgeColor.Text) ? null : tbBadgeColor.Text.ToColor();

        updatedConnectionSettings.AuthenticationLevel = GetComboBoxValue<AuthenticationLevel?>(cmbAuthenticationLevel);

        return updatedConnectionSettings;
    }

    private static (ScreenMode? screenMode, bool? useAllMonitors) GetScreenModeValuesFromUi(ScreenModeOption screenMode)
    {
        return screenMode switch
        {
            ScreenModeOption.Windowed => (ScreenMode.Windowed, false),
            ScreenModeOption.FullScreen => (ScreenMode.FullScreen, false),
            ScreenModeOption.UseAllMonitors => (ScreenMode.FullScreen, true),
            _ => (ScreenMode.Windowed, false),
        };
    }

    private void SetScreenModeComboBoxToUi(ScreenMode? screenMode, bool? useAllMonitors)
    {
        var screenModeOption = (screenMode, useAllMonitors) switch
        {
            (ScreenMode.FullScreen, true) => ScreenModeOption.UseAllMonitors,
            (ScreenMode.FullScreen, _) => ScreenModeOption.FullScreen,
            (ScreenMode.Windowed, _) => ScreenModeOption.Windowed,
            _ => ScreenModeOption.Windowed,
        };

        SetComboBoxValue(cmbScreenMode, screenModeOption);
        UpdateWindowMaximizeAvailability();
    }

    private void cmbScreenMode_SelectedIndexChanged(object? sender, EventArgs e)
        => UpdateWindowMaximizeAvailability();

    private void UpdateWindowMaximizeAvailability()
    {
        if (chkAutoMaximizeWindow == null || cmbScreenMode == null)
            return;

        var screenMode = GetComboBoxValue<ScreenModeOption>(cmbScreenMode);
        chkAutoMaximizeWindow.Enabled = screenMode == ScreenModeOption.Windowed;
    }

    private static CheckState GetCheckState(bool? value)
    {
        if (!value.HasValue)
            return CheckState.Indeterminate;
        return value.Value ? CheckState.Checked : CheckState.Unchecked;
    }

    private static bool? GetBoolValue(CheckState checkState)
    {
        return checkState switch
        {
            CheckState.Checked => true,
            CheckState.Unchecked => false,
            _ => null
        };
    }

    private static T? GetComboBoxValue<T>(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem<T> item)
            return item.Value;
        return default;

    }

    private static void SetComboBoxValue<T>(ComboBox comboBox, T value)
    {
        var indexToSelect = comboBox.Items
            .OfType<ComboBoxItem<T>>()
            .ToList()
            .FindIndex(item => item.Value?.Equals(value) == true);

        comboBox.SelectedIndex = Math.Max(0, indexToSelect);
    }

    private void UpdateSavedCredentialHints(bool refreshCredentials = false, bool fillEmptyUsername = true)
    {
        if (_commonSettings) return;
        var hostname = cmbHostname.Text.Trim();
        if (refreshCredentials || !_credentialHintLoaded ||
            !string.Equals(_credentialHintHostname, hostname, StringComparison.OrdinalIgnoreCase))
        {
            if (refreshCredentials)
            {
                _savedCredentialUsername = RdpCredentialService.CredentialsExist(hostname);
                _credentialHintLoaded = true;
            }
            else
            {
                // Do not call Credential Manager for every keystroke while a
                // hostname is being typed. The hint is refreshed when the
                // field loses focus or a recent endpoint is selected.
                _savedCredentialUsername = null;
                _credentialHintLoaded = false;
            }
            _credentialHintHostname = hostname;
        }

        var usernameIsEmpty = string.IsNullOrWhiteSpace(cmbUsername.Text);
        var savedUsername = _savedCredentialUsername;
        var savedCredentialsExists = savedUsername != null;
        if (fillEmptyUsername && usernameIsEmpty && savedCredentialsExists)
            using (new TextChangedEventSuppressor(cmbUsername, cmbUsername_TextChanged))
                cmbUsername.Text = savedUsername;

        var credentialsToSaveAvailable = !string.IsNullOrWhiteSpace(cmbHostname.Text) && !string.IsNullOrWhiteSpace(cmbUsername.Text);
        lbCredentialSave.Text = savedCredentialsExists ? "更新" : "保存";
        lbCredentialSave.Visible = credentialsToSaveAvailable;
        lbCredentialRemove.Visible = savedCredentialsExists;
        // Keep the password out of the form model while making an existing
        // protected credential visible as a privacy mask. PlaceholderText is
        // hidden automatically when the user focuses the box and starts
        // entering a replacement password.
        var passwordIsAlreadyConfigured = savedCredentialsExists ||
            !string.IsNullOrWhiteSpace(UpdatedSettings.Connection.Password);
        tbPassword.PlaceholderText = passwordIsAlreadyConfigured ? "●●●●●●" : string.Empty;
    }

    private void SaveSettingsToRdpFile(bool saveAs)
    {
        var connectionSettings = GetConnectionSettingsFromUi();
        var rdpFilePath = UpdatedSettings.RdpFilePath;
        var rdpFileNotExists = !File.Exists(UpdatedSettings.RdpFilePath);

        if (saveAs || rdpFileNotExists)
        {
            using var saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "远程桌面文件 (*.rdp)|*.rdp|所有文件 (*.*)|*.*";
            saveFileDialog.DefaultExt = "rdp";
            saveFileDialog.InitialDirectory = Path.GetDirectoryName(rdpFilePath);
            saveFileDialog.FileName = Path.GetFileName(rdpFilePath);
            saveFileDialog.Title = "导出远程桌面文件";

            if (saveFileDialog.ShowDialog(this) != DialogResult.OK)
                return;

            UpdatedSettings.RdpFilePath = saveFileDialog.FileName;
        }

        RdpFileService.SaveToFile(connectionSettings, UpdatedSettings.RdpFilePath!);
    }

    private static bool AllowDigitsOnly(char keyChar)
    {
        // backspace, delete, arrows via KeyDown, etc.
        var isControlKey = char.IsControl(keyChar);
        var isDigit = char.IsDigit(keyChar);
        return !isControlKey && !isDigit;
    }

    private void AllowDigitsOnly(ComboBox? combobox)
    {
        if (combobox == null)
            return;

        var original = combobox.Text;
        var filtered = new string(original.Where(char.IsDigit).ToArray());
        if (original == filtered)
        {
            AdjustWidthAndHeightValues();
            return;
        }

        // preserve caret position as well as possible
        var selStart = Math.Max(0, combobox.SelectionStart);
        combobox.Text = filtered;
        combobox.SelectionStart = Math.Min(filtered.Length, selStart);

        AdjustWidthAndHeightValues();
    }

    private void AdjustWidthAndHeightValues()
    {
        if (chkAutoResize.Checked)
            return;

        if (string.IsNullOrWhiteSpace(cmbWidth.Text))
        {
            cmbWidth.Text = _currentRdpSize.Width.ToString();
            if (cmbWidth.Focused)
                cmbWidth.SelectAll();
        }

        if (string.IsNullOrWhiteSpace(cmbHeight.Text))
        {
            cmbHeight.Text = _currentRdpSize.Height.ToString();
            if (cmbHeight.Focused)
                cmbHeight.SelectAll();
        }
    }

    private void RemoveRecent(ComboBoxItem<ConnectionSettings> selectedItem)
    {
        if (string.IsNullOrWhiteSpace(selectedItem.Value.Hostname))
            return;

        RecentConnectionsService.Remove(selectedItem.Value.Hostname);
        cmbHostname.Items.Remove(selectedItem);
        UpdateSavedCredentialHints();
    }

    private void ChangeColor()
    {
        using var colorDialog = new ColorDialog();
        colorDialog.Color = panelBadgeColor.BackColor;
        var palette = BadgeColorService.Palette.OrderBy(x => x.GetHue()).Select(color => color.R | (color.G << 8) | (color.B << 16)).ToArray();
        colorDialog.CustomColors = palette;
        if (colorDialog.ShowDialog() != DialogResult.OK)
            return;

        panelBadgeColor.BackColor = colorDialog.Color;
        tbBadgeColor.Text = panelBadgeColor.BackColor.ToColorString();
    }

    private void cmbWidth_KeyPress(object sender, KeyPressEventArgs e)
        => e.Handled = AllowDigitsOnly(e.KeyChar);

    private void cmbWidth_TextChanged(object sender, EventArgs e)
        => AllowDigitsOnly(sender as ComboBox);

    private void cmbHeight_KeyPress(object sender, KeyPressEventArgs e)
        => e.Handled = AllowDigitsOnly(e.KeyChar);

    private void cmbHeight_TextChanged(object sender, EventArgs e)
        => AllowDigitsOnly(sender as ComboBox);

    private void btnSaveAs_Click(object sender, EventArgs e)
        => SaveSettingsToRdpFile(true);

    private void chkAutoResize_CheckedChanged(object sender, EventArgs e)
    {
        if (chkAutoResize.Checked)
            chkSmartSizing.Checked = false;

        AdjustWidthAndHeightValues();
    }

    private void chkSmartSizing_CheckedChanged(object sender, EventArgs e)
    {
        if (chkSmartSizing.Checked)
            chkAutoResize.Checked = false;
    }

    private void btnSave_Click(object sender, EventArgs e)
        => CompleteSettings(connect: false);

    private void CompleteSettings(bool connect)
    {
        if (_commonSettings)
        {
            var profile = RdpProxySettingsService.Sanitize(GetConnectionSettingsFromUi());
            // Validate ordinary display/resource values without requiring a
            // target identity in this template, and never write a device.
            profile.Hostname = "example.invalid";
            if (!profile.IsValid()) { ShowValidationErrors(profile); return; }
            UpdatedSettings.Connection = RdpProxySettingsService.Sanitize(profile);
            DialogResult = DialogResult.OK;
            return;
        }
        if (SaveSettingsToConnectionLibrary())
        {
            ConnectRequested = connect;
            DialogResult = DialogResult.OK;
        }
    }

    private bool SaveSettingsToConnectionLibrary()
    {
        var previousHostname = UpdatedSettings.Connection.Hostname;
        var connectionSettings = GetConnectionSettingsFromUi();
        if (!connectionSettings.IsValid())
        {
            ShowValidationErrors(connectionSettings);
            return false;
        }

        // "保存" stores the connection in PureRDP's private device
        // library. Exporting a portable .rdp file remains the responsibility
        // of "另存为…" below the form.
        if (!RecentConnectionsService.AddOrUpdate(connectionSettings))
        {
            MessageBox.Show(this, "配置未能写入设备库，请检查目录写入权限或磁盘空间。当前修改仍保留在此窗口中。",
                "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        // Persist an explicitly supplied secret; .rdp files intentionally do
        // not contain it. Otherwise only this first in-memory connection works.
        if (connectionSettings.Password != null && !string.IsNullOrWhiteSpace(connectionSettings.Username))
        {
            try
            {
                RdpCredentialService.SaveConnectionPassword(connectionSettings.Hostname!,
                    connectionSettings.Username.CombineUsernameAndDomain(connectionSettings.Domain)!,
                    connectionSettings.Password);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(this, "设备配置已保存，但密码未能写入 Windows 凭据管理器。请检查凭据存储权限后重试。",
                    "凭据保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        // RDP credentials are keyed by TERMSRV/host. Keep an existing saved
        // password, but make its username follow the profile being saved;
        // otherwise the native client can silently reuse the old account.
        RdpCredentialService.UpdateSavedUsername(connectionSettings.Hostname,
            connectionSettings.Username.CombineUsernameAndDomain(connectionSettings.Domain));

        if (!string.IsNullOrWhiteSpace(previousHostname) &&
            !string.Equals(previousHostname, connectionSettings.Hostname, StringComparison.OrdinalIgnoreCase))
            RecentConnectionsService.Remove(previousHostname);

        UpdatedSettings.Connection = connectionSettings;
        UpdatedSettings.IsExternalConnection = false;
        UpdatedSettings.RdpFilePath = null;
        Text = "设置 - PureRDP";
        return true;
    }

    private void cmbHostname_TextChanged(object? sender, EventArgs e)
    {
        if (!_loadingConnectionSettings && !cmbHostname.DroppedDown)
        {
            _nativeUsernamePending = false;
            _credentialHintGeneration++;
            UpdateSavedCredentialHints();
        }
    }

    private void cmbUsername_TextChanged(object? sender, EventArgs e)
    {
        _nativeUsernamePending = false;
        if (!_loadingConnectionSettings && !cmbUsername.DroppedDown)
            UpdateSavedCredentialHints();
    }

    private void cmbHostname_Leave(object? sender, EventArgs e)
        => _ = QueueCredentialHintRefreshAsync();

    private Task QueueCredentialHintRefreshAsync() => RefreshCredentialHintAsync(_nativeUsernamePending);

    private async Task RefreshCredentialHintAsync(bool nativeSelection)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        var hostname = cmbHostname.Text.Trim();
        var usernameBeforeLookup = cmbUsername.Text;
        var generation = ++_credentialHintGeneration;
        string? savedUsername;
        string? historicalUsername;
        try
        {
            // Credential Manager is a native API. Keep it off the WinForms
            // thread so opening or editing a device cannot pause painting.
            var lookup = await Task.Run(() => (Saved: RdpCredentialService.CredentialsExist(hostname),
                Hint: nativeSelection ? WindowsRdpHistoryService.LoadUsernameHint(hostname) : null));
            savedUsername = lookup.Saved;
            historicalUsername = lookup.Hint;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return;
        }

        if (IsDisposed || !IsHandleCreated || generation != _credentialHintGeneration ||
            !string.Equals(hostname, cmbHostname.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _credentialHintHostname = hostname;
            _savedCredentialUsername = savedUsername;
            _credentialHintLoaded = true;
            if (nativeSelection)
                PopulateNativeUsername(savedUsername, historicalUsername, usernameBeforeLookup);
            UpdateSavedCredentialHints(fillEmptyUsername: !nativeSelection);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A dialog can be closing while the native query completes. The
            // result is only a visual hint, so discard it if controls vanished.
        }
    }

    private void PopulateNativeUsername(string? savedUsername, string? historicalUsername, string usernameBeforeLookup)
    {
        // An explicit history selection replaces the previous address's user,
        // but a later manual edit while the native lookup ran takes precedence.
        if (cmbUsername.Text != usernameBeforeLookup) return;
        var username = savedUsername ?? historicalUsername;
        if (string.IsNullOrWhiteSpace(username)) return;
        using (new TextChangedEventSuppressor(cmbUsername, cmbUsername_TextChanged))
            cmbUsername.Text = username;
        _nativeUsernamePending = false;
    }

    private async Task QueueRecentConnectionsRefreshAsync()
    {
        if (IsDisposed)
            return;

        var generation = ++_recentConnectionsGeneration;
        List<ConnectionSettings> recentConnections;
        string[] nativeAddresses;
        try
        {
            var history = await Task.Run(() => (Recent: RecentConnectionsService.LoadAll(), Native: WindowsRdpHistoryService.LoadAddresses()));
            recentConnections = history.Recent;
            nativeAddresses = WindowsRdpHistoryService.AdditionalAddresses(
                recentConnections.Select(setting => setting.Hostname), history.Native);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return;
        }

        if (IsDisposed || !IsHandleCreated || generation != _recentConnectionsGeneration)
            return;

        try
        {
            // Keep any text the user entered while the files were loading.
            // Loading the dropdown is a background convenience and must never
            // replace an in-progress edit.
            var hostname = cmbHostname.Text;
            var username = cmbUsername.Text;
            _loadingConnectionSettings = true;
            cmbHostname.BeginUpdate();
            cmbUsername.BeginUpdate();
            try
            {
                cmbHostname.Items.Clear();
                cmbHostname.Items.AddRange(recentConnections
                    .Where(setting => !string.IsNullOrWhiteSpace(setting.Hostname))
                    .Select(setting => new ComboBoxItem<ConnectionSettings>(setting, setting.Hostname!))
                    .Cast<object>()
                    .ToArray());
                // Native entries are addresses only, not device profiles. Selecting
                // one must not reset the name, display options or other edited values.
                cmbHostname.Items.AddRange(nativeAddresses);
                cmbHostname.SelectedIndex = -1;
                cmbHostname.Text = hostname;

                cmbUsername.Items.Clear();
                cmbUsername.Items.AddRange(recentConnections
                    .Select(setting => setting.Username.CombineUsernameAndDomain(setting.Domain))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct()
                    .Cast<object>()
                    .ToArray());
                cmbUsername.SelectedIndex = -1;
                cmbUsername.Text = username;
            }
            finally
            {
                cmbUsername.EndUpdate();
                cmbHostname.EndUpdate();
                _loadingConnectionSettings = false;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _loadingConnectionSettings = false;
        }
    }

    private void lbCredentialSave_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        var password = string.IsNullOrWhiteSpace(tbPassword.Text) ? UpdatedSettings.Connection.Password : tbPassword.Text;
        try { RdpCredentialService.SaveCredentials(cmbHostname.Text, cmbUsername.Text, password); }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "Windows 凭据保存失败，请检查凭据存储权限后重试。", "保存失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        UpdateSavedCredentialHints(refreshCredentials: true);
    }

    private void lbCredentialRemove_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        RdpCredentialService.RemoveCredentials(cmbHostname.Text);
        UpdateSavedCredentialHints(refreshCredentials: true);
    }

    private void cmbHostname_SelectionChangeCommitted(object sender, EventArgs e)
    {
        UpdatedSettings.RdpFilePath = null;
        if (cmbHostname.SelectedItem is ComboBoxItem<ConnectionSettings> item)
            LoadConnectionSettingsToUi(item.Value);
        else if (cmbHostname.SelectedItem is string)
        {
            // Never reuse a previous device's explicit password for a different
            // history address. Windows keeps the selected target's saved secret.
            tbPassword.Clear();
            UpdatedSettings.Connection.Password = null;
            UpdatedSettings.Connection.EncryptedPassword51 = null;
            UpdatedSettings.Connection.NativeFallback?.CancelIfPending();
            UpdatedSettings.Connection.NativeFallback = null;
            _nativeUsernamePending = true;
            _ = RefreshCredentialHintAsync(true);
        }
    }

    private void cmbHostname_KeyDown(object sender, KeyEventArgs e)
    {
        var isShiftDelete = e is { Shift: true, KeyCode: Keys.Delete };
        if (!isShiftDelete)
            return;

        if (cmbHostname.SelectedItem is not ComboBoxItem<ConnectionSettings> selectedItem)
            return;

        RemoveRecent(selectedItem);
        e.Handled = true;
    }

    private void FormSettings_FormClosing(object sender, FormClosingEventArgs e)
    {
        _credentialHintGeneration++;
        _recentConnectionsGeneration++;
        // Validation/persistence happens once, in the explicit Save/Connect actions.
        // Closing with X or Cancel must never apply unconfirmed edits.
        if (DialogResult != DialogResult.OK)
            DialogResult = DialogResult.Cancel;
    }

    private void ShowValidationErrors(ConnectionSettings connectionSettings)
    {
        var validationErrors = connectionSettings.Validate();
        var errorMessage = $"""
            连接设置存在以下问题：

            {string.Join(Environment.NewLine, validationErrors.Select(err => $"- {err}"))}
            """;

        MessageBox.Show(this, errorMessage, "连接设置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void panelBadgeColor_Click(object sender, EventArgs e)
        => ChangeColor();

    private void tbBadgeColor_TextChanged(object sender, EventArgs e)
    {
        var color = tbBadgeColor.Text.TryToColor();
        panelBadgeColor.BackColor = color ?? SystemColors.Window;
    }

    private enum ScreenModeOption
    {
        Windowed,
        FullScreen,
        UseAllMonitors
    }

    private class ComboBoxItem<T>
    {
        public string Display { get; }

        public T Value { get; }

        public ComboBoxItem(T value, string display)
        {
            Value = value;
            Display = display;
        }

        public override string ToString() => Display;
    }
}
