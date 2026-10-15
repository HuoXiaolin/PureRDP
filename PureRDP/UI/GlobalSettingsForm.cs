using PureRDP.Configuration;
using PureRDP.Configuration.Enums;
using PureRDP.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
namespace PureRDP.UI;

internal sealed class GlobalSettingsForm : Form
{
    private GlobalSettings _original;
    private readonly List<SettingsPage> _pages = [];
    private readonly Panel _content;
    private readonly Label _status;
    public GlobalSettings SavedSettings { get; private set; }
    public event EventHandler? SettingsSaved;
    public GlobalSettingsForm(GlobalSettings settings)
    {
        _original = settings.Clone();
        SavedSettings = settings.Clone();
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        SuspendLayout();
        Text = "全局设置 · PureRDP";
        Icon = Properties.Resources.AppIcon;
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // Give the settings surface enough room for the log tables and the
        // future settings pages without forcing dense, clipped controls.
        ClientSize = new Size(1080, 780);
        MinimumSize = new Size(1000, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiDesignTokens.PageBackground;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 194));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        var heading = new Label { Dock = DockStyle.Fill, Text = "全局设置\n管理 PureRDP 的应用偏好", Padding = new Padding(28, 16, 0, 0), Font = new Font(Font.FontFamily, 13F), ForeColor = UiDesignTokens.TextPrimary, BackColor = UiDesignTokens.HeroSky };
        shell.Controls.Add(heading, 0, 0);
        shell.SetColumnSpan(heading, 2);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 18, 12, 18), AutoScroll = true, BackColor = UiDesignTokens.SurfaceSoft };
        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28), AutoScroll = true, BackColor = UiDesignTokens.Surface };
        shell.Controls.Add(navigation, 0, 1);
        shell.Controls.Add(_content, 1, 1);
        AddPage(navigation, new WindowSettingsPage(settings.Windows));
        AddPage(navigation, new StartupSettingsPage());
        AddPage(navigation, new ShortcutSettingsPage(settings.Shortcuts));
        AddPage(navigation, new PreviewSettingsPage(settings.Previews));
        AddPage(navigation, new DeviceMonitoringSettingsPage(settings.Monitoring));
        AddPage(navigation, new ConnectionLogPage());
        AddPage(navigation, new DataStorageSettingsPage());
        AddPage(navigation, new AboutSettingsPage());
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 16), ColumnCount = 5, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _status = new Label { Dock = DockStyle.Fill, Text = "保存后对所有连接生效", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiDesignTokens.TextMuted };
        var import = new ModernButton { Text = "导入配置", Dock = DockStyle.Fill };
        import.Click += (_, _) => ImportSettings();
        var export = new ModernButton { Text = "导出配置", Dock = DockStyle.Fill };
        export.Click += (_, _) => ExportSettings();
        var cancel = new ModernButton { Text = "关闭", Dock = DockStyle.Fill };
        cancel.Click += (_, _) => Close();
        var save = new ModernButton { Text = "保存并应用", Dock = DockStyle.Fill, Kind = ModernButtonKind.Primary };
        save.Click += (_, _) => SaveSettings();
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(import, 1, 0);
        footer.Controls.Add(export, 2, 0);
        footer.Controls.Add(cancel, 3, 0);
        footer.Controls.Add(save, 4, 0);
        shell.Controls.Add(footer, 0, 2);
        shell.SetColumnSpan(footer, 2);
        Controls.Add(shell);
        AcceptButton = save;
        CancelButton = cancel;
        SelectPage(_pages[0]);
        ResumeLayout(true);
    }
    private void AddPage(FlowLayoutPanel navigation, SettingsPage page)
    {
        _pages.Add(page);
        page.Font = Font;
        page.ForeColor = UiDesignTokens.TextPrimary;
        page.Dock = DockStyle.Fill;
        page.AutoScroll = true;
        page.AutoScrollMinSize = new Size(0, page switch
        {
            ShortcutSettingsPage => 610,
            StartupSettingsPage => 570,
            AboutSettingsPage => 570,
            DataStorageSettingsPage => 590,
            _ => 460,
        });
        page.Visible = false;
        _content.Controls.Add(page);
        var button = new ModernButton { Text = page.Title, Width = 164, Height = 42, Bordered = false };
        button.Click += (_, _) => SelectPage(page);
        page.NavigationButton = button;
        navigation.Controls.Add(button);
    }
    private void SelectPage(SettingsPage selected)
    {
        _content.SuspendLayout();
        try
        {
            foreach (var page in _pages)
            {
                page.Visible = page == selected;
                page.NavigationButton!.Selected = page == selected;
                page.NavigationButton.Invalidate();
            }
        }
        finally
        {
            _content.ResumeLayout(true);
        }
    }
    private void SaveSettings()
    {
        foreach (var page in _pages)
        {
            if (page is ShortcutSettingsPage shortcuts && !shortcuts.ValidateShortcuts(out var error))
            {
                SelectPage(page);
                _status.Text = "快捷键存在冲突，请修改后保存";
                _status.ForeColor = UiDesignTokens.Danger;
                MessageBox.Show(this, error, "快捷键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        var draft = _original.Clone();
        foreach (var page in _pages) page.ApplyTo(draft);
        if (!GlobalSettingsService.Save(draft))
        {
            _status.Text = "保存失败，请检查配置目录权限";
            _status.ForeColor = UiDesignTokens.Danger;
            return;
        }
        SavedSettings = draft;
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        foreach (var page in _pages.OfType<StartupSettingsPage>())
        {
            if (!page.ApplyStartup())
            {
                SelectPage(page);
                _status.Text = "其他设置已保存，自启动设置失败，请查看提示";
                _status.ForeColor = UiDesignTokens.Danger;
                return;
            }
        }
        _status.Text = "设置已保存并应用";
        _status.ForeColor = UiDesignTokens.Accent;
    }

    private GlobalSettings GetDraftSettings()
    {
        var draft = _original.Clone();
        foreach (var page in _pages) page.ApplyTo(draft);
        return draft;
    }

    private void ImportSettings()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入 PureRDP 全局设置",
            Filter = "PureRDP 设置 (*.json)|*.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (!GlobalSettingsService.TryImport(dialog.FileName, out var imported, out var error))
        {
            _status.Text = $"导入失败：{error}";
            _status.ForeColor = UiDesignTokens.Danger;
            return;
        }

        _original = imported.Clone();
        SavedSettings = _original.Clone();
        foreach (var page in _pages) page.LoadFrom(_original);
        _status.Text = "设置已导入，请点击“保存并应用”写入当前配置";
        _status.ForeColor = UiDesignTokens.Accent;
    }

    private void ExportSettings()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出 PureRDP 全局设置",
            Filter = "PureRDP 设置 (*.json)|*.json|JSON 文件 (*.json)|*.json",
            DefaultExt = "json",
            AddExtension = true,
            FileName = "purerdp-global-settings.json",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (!GlobalSettingsService.Export(GetDraftSettings(), dialog.FileName))
        {
            _status.Text = "导出失败，请检查文件路径和权限";
            _status.ForeColor = UiDesignTokens.Danger;
            return;
        }

        _status.Text = "设置 JSON 已导出";
        _status.ForeColor = UiDesignTokens.Accent;
    }
}
internal abstract class SettingsPage : UserControl
{
    public abstract string Title { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ModernButton? NavigationButton { get; set; }
    public abstract void ApplyTo(GlobalSettings settings);
    public abstract void LoadFrom(GlobalSettings settings);
}
internal sealed class WindowSettingsPage : SettingsPage
{
    private readonly RadioButton _independent;
    private readonly RadioButton _tile;
    private readonly ComboBox _controlBarPosition;
    private readonly CheckBox _controlBarAutoHide;
    public override string Title => "连接与窗口";
    public WindowSettingsPage(GlobalWindowPreferences preferences)
    {
        BackColor = UiDesignTokens.Surface;
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 0, 8, 8),
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 292));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rows.Controls.Add(new Label
        {
            Text = Title,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            ForeColor = UiDesignTokens.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        rows.Controls.Add(new Label
        {
            Text = "统一管理多窗口排列方式，以及控制栏的位置和显示策略。",
            Dock = DockStyle.Fill,
            ForeColor = UiDesignTokens.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);

        var card = new RoundedSurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) };
        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Margin = Padding.Empty,
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 32, 38, 38, 48, 48, 56 })
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        var layoutTitle = new Label
        {
            Text = "多连接查看方式",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = UiDesignTokens.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        form.Controls.Add(layoutTitle, 0, 0);
        form.SetColumnSpan(layoutTitle, 2);

        _independent = new RadioButton
        {
            Text = "每台设备独立窗口",
            Dock = DockStyle.Fill,
            Checked = !preferences.AutoTileSessions,
            ForeColor = UiDesignTokens.TextPrimary,
            Margin = new Padding(0, 4, 0, 0),
        };
        _tile = new RadioButton
        {
            Text = "自动适应宫格布局",
            Dock = DockStyle.Fill,
            Checked = preferences.AutoTileSessions,
            ForeColor = UiDesignTokens.TextPrimary,
            Margin = new Padding(0, 4, 0, 0),
        };
        form.Controls.Add(FieldLabel("独立窗口"), 0, 1);
        form.Controls.Add(_independent, 1, 1);
        form.Controls.Add(FieldLabel("宫格布局"), 0, 2);
        form.Controls.Add(_tile, 1, 2);

        _controlBarPosition = new ModernComboBox
        {
            Dock = DockStyle.Left,
            Width = 230,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Microsoft YaHei UI", 10F),
            Margin = new Padding(0, 7, 0, 0),
        };
        _controlBarPosition.Items.Add(new PositionItem(ControlBarPosition.Top, "窗口顶部（居中）"));
        _controlBarPosition.Items.Add(new PositionItem(ControlBarPosition.Bottom, "窗口底部（居中）"));
        _controlBarPosition.Items.Add(new PositionItem(ControlBarPosition.Left, "窗口左侧（居中）"));
        _controlBarPosition.Items.Add(new PositionItem(ControlBarPosition.Right, "窗口右侧（居中）"));
        SetComboBoxValue(_controlBarPosition, preferences.ControlBarDock);
        form.Controls.Add(FieldLabel("控制栏位置"), 0, 3);
        form.Controls.Add(_controlBarPosition, 1, 3);

        _controlBarAutoHide = new CheckBox
        {
            Text = "鼠标移开后自动隐藏",
            AutoSize = true,
            Checked = preferences.ControlBarAutoHide,
            ForeColor = UiDesignTokens.TextPrimary,
            Margin = new Padding(0, 12, 0, 0),
        };
        form.Controls.Add(FieldLabel("控制栏显示"), 0, 4);
        form.Controls.Add(_controlBarAutoHide, 1, 4);

        var note = new Label
        {
            Text = "关闭后控制栏会在所选位置持续显示。\n设备设置中的“隐藏控制栏”仍可单独完全隐藏控制栏。",
            Dock = DockStyle.Fill,
            ForeColor = UiDesignTokens.TextMuted,
            Padding = new Padding(0, 8, 0, 0),
        };
        form.Controls.Add(note, 0, 5);
        form.SetColumnSpan(note, 2);
        card.Controls.Add(form);
        rows.Controls.Add(card, 0, 2);
        Controls.Add(rows);
    }
    public override void ApplyTo(GlobalSettings settings)
    {
        settings.Windows.AutoTileSessions = _tile.Checked;
        settings.Windows.ControlBarDock = GetComboBoxValue(_controlBarPosition);
        settings.Windows.ControlBarAutoHide = _controlBarAutoHide.Checked;
    }
    public override void LoadFrom(GlobalSettings settings)
    {
        _independent.Checked = !settings.Windows.AutoTileSessions;
        _tile.Checked = settings.Windows.AutoTileSessions;
        SetComboBoxValue(_controlBarPosition, settings.Windows.ControlBarDock);
        _controlBarAutoHide.Checked = settings.Windows.ControlBarAutoHide;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = UiDesignTokens.TextSecondary,
    };

    private static ControlBarPosition GetComboBoxValue(ComboBox comboBox)
        => comboBox.SelectedItem is PositionItem item ? item.Value : ControlBarPosition.Right;

    private static void SetComboBoxValue(ComboBox comboBox, ControlBarPosition value)
    {
        var index = comboBox.Items.OfType<PositionItem>()
            .ToList().FindIndex(item => item.Value == value);
        comboBox.SelectedIndex = Math.Max(0, index);
    }

    private sealed class PositionItem(ControlBarPosition value, string display)
    {
        public ControlBarPosition Value { get; } = value;
        public override string ToString() => display;
    }
}

