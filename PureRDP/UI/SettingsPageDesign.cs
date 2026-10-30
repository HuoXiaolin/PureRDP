using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PureRDP.UI;

/// <summary>Shared settings geometry. Existing controls retain their bindings and events.</summary>
internal static class SettingsPageDesign
{
    internal static TableLayoutPanel Stack() {
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    internal static void Add(TableLayoutPanel stack, Control control) {
        var row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 0, 0, 12);
        stack.Controls.Add(control, 0, row);
    }

    internal static Label Note(string text) => new() {
        Text = text, AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(680, 0),
        ForeColor = UiDesignTokens.TextSecondary, Margin = new Padding(0, 4, 0, 8),
    };

    internal static Control Section(string title, params Control[] controls) {
        var panel = new RoundedSurfacePanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = UiDesignTokens.Surface,
            Padding = new Padding(16, 14, 16, 8) };
        var rows = Stack();
        Add(rows, new Label { Text = title, AutoSize = true, Dock = DockStyle.Top,
            ForeColor = UiDesignTokens.TextPrimary,
            Font = new Font(UiDesignTokens.FontFamily, 10F, FontStyle.Bold) });
        foreach (var control in controls) Add(rows, control);
        panel.Controls.Add(rows);
        return panel;
    }

    internal static FlowLayoutPanel Actions(params Control[] controls) {
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        foreach (var control in controls) {
            control.Margin = new Padding(0, 0, 8, 4);
            row.Controls.Add(control);
        }
        return row;
    }

    internal static Control Field(string label, Control control, string? description = null) {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 2, RowCount = description == null ? 1 : 2, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiDesignTokens.TextPrimary }, 0, 0);
        control.Anchor = AnchorStyles.Left;
        control.Margin = new Padding(0, 2, 0, 2);
        table.Controls.Add(control, 1, 0);
        if (description != null) {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var note = Note(description);
            table.Controls.Add(note, 0, 1);
            table.SetColumnSpan(note, 2);
        }
        return table;
    }

    internal static void Apply(SettingsPage page) {
        var roots = page.Controls.Cast<Control>().ToArray();
        foreach (var root in roots) RemoveLegacyTitle(root, page.Title);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
            RowCount = 2, Margin = Padding.Empty, BackColor = UiDesignTokens.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        heading.Controls.Add(new Label { Text = page.Title, Location = Point.Empty,
            Size = new Size(740, 32), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font(UiDesignTokens.FontFamily, 16F, FontStyle.Bold), ForeColor = UiDesignTokens.TextPrimary });
        var description = Note(page switch {
            WindowSettingsPage => "设置控制栏位置、窗口外观及多连接排列方式。",
            StartupSettingsPage => "管理当前 Windows 用户的自动启动与静默启动。",
            ShortcutSettingsPage => "PureRDP 操作快捷键与设备 Windows 快捷键分别设置。",
            PreviewSettingsPage => "管理卡片内的只读远端预览，不改变连接窗口的操作方式。",
            DeviceMonitoringSettingsPage => "设备端口扫描与连接质量采样分别配置。",
            ConnectionLogPage => "查看设备连接记录；选中设备后显示对应的详细日志。",
            DataStorageSettingsPage => "查看实际数据位置，了解配置、日志和凭据的存储边界。",
            FileOpeningSettingsPage => "文件默认应用、MSTSC 启动代理和通用设置分别管理。",
            _ => "软件信息、项目地址与版本更新。",
        });
        description.Dock = DockStyle.None;
        description.Location = new Point(0, 38);
        heading.Controls.Add(description);
        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty,
            Padding = new Padding(0, 0, 6, 0), BackColor = UiDesignTokens.Surface };
        foreach (var root in roots) body.Controls.Add(root);
        body.AutoScrollMinSize = new Size(0, page switch {
            ConnectionLogPage => 420, AboutSettingsPage => 510, ShortcutSettingsPage => 470,
            FileOpeningSettingsPage => 680, DataStorageSettingsPage => 500, _ => 360,
        });
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(body, 0, 1);
        page.AutoScroll = false;
        page.AutoScrollMinSize = Size.Empty;
        page.Controls.Add(layout);
        layout.BringToFront();
        Normalize(body);
        body.ClientSizeChanged += (_, _) => WrapNotes(body);
        WrapNotes(body);
    }

    private static void RemoveLegacyTitle(Control root, string title) {
        foreach (Control child in root.Controls.Cast<Control>().ToArray()) {
            if (child is Label label && label.Font.Size >= 14 &&
                (label.Text == title || label.Text == "设备状态扫描")) {
                label.Visible = false;
                if (root is TableLayoutPanel table) {
                    var row = table.GetRow(label);
                    if (row >= 0 && row < table.RowStyles.Count) {
                        table.RowStyles[row].SizeType = SizeType.Absolute;
                        table.RowStyles[row].Height = 0;
                    }
                }
            } else RemoveLegacyTitle(child, title);
        }
    }

    private static void Normalize(Control parent) {
        foreach (Control child in parent.Controls) {
            if (child is FlowLayoutPanel { AutoSize: true } flow)
                flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            if (child is Button button) {
                button.MinimumSize = new Size(0, 34);
                button.Height = 34;
            }
            if (child is Label label && label.ForeColor == SystemColors.ControlText)
                label.ForeColor = UiDesignTokens.TextPrimary;
            if (child is DataGridView grid) {
                grid.BorderStyle = BorderStyle.None;
                grid.BackgroundColor = UiDesignTokens.Surface;
                grid.GridColor = UiDesignTokens.Border;
                grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = UiDesignTokens.Surface;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = UiDesignTokens.TextSecondary;
                grid.DefaultCellStyle.SelectionBackColor = UiDesignTokens.AccentSoft;
                grid.DefaultCellStyle.SelectionForeColor = UiDesignTokens.TextPrimary;
                grid.AlternatingRowsDefaultCellStyle.BackColor = UiDesignTokens.Surface;
                grid.RowTemplate.Height = 32;
            }
            Normalize(child);
        }
    }

    private static void WrapNotes(Control parent) {
        foreach (Control child in parent.Controls) {
            if (child is Label { AutoSize: true } label && label.MaximumSize.Width > 0) {
                var width = Math.Max(160, parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal);
                if (label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
            }
            WrapNotes(child);
        }
    }
}
