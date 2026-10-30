using PureRDP.Configuration;
using PureRDP.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PureRDP.UI;

internal sealed class DataStorageSettingsPage : SettingsPage
{
    public override string Title => "数据与清理";
    private readonly TextBox _primaryPath;
    private readonly TextBox _locations;
    private readonly Label _message;

    public DataStorageSettingsPage()
    {
        BackColor = UiDesignTokens.Surface;
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Padding = new Padding(0, 0, 0, 16),
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.Controls.Add(new Label { Text = Title, AutoSize = true,
            Font = new Font(UiDesignTokens.FontFamily, 16F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 16) });
        rows.Controls.Add(new Label { Text = "当前用户的主数据目录", AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        _primaryPath = PathBox(48);
        _primaryPath.Name = "PrimaryDataDirectory";
        _primaryPath.AccessibleName = "当前用户主数据目录";
        rows.Controls.Add(_primaryPath);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 8, 0, 16) };
        var open = new ModernButton { Text = "打开数据目录", Width = 148, Height = 36 };
        var copy = new ModernButton { Text = "复制路径", Width = 112, Height = 36 };
        var refresh = new ModernButton { Text = "刷新位置", Width = 112, Height = 36 };
        open.Click += (_, _) => OpenDirectory();
        copy.Click += (_, _) => CopyPath();
        refresh.Click += (_, _) => RefreshPaths();
        actions.Controls.AddRange(new Control[] { open, copy, refresh });
        rows.Controls.Add(actions);
        rows.Controls.Add(new Label { Text = "所有数据位置与诊断日志（含备用目录）", AutoSize = true,
            Margin = new Padding(0, 0, 0, 8) });
        _locations = PathBox(176);
        _locations.AccessibleName = "所有数据与诊断日志位置";
        rows.Controls.Add(_locations);
        rows.Controls.Add(new Label
        {
            Text = "清理前请先从系统托盘退出 PureRDP，并备份需要保留的数据。\n" +
                "目录包含设备记录（Recent）、全局配置、窗口状态和连接日志。存在备用目录时也需检查；运行中删除可能被重新写入。\n" +
                "手动另存的 .rdp 文件不在此清单内，请按保存位置清理。\n" +
                "密码另存于 Windows 凭据管理器，删除数据目录不会删除凭据。请在凭据管理器中仅删除对应目标的 TERMSRV 凭据。\n" +
                "卸载前请先在“开机自启动”页关闭自启动。此页面只展示、打开或复制位置，不自动删除文件。",
            AutoSize = true, MaximumSize = new Size(740, 0), ForeColor = UiDesignTokens.TextSecondary,
            Margin = new Padding(0, 18, 0, 0),
        });
        _message = new Label { AutoSize = true, ForeColor = UiDesignTokens.Accent,
            Margin = new Padding(0, 12, 0, 0) };
        rows.Controls.Add(_message);
        var sections = SettingsPageDesign.Stack();
        var cleanup = rows.Controls.OfType<Label>().First(label => label.Text.StartsWith("清理前"));
        var dataActions = SettingsPageDesign.Actions(open, copy, refresh);
        dataActions.AutoSize = false;
        dataActions.Height = 38;
        SettingsPageDesign.Add(sections, SettingsPageDesign.Section("当前用户主数据目录", _primaryPath,
            dataActions));
        SettingsPageDesign.Add(sections, SettingsPageDesign.Section("所有数据位置与诊断日志", _locations));
        SettingsPageDesign.Add(sections, SettingsPageDesign.Section("清理说明", cleanup));
        SettingsPageDesign.Add(sections, _message);
        rows.Dispose();
        Controls.Add(sections);
        VisibleChanged += (_, _) => { if (Visible) RefreshPaths(); };
        RefreshPaths();
    }

    private static TextBox PathBox(int height) => new()
    {
        ReadOnly = true, Multiline = true, Height = height, Dock = DockStyle.Top,
        ScrollBars = ScrollBars.Vertical, BackColor = UiDesignTokens.SurfaceSubtle,
        ForeColor = UiDesignTokens.TextPrimary, BorderStyle = BorderStyle.FixedSingle,
    };

    private void RefreshPaths()
    {
        _primaryPath.Text = ProductPaths.PrimaryDataDirectory;
        _locations.Text = string.Join(Environment.NewLine,
            ProductPaths.GetDataDirectories().Select(path =>
                $"{(Directory.Exists(path) ? "已存在" : "未创建")}  {path}")
            .Concat(ProductPaths.DiagnosticFiles.Select(path =>
                $"{(File.Exists(path) ? "日志文件" : "未创建")}  {path}")));
    }

    private void OpenDirectory()
    {
        var path = ProductPaths.PrimaryDataDirectory;
        if (!Directory.Exists(path))
        {
            _message.Text = "主数据目录尚未创建，请检查下方已存在的备用目录。";
            return;
        }
        try { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        { _message.Text = "无法打开目录，请复制路径后手动打开。"; }
    }

    private void CopyPath()
    {
        try { Clipboard.SetText(ProductPaths.PrimaryDataDirectory); _message.Text = "路径已复制。"; }
        catch (ExternalException) { _message.Text = "剪贴板暂时不可用，请选中路径后复制。"; }
    }

    public override void ApplyTo(GlobalSettings settings) { }
    public override void LoadFrom(GlobalSettings settings) => RefreshPaths();
}