internal sealed class DeviceMonitoringSettingsPage : SettingsPage
{
    private readonly NumericUpDown _interval;
    private readonly NumericUpDown _qualityInterval;
    public override string Title => "设备状态";

    public DeviceMonitoringSettingsPage(GlobalMonitoringPreferences preferences)
    {
        BackColor = UiDesignTokens.Surface;
        _interval = new NumericUpDown
        {
            Minimum = 5,
            Maximum = 86400,
            Increment = 5,
            Value = Math.Clamp(preferences.AvailabilityScanIntervalSeconds, 5, 86400),
            ThousandsSeparator = true,
            Width = 180,
            Font = new Font("Microsoft YaHei UI", 11F),
        };
        _qualityInterval = new NumericUpDown { Minimum = 1, Maximum = 86400, Width = 180,
            Value = Math.Clamp(preferences.ConnectionQualityIntervalSeconds <= 0 ? 3 : preferences.ConnectionQualityIntervalSeconds, 1, 86400) };
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 5, AutoSize = true };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        rows.Controls.Add(new Label { Text = "设备状态扫描", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold), ForeColor = UiDesignTokens.TextPrimary });
        var input = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        input.Controls.Add(new Label { Text = "扫描周期：", AutoSize = false, Width = 220, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty });
        input.Controls.Add(_interval);
        input.Controls.Add(new Label { Text = "秒", AutoSize = true, Margin = new Padding(8, 8, 0, 0) });
        rows.Controls.Add(input);
        rows.Controls.Add(new Label
        {
            Text = "控制台会按此周期同时检查每台设备的地址和端口。默认每小时扫描一次。\n绿色表示 TCP 端口可连接，红色表示当前不可连接；扫描不会建立 RDP 登录会话。",
            Dock = DockStyle.Fill,
            ForeColor = UiDesignTokens.TextSecondary,
            Padding = new Padding(0, 8, 0, 0),
        });
        var qualityInput = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        qualityInput.Controls.Add(new Label { Text = "连接状态刷新间隔：", AutoSize = false, Width = 220, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty });
        qualityInput.Controls.Add(_qualityInterval);
        qualityInput.Controls.Add(new Label { Text = "秒", AutoSize = true, Margin = new Padding(8, 6, 0, 0) });
        rows.Controls.Add(qualityInput);
        rows.Controls.Add(new Label { Text = "质量、速度、延迟、丢包率的采样间隔，默认 3 s。\n保存后对现有连接和预览立即生效；网络请求未完成时不会重复采样。", Dock = DockStyle.Fill, ForeColor = UiDesignTokens.TextSecondary });
        Controls.Add(rows);
    }

    public override void ApplyTo(GlobalSettings settings)
    {
        settings.Monitoring.AvailabilityScanIntervalSeconds = (int)_interval.Value;
        settings.Monitoring.ConnectionQualityIntervalSeconds = (int)_qualityInterval.Value;
    }

    public override void LoadFrom(GlobalSettings settings)
    {
        _interval.Value = Math.Clamp(settings.Monitoring.AvailabilityScanIntervalSeconds, 5, 86400);
        _qualityInterval.Value = Math.Clamp(settings.Monitoring.ConnectionQualityIntervalSeconds <= 0 ? 3 : settings.Monitoring.ConnectionQualityIntervalSeconds, 1, 86400);
    }
}

