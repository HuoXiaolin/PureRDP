using PureRDP.Configuration;
using PureRDP.Services;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Security;
using System.Windows.Forms;

namespace PureRDP.UI;

internal sealed class FileOpeningSettingsPage : SettingsPage
{
    public override string Title => ".rdp 代理";
    private readonly Label _message = new() { AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(680, 0),
        ForeColor = UiDesignTokens.TextSecondary, Margin = new Padding(0, 14, 0, 12) };
    private readonly CheckBox _useCommon;
    private readonly CheckBox _preferNative;
    private ConnectionSettings _common;
    private readonly Button _enableProxy;
    private readonly Button _restoreProxy;
    private readonly Label _proxyState = new() { Name = "MstscProxyState", AutoSize = true,
        Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 0, 10) };
    private readonly Label _commonState = new() { Name = "RdpCommonState", AutoSize = true,
        Padding = new Padding(8, 4, 8, 4), Margin = new Padding(8, 12, 0, 12) };
    private bool _savedCommon;
    private bool _configuringProxy;

    public FileOpeningSettingsPage() : this(new GlobalRdpProxyPreferences()) { }
    public FileOpeningSettingsPage(GlobalRdpProxyPreferences preferences)
    {
        _common = RdpProxySettingsService.Sanitize(preferences.Connection);
        _savedCommon = preferences.UseCommonSettings;
        BackColor = UiDesignTokens.Surface;
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Note(string text) => rows.Controls.Add(new Label { Text = text, AutoSize = true,
            Dock = DockStyle.Top, MaximumSize = new Size(680, 0), Margin = new Padding(0, 8, 0, 12),
            ForeColor = UiDesignTokens.TextSecondary });
        FlowLayoutPanel Actions()
        {
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            rows.Controls.Add(actions);
            return actions;
        }
        rows.Controls.Add(new Label { Text = Title, AutoSize = true,
            Font = new Font(UiDesignTokens.FontFamily, 16F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 16) });
        Note("默认应用负责双击 .rdp 文件。网页助手如果直接启动 mstsc.exe，则需要另行开启 MSTSC 启动代理。");
        var defaultsActions = Actions();
        var register = new ModernButton { Text = "注册打开方式", Name = "RegisterRdpAssociation", Width = 140, Height = 38 };
        var defaults = new ModernButton { Text = "设置默认应用", Name = "ChooseDefaultRdpApp", Width = 140, Height = 38, Kind = ModernButtonKind.Primary };
        var native = new ModernButton { Text = "恢复原生打开", Name = "RestoreNativeRdp", Width = 140, Height = 38 };
        register.Click += (_, _) => ConfigureDefault(false);
        defaults.Click += (_, _) => ConfigureDefault(true);
        native.Click += async (_, _) =>
        {
            try
            {
                if (MstscProxyService.IsEnabled()) await MstscProxyService.RequestConfiguration(false);
                RefreshProxyState();
                RdpFileAssociationService.OpenDefaultApps();
                _message.Text = "MSTSC 启动代理已关闭。请在 Windows 中将 .rdp 的默认应用选为“远程桌面连接”；完成选择后即恢复原生文件打开。";
            }
            catch (Exception error) when (IsOperationError(error)) { _message.Text = "恢复未完成：" + error.Message; }
        };
        defaultsActions.Controls.AddRange(new Control[] { register, defaults, native });
        Note("MSTSC 启动代理（可选）");
        rows.Controls.Add(_proxyState);
        var proxyActions = Actions();
        _enableProxy = new ModernButton { Text = "开启启动代理", Name = "EnableMstscProxy", Width = 150, Height = 38 };
        _restoreProxy = new ModernButton { Text = "关闭并恢复", Name = "RestoreMstscProxy", Width = 150, Height = 38 };
        _enableProxy.Click += async (_, _) => await ConfigureProxy(true);
        _restoreProxy.Click += async (_, _) => await ConfigureProxy(false);
        proxyActions.Controls.AddRange(new Control[] { _enableProxy, _restoreProxy });
        Note("开启时由 Windows 请求管理员授权，对本机所有用户的 mstsc.exe 启动生效。关闭会恢复此前的启动设置。" +
            "\n请保留当前 PureRDP.exe 路径；移动或删除程序前先关闭代理。不替换系统文件，不接管 H5 或厂商私有客户端。");
        _preferNative = new CheckBox { Text = "外部连接始终使用原生 MSTSC（优先兼容）", AutoSize = true,
            Name = "PreferNativeRdpClient", Checked = preferences.PreferNativeClient, Margin = new Padding(0, 12, 0, 8) };
        rows.Controls.Add(_preferNative);
        Note("默认先尝试 PureRDP，连接失败后自动切换原生 MSTSC；普通字段、空字段和未填写用户名不会提前切换。" +
            "\n网关、RemoteApp、多显示器及不能等价实现的安全模式仍直接使用原生，保留必要的连接语义。" +
            "\n依赖厂商客户端代填或其他专属行为时，可开启“始终使用原生”。原生窗口使用自身控制栏，不应用下方通用设置。");
        _useCommon = new CheckBox { Text = "外部 .rdp / MSTSC 连接使用通用连接设置", AutoSize = true,
            Name = "UseRdpCommonSettings", Checked = preferences.UseCommonSettings, Margin = new Padding(0, 12, 0, 12) };
        _useCommon.CheckedChanged += (_, _) => RefreshCommonState();
        _preferNative.CheckedChanged += (_, _) => RefreshCommonState();
        var commonToggle = Actions();
        commonToggle.Controls.AddRange(new Control[] { _useCommon, _commonState });
        RefreshCommonState();
        var commonActions = Actions();
        var edit = new ModernButton { Text = "编辑通用连接设置", Name = "EditRdpCommonSettings", Width = 185, Height = 38 };
        edit.Click += (_, _) =>
        {
            using var editor = new FormSettings(new ApplicationSettings { Connection = _common }, FindForm()?.Icon, Size.Empty, true);
            if (editor.ShowDialog(this) == DialogResult.OK) _common = RdpProxySettingsService.Sanitize(editor.UpdatedSettings.Connection);
        };
        commonActions.Controls.Add(edit);
        Note("沿用设备连接设置中的显示、缩放、本地资源、快捷键转发和身份验证选项；启用后覆盖外部连接的同类选项。" +
            "\n目标地址、用户名、密码和临时票据仍使用原始连接，不放入通用设置。设备库连接不受影响。");
        rows.Controls.Add(_message);
        // Reparent the existing actions: registration, UAC and save semantics stay unchanged.
        var sections = SettingsPageDesign.Stack();
        SettingsPageDesign.Add(sections, SettingsPageDesign.Section(".rdp 文件打开方式",
            SettingsPageDesign.Note("双击文件时使用 Windows 默认应用；网页助手直接调用 mstsc.exe 时可另开下方代理。"),
            defaultsActions));
        SettingsPageDesign.Add(sections, SettingsPageDesign.Section("MSTSC 启动代理",
            _proxyState, proxyActions,
            SettingsPageDesign.Note("开启或恢复需要管理员授权，对本机所有用户生效；关闭后恢复此前设置。\n请保留当前 PureRDP.exe 路径，移动或删除程序前先关闭代理。不会替换系统文件或接管 H5 客户端。")));
        SettingsPageDesign.Add(sections, SettingsPageDesign.Section("通用连接设置",
            commonToggle, commonActions,
            SettingsPageDesign.Note("显示、缩放、本地资源、快捷键转发与身份验证使用通用设置。目标地址、用户名、密码及临时票据仍来自原始连接；设备库连接不受影响。"),
            _preferNative,
            SettingsPageDesign.Note("默认优先 PureRDP，建立失败后切换原生；网关、RemoteApp、多显示器及不能等价实现的安全模式保留原生处理。依赖厂商专属行为时，可选择始终使用原生；原生窗口不应用通用模板。")));
        SettingsPageDesign.Add(sections, _message);
        rows.Dispose();
        Controls.Add(sections);
        RefreshStatus();
    }

    private static bool IsOperationError(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or Win32Exception or ArgumentException;

    private void RefreshStatus()
    {
        RefreshProxyState();
        try
        {
            _message.Text = RdpFileAssociationService.IsRegistered() ? "已注册 .rdp 打开方式。" : "尚未注册 .rdp 打开方式。";
        }
        catch (Exception error) when (IsOperationError(error)) { _message.Text = "暂时无法读取 .rdp 文件关联状态；请在发布版程序中重试。"; }
    }

    private void RefreshProxyState()
    {
        if (_configuringProxy) return;
        try { DisplayProxyState(MstscProxyService.IsEnabled()); }
        catch (Exception error) when (IsOperationError(error)) { DisplayProxyState(null); }
    }

    // A presentation seam for isolated renders, with no registry writes.
    internal void DisplayProxyState(bool? enabled)
    {
        _proxyState.Text = enabled switch { true => "●  MSTSC 启动代理：已开启", false => "○  MSTSC 启动代理：未开启", _ => "!  MSTSC 启动代理：状态无法读取" };
        _proxyState.BackColor = enabled == true ? UiDesignTokens.Accent : UiDesignTokens.SurfacePressed;
        _proxyState.ForeColor = enabled == true ? Color.White : enabled == false ? UiDesignTokens.TextPrimary : UiDesignTokens.Warning;
        ((ModernButton)_enableProxy).Selected = enabled == true;
        ((ModernButton)_enableProxy).Kind = enabled == false ? ModernButtonKind.Primary : ModernButtonKind.Secondary;
        _enableProxy.Text = enabled == true ? "已开启 · 重新配置" : "开启启动代理";
    }

    private void RefreshCommonState()
    {
        _useCommon.Enabled = !_preferNative.Checked;
        var pending = _useCommon.Checked != _savedCommon;
        _commonState.Text = _preferNative.Checked ? "原生连接不应用" : pending ? (_useCommon.Checked ? "待保存：启用" : "待保存：停用") : (_useCommon.Checked ? "已启用" : "未启用");
        _commonState.BackColor = pending ? UiDesignTokens.HeroSky : _useCommon.Checked ? UiDesignTokens.AccentSoft : UiDesignTokens.SurfacePressed;
        _commonState.ForeColor = UiDesignTokens.TextPrimary;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _enableProxy != null) RefreshStatus();
    }

    private void ConfigureDefault(bool chooseDefault)
    {
        try
        {
            RdpFileAssociationService.Register();
            _message.Text = "已注册打开方式。请在 Windows 默认应用中将 .rdp 选为 PureRDP。";
            if (chooseDefault) RdpFileAssociationService.OpenDefaultApps();
        }
        catch (Exception error) when (IsOperationError(error)) { _message.Text = "操作未完成：" + error.Message; }
    }

    private async System.Threading.Tasks.Task ConfigureProxy(bool enabled)
    {
        _configuringProxy = true;
        _proxyState.Text = enabled ? "…  正在开启启动代理" : "…  正在关闭并恢复";
        _proxyState.BackColor = UiDesignTokens.HeroSky;
        _proxyState.ForeColor = UiDesignTokens.TextPrimary;
        _enableProxy.Enabled = _restoreProxy.Enabled = false;
        try
        {
            await MstscProxyService.RequestConfiguration(enabled);
            RefreshStatus();
        }
        catch (Exception error) when (IsOperationError(error)) { _message.Text = "操作未完成：" + error.Message; }
        finally
        {
            _configuringProxy = false;
            if (!IsDisposed) { _enableProxy.Enabled = _restoreProxy.Enabled = true; RefreshProxyState(); }
        }
    }

    public override void ApplyTo(GlobalSettings settings)
        => settings.RdpProxy = new GlobalRdpProxyPreferences { UseCommonSettings = _useCommon.Checked, PreferNativeClient = _preferNative.Checked,
            Connection = RdpProxySettingsService.Sanitize(_common) };
    public override void LoadFrom(GlobalSettings settings)
    {
        _savedCommon = settings.RdpProxy.UseCommonSettings;
        _preferNative.Checked = settings.RdpProxy.PreferNativeClient;
        _useCommon.Checked = settings.RdpProxy.UseCommonSettings;
        RefreshCommonState();
        _common = RdpProxySettingsService.Sanitize(settings.RdpProxy.Connection);
        RefreshStatus();
    }
}
