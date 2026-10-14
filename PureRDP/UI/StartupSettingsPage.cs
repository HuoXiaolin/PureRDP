using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Security;
using System.Windows.Forms;
using PureRDP.Configuration;

namespace PureRDP.UI;

internal sealed class StartupSettingsPage : SettingsPage
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly CheckBox _enabled = new() { Text = "登录 Windows 后自动启动 PureRDP", AutoSize = true };
    private readonly CheckBox _silent = new() { Text = "静默启动：仅到系统托盘，不打开主窗口", AutoSize = true };
    private readonly TextBox _registered = new() { ReadOnly = true, Multiline = true, Height = 72, Dock = DockStyle.Top, ScrollBars = ScrollBars.Vertical };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(750, 0) };
    private bool _changed;
    public override string Title => "开机自启动";
    private static string CurrentExecutable => Environment.ProcessPath ?? Application.ExecutablePath;

    public StartupSettingsPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 8, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = Title, AutoSize = true, Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold), Margin = new Padding(0, 0, 0, 24) });
        layout.Controls.Add(_enabled);
        layout.Controls.Add(_silent);
        layout.Controls.Add(new Label { Text = "已登记的自启动程序路径：", AutoSize = true, Margin = new Padding(0, 24, 0, 8) });
        layout.Controls.Add(_registered);
        layout.Controls.Add(new Label { Text = "当前程序路径：", AutoSize = true, Margin = new Padding(0, 20, 0, 8) });
        layout.Controls.Add(new TextBox { Text = CurrentExecutable, ReadOnly = true, Multiline = true, Height = 72, Dock = DockStyle.Top, ScrollBars = ScrollBars.Vertical });
        var repair = new ModernButton { Text = "修复路径", Width = 140, Height = 38, Margin = new Padding(0, 20, 0, 12) };
        repair.Click += (_, _) =>
        {
            if (!_enabled.Checked)
            {
                _message.Text = "请先勾选开机自启动，再修复路径。";
                return;
            }
            ApplyStartup(force: true);
        };
        layout.Controls.Add(repair);
        layout.Controls.Add(_message);
        layout.Controls.Add(new Label { Text = "开关点击“保存并应用”后生效。修复路径立即更新为当前程序位置。\n仅设置当前 Windows 用户，不随 JSON 配置导入或导出。", AutoSize = true, Margin = new Padding(0, 16, 0, 0) });
        Controls.Add(layout);
        ReadRegistration();
        _enabled.CheckedChanged += (_, _) => _changed = true;
        _silent.CheckedChanged += (_, _) => _changed = true;
    }

    private void ReadRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var command = key?.GetValue("PureRDP") as string;
            _registered.Text = string.IsNullOrWhiteSpace(command) ? "未设置" : command;
            _enabled.Checked = !string.IsNullOrWhiteSpace(command);
            _silent.Checked = _enabled.Checked && command?.IndexOf("--silent", StringComparison.OrdinalIgnoreCase) >= 0;
            _changed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            _message.Text = "读取自启动配置失败：" + e.Message;
            _message.ForeColor = UiDesignTokens.Danger;
        }
    }

    internal bool ApplyStartup(bool force = false)
    {
        if (!_changed && !force) return true;
        try
        {
            var executable = CurrentExecutable;
            if (_enabled.Checked && (!File.Exists(executable) || !string.Equals(Path.GetFileName(executable), "PureRDP.exe", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("请使用发布版 PureRDP.exe 启动程序后再设置。");
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (_enabled.Checked)
            {
                var command = "\"" + executable + "\"" + (_silent.Checked ? " --silent" : string.Empty);
                key.SetValue("PureRDP", command, RegistryValueKind.String);
            }
            else key.DeleteValue("PureRDP", throwOnMissingValue: false);
            ReadRegistration();
            _message.ForeColor = UiDesignTokens.Accent;
            _message.Text = _enabled.Checked ? "自启动路径已更新。" : "开机自启动已关闭。";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            _message.ForeColor = UiDesignTokens.Danger;
            _message.Text = "更新失败：" + e.Message;
            return false;
        }
    }

    public override void ApplyTo(GlobalSettings settings) { }
    public override void LoadFrom(GlobalSettings settings) { }
}