internal sealed class ConnectionLogPage : SettingsPage
{
    private bool _refreshing;
    private readonly DataGridView _summaryGrid;
    private readonly DataGridView _detailsGrid;
    private readonly Label _summaryLabel;
    private readonly Timer _refreshTimer;

    public override string Title => "连接日志";

    public ConnectionLogPage()
    {
        BackColor = UiDesignTokens.Surface;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new Panel { Dock = DockStyle.Fill };
        var clearButton = new ModernButton
        {
            Text = "清空日志", Dock = DockStyle.Right, Width = 120,
            Kind = ModernButtonKind.Secondary,
            AccessibleName = "清空日志",
        };
        clearButton.Click += async (_, _) =>
        {
            if (MessageBox.Show(FindForm(),
                "确定清空全部连接日志及统计吗？此操作无法撤销。\n现有远程连接不会断开，下次建立连接时重新记录。",
                "清空日志", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            clearButton.Enabled = false;
            bool cleared;
            try
            {
                cleared = await ConnectionLogService.ClearAsync();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                cleared = false;
            }
            if (IsDisposed) return;
            clearButton.Enabled = true;
            if (!cleared)
            {
                MessageBox.Show(FindForm(), "日志保存失败，未能清空，请检查文件是否可写。", "清空失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            RefreshLogs();
        };
        heading.Controls.Add(new Label { Text = "连接日志统计", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold), ForeColor = UiDesignTokens.TextPrimary });
        heading.Controls.Add(clearButton);
        layout.Controls.Add(heading);
        _summaryLabel = new Label { Text = "正在读取日志…", Dock = DockStyle.Fill, ForeColor = UiDesignTokens.TextSecondary, TextAlign = ContentAlignment.MiddleLeft };
        layout.Controls.Add(_summaryLabel);
        _summaryGrid = CreateGrid();
        _summaryGrid.Columns.Add("device", "设备");
        _summaryGrid.Columns.Add("address", "地址");
        _summaryGrid.Columns.Add("count", "连接次数");
        _summaryGrid.Columns.Add("duration", "累计时长");
        _summaryGrid.Columns.Add("last", "最近连接");
        layout.Controls.Add(_summaryGrid);
        layout.Controls.Add(new Label { Text = "远程明细", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), ForeColor = UiDesignTokens.TextPrimary, Padding = new Padding(0, 10, 0, 0) });
        _detailsGrid = CreateGrid();
        _detailsGrid.Columns.Add("device", "设备");
        _detailsGrid.Columns.Add("started", "开始时间");
        _detailsGrid.Columns.Add("ended", "结束时间");
        _detailsGrid.Columns.Add("duration", "持续时长");
        _detailsGrid.Columns.Add("state", "状态");
        layout.Controls.Add(_detailsGrid);
        Controls.Add(layout);

        _refreshTimer = new Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshLogs();
        Load += (_, _) => RefreshLogs();
        VisibleChanged += (_, _) =>
        {
            if (Visible) _refreshTimer.Start();
            else _refreshTimer.Stop();
        };
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = UiDesignTokens.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            EnableHeadersVisualStyles = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            Font = new Font("Microsoft YaHei UI", 9.5F),
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiDesignTokens.SurfaceSoft,
            ForeColor = UiDesignTokens.TextPrimary,
            SelectionBackColor = UiDesignTokens.SurfaceSoft,
            SelectionForeColor = UiDesignTokens.TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiDesignTokens.Surface,
            ForeColor = UiDesignTokens.TextSecondary,
            SelectionBackColor = UiDesignTokens.AccentSoft,
            SelectionForeColor = UiDesignTokens.TextPrimary,
            Padding = new Padding(6, 3, 6, 3),
        };
        return grid;
    }

    private async void RefreshLogs()
    {
        if (_refreshing || IsDisposed || !Visible) return;
        _refreshing = true;
        System.Collections.Generic.IReadOnlyList<ConnectionLogEntry> entries;
        try
        {
            entries = await ConnectionLogService.LoadAllAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!IsDisposed)
                _summaryLabel.Text = "日志读取失败，请检查日志文件是否损坏或被占用";
            return;
        }
        finally { _refreshing = false; }
        if (IsDisposed || !Visible) return;
        _summaryGrid.SuspendLayout();
        _detailsGrid.SuspendLayout();
        try
        {
            var groups = entries
                .GroupBy(entry => entry.Hostname ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Max(entry => entry.StartedAtUtc))
                .ToArray();
            RemoveMissingLogRows(_detailsGrid, entries.Select(entry => entry.Id));
            RemoveMissingLogRows(_summaryGrid, groups.Select(group => group.Key));
            var summaryRows = IndexRows(_summaryGrid);
            var detailRows = IndexRows(_detailsGrid);
            var appendSummaryRows = _summaryGrid.Rows.Count == 0;
            var appendDetailRows = _detailsGrid.Rows.Count == 0;
            var totalDuration = TimeSpan.Zero;
            foreach (var group in groups)
            {
                var duration = group.Aggregate(TimeSpan.Zero, (total, entry) => total + DurationOf(entry));
                totalDuration += duration;
                var latest = group.Max(entry => entry.StartedAtUtc).ToLocalTime();
                UpdateLogRow(_summaryGrid, summaryRows, appendSummaryRows, group.Key,
                    DeviceName(group.First()), group.Key, group.Count(), FormatDuration(duration), latest.ToString("yyyy-MM-dd HH:mm"));
            }

            foreach (var entry in entries)
            {
                var started = entry.StartedAtUtc.ToLocalTime();
                var ended = entry.EndedAtUtc?.ToLocalTime();
                UpdateLogRow(_detailsGrid, detailRows, appendDetailRows, entry.Id,
                    DeviceName(entry), started.ToString("yyyy-MM-dd HH:mm:ss"), ended?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                    !ended.HasValue && !entry.IsActive && !entry.LastSeenAtUtc.HasValue ? "未知" : FormatDuration(DurationOf(entry)),
                    ended.HasValue ? "已结束" : entry.IsActive ? "进行中" : "已中断（结束时间未知）");
            }

            _summaryLabel.Text = entries.Count == 0
                ? "暂无成功建立的 RDP 连接记录"
                : $"共 {groups.Length} 台设备，{entries.Count} 次连接，已记录在线 {FormatDuration(totalDuration)}";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // DataGridView can reject a row operation while its native handle
            // is being recreated during a DPI/layout change. Keep the settings
            // window alive and retry on the next timer tick.
            if (!IsDisposed)
                _summaryLabel.Text = "日志正在重新布局，请稍候…";
        }
        finally
        {
            try { _detailsGrid.ResumeLayout(); } catch (InvalidOperationException) { }
            try { _summaryGrid.ResumeLayout(); } catch (InvalidOperationException) { }
        }
    }

    private static string DeviceName(ConnectionLogEntry entry)
        => string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Hostname ?? "未命名设备" : entry.DisplayName;

    private static TimeSpan DurationOf(ConnectionLogEntry entry)
        => (entry.EndedAtUtc ?? (entry.IsActive ? DateTime.UtcNow : entry.LastSeenAtUtc ?? entry.StartedAtUtc)) - entry.StartedAtUtc is var duration && duration > TimeSpan.Zero
            ? duration : TimeSpan.Zero;

    private static Dictionary<string, DataGridViewRow> IndexRows(DataGridView grid)
    {
        var rows = new Dictionary<string, DataGridViewRow>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in grid.Rows)
            if (row.Tag is string key)
                rows[key] = row;
        return rows;
    }

    private static void UpdateLogRow(DataGridView grid,
        Dictionary<string, DataGridViewRow> rows, bool appendNewRows, string key, params object[] values)
    {
        if (!rows.TryGetValue(key, out var row))
        {
            DataGridViewRow? anchor = null;
            if (!appendNewRows && CanReadScrollPosition(grid))
            {
                try
                {
                    var firstVisible = grid.FirstDisplayedScrollingRowIndex;
                    if (firstVisible >= 0 && firstVisible < grid.Rows.Count)
                        anchor = grid.Rows[firstVisible];
                }
                catch (InvalidOperationException) { }
                catch (ArgumentOutOfRangeException) { }
            }

            if (appendNewRows)
            {
                var rowIndex = grid.Rows.Add(values);
                row = grid.Rows[rowIndex];
            }
            else
            {
                grid.Rows.Insert(0, values);
                row = grid.Rows[0];
            }
            row.Tag = key;
            rows[key] = row;
            if (anchor != null && CanReadScrollPosition(grid))
            {
                try
                {
                    var target = anchor.Index;
                    if (target >= 0 && target < grid.Rows.Count && !grid.Rows[target].Frozen)
                        grid.FirstDisplayedScrollingRowIndex = target;
                }
                catch (InvalidOperationException) { }
                catch (ArgumentOutOfRangeException) { }
            }
            return;
        }
        for (var index = 0; index < values.Length; index++)
            if (!Equals(row.Cells[index].Value, values[index]))
                row.Cells[index].Value = values[index];
    }

    private static bool CanReadScrollPosition(DataGridView grid)
    {
        try
        {
            return grid.IsHandleCreated && grid.Visible && grid.DisplayedRowCount(false) > 0 &&
                   grid.ClientSize.Height > grid.ColumnHeadersHeight + grid.RowTemplate.Height;
        }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static void RemoveMissingLogRows(DataGridView grid, System.Collections.Generic.IEnumerable<string> keys)
    {
        var retained = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = grid.Rows.Count - 1; index >= 0; index--)
            if (grid.Rows[index].Tag is string key && !retained.Contains(key))
                grid.Rows.RemoveAt(index);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}天 {duration.Hours:00}:{duration.Minutes:00}";
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    public override void ApplyTo(GlobalSettings settings) { }
    public override void LoadFrom(GlobalSettings settings) => RefreshLogs();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class AboutSettingsPage : SettingsPage
{
    private readonly GitHubUpdateService _updates = new();
    private readonly System.Threading.CancellationTokenSource _lifetime = new();
    private readonly Label _updateStatus;
    private readonly Button _checkUpdates;
    private readonly Button _releases;
    private readonly Image _brandImage;
    private bool _aboutDisposed;
    public override string Title => "关于 PureRDP";
    public AboutSettingsPage()
    {
        BackColor = UiDesignTokens.Surface;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(0, 0, 0, 8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.Dock = DockStyle.Top;
        root.Height = 634;

        var hero = new RoundedSurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 20), BackColor = UiDesignTokens.SurfaceSubtle };
        var heroLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var icon = new PictureBox
        {
            Size = new Size(68, 68),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = _brandImage = Properties.Resources.AppIcon.ToBitmap(),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 16, 0),
        };
        heroLayout.Controls.Add(icon, 0, 0);
        var brand = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        brand.Controls.Add(new Label { Text = "PureRDP", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 22F, FontStyle.Bold), ForeColor = UiDesignTokens.TextPrimary }, 0, 0);
        brand.Controls.Add(new Label { Text = "REMOTE DESKTOP HUB", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = UiDesignTokens.Accent }, 0, 1);
        brand.Controls.Add(new Label { Text = "把常用远程桌面收进一个工作台。\n看预览、查状态、切窗口，都在这里完成。", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F), ForeColor = UiDesignTokens.TextSecondary }, 0, 2);
        heroLayout.Controls.Add(brand, 1, 0);
        hero.Controls.Add(heroLayout);
        root.Controls.Add(hero, 0, 0);

        var product = CreateInfoPanel("产品信息", new[]
        {
            ("版本", GitHubUpdateService.VersionText),
            ("作者", GitHubUpdateService.Author),
            ("定位", "Windows 远程桌面集中管理工具"),
        });
        root.Controls.Add(product, 0, 1);

        var runtime = CreateInfoPanel("让远程工作更顺手", new[]
        {
            ("设备管理", "搜索设备、颜色标记，常用连接一眼找到"),
            ("多窗协作", "独立窗口或宫格布局，快捷键切换与控制"),
            ("即时掌握", "只读画面预览、网络状态与设备端口检查"),
        });
        root.Controls.Add(runtime, 0, 2);
        var updatePanel = new RoundedSurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), BackColor = UiDesignTokens.SurfaceSubtle };
        var updateLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        updateLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        updateLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        updateLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var project = new LinkLabel { Text = GitHubUpdateService.ProjectUrl, Dock = DockStyle.Fill, LinkColor = UiDesignTokens.Accent, ActiveLinkColor = UiDesignTokens.AccentPressed, AccessibleName = "PureRDP 项目地址" };
        project.LinkClicked += (_, _) => OpenProjectPage(GitHubUpdateService.ProjectUrl);
        updateLayout.Controls.Add(project, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _checkUpdates = new Button { Text = "检查更新", AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = UiDesignTokens.Accent, ForeColor = Color.White };
        _releases = new Button { Text = "查看版本页", AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = UiDesignTokens.Surface, ForeColor = UiDesignTokens.Accent };
        _checkUpdates.Click += async (_, _) => await CheckUpdatesAsync();
        _releases.Click += (_, _) => OpenProjectPage(GitHubUpdateService.ReleasesUrl);
        actions.Controls.AddRange([_checkUpdates, _releases]);
        updateLayout.Controls.Add(actions, 0, 1);
        _updateStatus = new Label { Text = "手动检查 GitHub 正式版本；不会自动下载、安装或重启。", Dock = DockStyle.Fill, ForeColor = UiDesignTokens.TextSecondary };
        updateLayout.Controls.Add(_updateStatus, 0, 2);
        updatePanel.Controls.Add(updateLayout);
        root.Controls.Add(updatePanel, 0, 3);
        Controls.Add(root);
    }

    private static Control CreateInfoPanel(string title, IEnumerable<(string Label, string Value)> rows)
    {
        var items = rows.ToArray();
        var panel = new RoundedSurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(18, 10, 18, 10), BackColor = UiDesignTokens.SurfaceSubtle };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = items.Length + 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        foreach (var _ in items)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        var titleLabel = new Label { Text = title, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), ForeColor = UiDesignTokens.TextPrimary };
        table.Controls.Add(titleLabel, 0, 0);
        table.SetColumnSpan(titleLabel, 2);
        var row = 1;
        foreach (var item in items)
        {
            table.Controls.Add(new Label { Text = item.Label, Dock = DockStyle.Fill, ForeColor = UiDesignTokens.TextMuted, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            table.Controls.Add(new Label { Text = item.Value, Dock = DockStyle.Fill, ForeColor = UiDesignTokens.TextSecondary, TextAlign = ContentAlignment.MiddleLeft }, 1, row++);
        }
        panel.Controls.Add(table);
        return panel;
    }
    public override void ApplyTo(GlobalSettings settings) { }
    public override void LoadFrom(GlobalSettings settings) { }

    private async System.Threading.Tasks.Task CheckUpdatesAsync()
    {
        if (!_checkUpdates.Enabled || IsDisposed) return;
        _checkUpdates.Enabled = false;
        _checkUpdates.Text = "检查中…";
        _updateStatus.Text = "正在查询项目最新公开正式版本…";
        try
        {
            var result = await _updates.CheckAsync(_lifetime.Token);
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            _updateStatus.Text = result.Message;
            _updateStatus.ForeColor = result.Status is UpdateCheckStatus.NetworkError or UpdateCheckStatus.InvalidRelease or UpdateCheckStatus.RateLimited ? UiDesignTokens.Danger : UiDesignTokens.TextSecondary;
            _releases.Text = result.Status == UpdateCheckStatus.UpdateAvailable ? "前往更新页面" : "查看版本页";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!IsDisposed && !_lifetime.IsCancellationRequested)
            {
                _checkUpdates.Enabled = true;
                _checkUpdates.Text = "检查更新";
            }
        }
    }

    private void OpenProjectPage(string url)
    {
        try { using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { _updateStatus.Text = "无法打开浏览器，请复制上方项目地址手动访问。"; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_aboutDisposed)
        {
            _aboutDisposed = true;
            _lifetime.Cancel();
            _updates.Dispose();
            _lifetime.Dispose();
            _brandImage.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class PreviewSettingsPage : SettingsPage
{
    private readonly CheckBox _enabled;
    public override string Title => "卡片预览";
    public PreviewSettingsPage(GlobalPreviewPreferences preferences)
    {
        BackColor = UiDesignTokens.Surface;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "卡片预览", Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold) });
        _enabled = new CheckBox
        {
            Text = "开启预览窗",
            Checked = preferences.Enabled,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Appearance = Appearance.Normal,
            ForeColor = UiDesignTokens.TextPrimary,
            AccessibleName = "开启预览窗",
        };
        layout.Controls.Add(_enabled);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = UiDesignTokens.TextSecondary,
            Text = "卡片内显示只读远程画面，不接收鼠标或键盘操作。\n连接窗口最小化后，预览继续显示。\n未打开连接窗口时，点击“预览”单独建立后台连接。\n已有连接时复用会话，避免重复登录。\n关闭此功能会停止独立预览，不会关闭连接窗口。" });
        Controls.Add(layout);
    }
    public override void ApplyTo(GlobalSettings settings) => settings.Previews.Enabled = _enabled.Checked;
    public override void LoadFrom(GlobalSettings settings) => _enabled.Checked = settings.Previews.Enabled;
}
