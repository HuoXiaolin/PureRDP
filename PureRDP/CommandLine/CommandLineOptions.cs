using PureRDP.Configuration;
using PureRDP.Configuration.Enums;
using PureRDP.Extensions;
using PureRDP.Platform;
using PureRDP.Services;
using Mono.Options;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PureRDP.CommandLine;

/// <summary>
/// Parses and stores command line options for PureRDP
/// </summary>
internal class CommandLineOptions
{
    /// <summary>
    /// Gets or sets whether to show help
    /// </summary>
    private bool ShowHelp { get; set; }

    /// <summary>
    /// Parses command line arguments using Mono.Options
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <returns>Parsed options</returns>
    public static ApplicationSettings Parse(string[] args)
    {
        var options = new CommandLineOptions();
        var settings = new ApplicationSettings();

        try
        {
            var optionSet = CreateOptionSet(options, settings);
            var extraArgs = optionSet.Parse(args);

            ProcessPositionalRdpFileArgument(settings, extraArgs);
            ProcessPositionalHostnameArgument(settings, extraArgs);

            if (extraArgs.Count > 0)
                throw new OptionException($"无法识别的参数： {string.Join(", ", extraArgs)}", string.Join(", ", extraArgs));

            if (options.ShowHelp)
                ShowHelpAndExit();

            if (!string.IsNullOrEmpty(settings.RdpFilePath))
            {
                var rdpFileSettings = RdpFileService.ReadFromFile(settings.RdpFilePath);
                settings.Connection.MergeFrom(rdpFileSettings, nameof(ConnectionSettings.Password));
            }

            settings.Connection.MergeFrom(ConnectionSettings.Default);

            return settings;
        }
        catch (OptionException ex)
        {
            var errorMessage = $"命令行参数错误：\n\n{ex.Message}\n\n使用 --help 查看用法。";
            DisplayMessage(errorMessage, "PureRDP - 命令行错误", MessageBoxIcon.Error);
            Application.Exit();
            throw;
        }
    }

    /// <summary>
    /// Processes positional argument to check if it's an RDP file and sets RdpFilePath accordingly.
    /// If the first positional argument ends with .rdp and RdpFilePath is not already set,
    /// it will be used as the RDP file path.
    /// </summary>
    /// <param name="settings">The application settings to update</param>
    /// <param name="args">The list of extra arguments from command line parsing</param>
    private static void ProcessPositionalRdpFileArgument(ApplicationSettings settings, IList<string> args)
    {
        if (args.Count <= 0)
            return;

        var arg0IsRdpFile = args[0].EndsWith(".rdp", StringComparison.OrdinalIgnoreCase);
        if (!arg0IsRdpFile)
            return;

        settings.RdpFilePath = args[0];
        args.RemoveAt(0);
    }

    /// <summary>
    /// Processes positional argument as a hostname if it's not an RDP file.
    /// If the first positional argument doesn't end with .rdp and hostname is not already set,
    /// it will be used as the hostname.
    /// </summary>
    /// <param name="settings">The application settings to update</param>
    /// <param name="args">The list of extra arguments from command line parsing</param>
    private static void ProcessPositionalHostnameArgument(ApplicationSettings settings, IList<string> args)
    {
        if (args.Count <= 0)
            return;

        if (!args[0].IsValidHostname())
            return;

        settings.Connection.Hostname = args[0];
        args.RemoveAt(0);
    }

    private static OptionSet CreateOptionSet(CommandLineOptions options, ApplicationSettings settings)
    {
        var optionSet = new OptionSet
        {
            // Connection options
            { "h|host|hostname=", "远程计算机名称或 IP 地址。可使用 hostname:port、IPv4:port、[IPv6]:port。",
                v => settings.Connection.Hostname = v },
            { "d|domain=", "用于身份验证的域名。",
                v => settings.Connection.Domain = v },
            { "u|user|username=", "用户名。支持 User、DOMAIN\\user、user@domain。",
                v => ParseUsernameAndDomain(v, settings) },
            { "password=", "登录密码（不建议在命令行中传入）。",
                v => settings.Connection.Password = v },
            { "enable-credssp|credssp", "启用 CredSSP 身份验证。",
                v => settings.Connection.EnableCredSsp = v != null ? true : null },
            { "auth-level|authentication-level=", "服务器证书验证：no-warning、no-connect、warn、none。",
                v => settings.Connection.AuthenticationLevel = ParseAuthenticationLevel(v) },
            { "keep-alive-interval=", "保持连接的数据包间隔，单位毫秒。",
                (int? v) => settings.Connection.KeepAliveInterval = v },
            { "connection-timeout|connect-timeout=", "连接超时时间，单位秒。",
                (int? v) => settings.Connection.ConnectionTimeout = v },
            { "max-reconnect-attempts|max-reconnect-retries=", "连接中断后的最大自动重试次数。",
                (int? v) => settings.Connection.MaxReconnectAttempts = v },

            // Display options
            { "width=", "远程桌面宽度，单位像素。",
                (int? v) => settings.Connection.Width = v },
            { "height=", "远程桌面高度，单位像素。",
                (int? v) => settings.Connection.Height = v },
            { "color-depth=", "色彩深度：8、15、16、24 或 32，默认 32。",
                (int? v) => settings.Connection.ColorDepth = v },
            { "scale|scale-factor=", "DPI 缩放百分比，范围 100–500；省略则跟随本地。",
                (uint? v) => settings.Connection.ScaleFactor = v },
            { "smart-sizing", "拉伸现有画面以适应窗口。",
                v => settings.Connection.SmartSizing = v != null ? true: null },
            { "no-auto-resize", "关闭随窗口大小变化的动态分辨率。",
                v => settings.Connection.AutoResize = v != null ? false: null },
            { "f|fullscreen", "以全屏模式启动。",
                v => settings.Connection.ScreenMode = v != null ? ScreenMode.FullScreen : null },
            { "all-monitors|multi-monitor|multimon", "远程会话使用所有显示器。",
                v => settings.Connection.UseAllMonitors = v != null ? true: null },
            { "badge-color|badgecolor=", "窗口图标标记颜色，支持英文颜色名或 RGB/ARGB 十六进制值。",
                v => settings.Connection.BadgeColor = ParseBadgeColor(v) },

            // Experience options
            { "show-connection-bar|display-connection-bar", "全屏时显示连接栏。",
                v => settings.Connection.DisplayConnectionBar = v != null ? true: null },
            { "hide-connection-bar|no-connection-bar", "全屏时隐藏连接栏。",
                v => settings.Connection.DisplayConnectionBar = v != null ? false: null },
            { "pin-connection-bar", "全屏时固定连接栏。",
                v => settings.Connection.PinConnectionBar = v != null ? true: null },

            // Redirection options
            { "redirect-clipboard", "剪贴板默认启用；使用 --redirect-clipboard- 关闭。",
                v => settings.Connection.RedirectClipboard = v == null ? false: null },
            { "audio-mode=", "声音播放位置：local 本地、remote 远端、none 不播放。",
                v => settings.Connection.AudioPlaybackMode = ParseAudioMode(v) },
            { "redirect-audio-capture|redirect-microphone", "让远端应用使用本地麦克风。",
                v => settings.Connection.RedirectAudioCapture = v != null ? true: null },
            { "redirect-cameras=", "摄像头重定向：* 表示全部，也可指定设备路径。",
                v => settings.Connection.RedirectCameras = v },
            { "redirect-comports|redirect-com-ports", "启用 COM 串口重定向。",
                v => settings.Connection.RedirectComPorts = v != null ? true: null },
            { "redirect-pnp-devices=|redirect-pnp=", "即插即用设备重定向：*、DynamicDevices 或设备标识。",
                v => settings.Connection.RedirectPnpDevices = v },
            { "redirect-drives=", "启用本地磁盘重定向；当前命令行实现映射所有磁盘。",
                v => settings.Connection.RedirectDrives = v != null ? "*":null },
            { "redirect-location", "启用位置重定向。",
                v => settings.Connection.RedirectLocation = v != null ? true: null },
            { "redirect-printers", "让远端应用使用本地打印机。",
                v => settings.Connection.RedirectPrinters = v != null ? true: null },
            { "redirect-smartcards", "启用智能卡重定向。",
                v => settings.Connection.RedirectSmartCards = v != null ? true: null },
            { "redirect-webauthn", "启用 WebAuthn 重定向（Windows Hello、安全密钥）。",
                v => settings.Connection.RedirectWebAuthn = v != null ? true: null },
            { "keyboard-mode=", "Windows 组合键：local 本地、remote 远端、fullscreen 仅全屏。",
                v => ApplyKeyboardModeOption(settings.Connection, ParseKeyboardMode(v)) },

            // Window state and position options
            { "window-position|windows-pos|position|pos=", "窗口位置，格式 x,y。",
                v => ParseWindowPosition(v, settings) },
            { "window-size|size=", "窗口尺寸，格式 宽,高 或 宽x高（使用数字）。",
                v => ParseWindowSize(v, settings) },
            { "windows-maximized|maximized|windows-max|max", "以最大化窗口启动。",
                v => settings.Window.Maximized = v != null ? true: null },

            // Other options
            { "no-auto-connect", "指定地址后不自动连接。",
                v => settings.NoAutoConnect = v != null ? true : null },
            { "no-close-on-disconnect|no-auto-close", "断开连接后保留窗口。",
                v => settings.NoCloseOnDisconnect = v != null ? true: null },
            { "rdp|rdp-file=", "从指定 .rdp 文件读取连接设置。",
                v => settings.RdpFilePath = v },
            { "edit", "打开指定 .rdp 文件进行编辑。",
                v => settings.EditRdpFile = v != null ? true : null },
            { "silent|silent-startup", "启动后仅驻留系统托盘，不显示主控制台。",
                v => settings.SilentStartup = v != null ? true : null },

            { "help|?", "显示命令行帮助。",
                v => options.ShowHelp = v != null }
        };

        return optionSet;
    }

    private static void ParseUsernameAndDomain(string value, ApplicationSettings settings)
    {
        var (username, domain) = value.ParseUsernameAndDomain();
        settings.Connection.Username = username;
        if (domain != null)
            settings.Connection.Domain = domain;
    }

    private static Color? ParseBadgeColor(string? value)
    {
        try
        {
            var colorMatch = Regex.Match(value ?? string.Empty, @"#?((?<hex>[\dA-Fa-f]{8}|[\dA-Fa-f]{6})|(?<name>\w+))");
            if (!colorMatch.Success)
                return null;

            var hexValue = colorMatch.Groups["hex"].Value;
            if (hexValue.Length > 0)
                return $"#{hexValue}".ToColor();

            var colorName = colorMatch.Groups["name"].Value;
            if (colorName.Length > 0)
                return colorName.ToColor();

            return null;
        }
        catch (ArgumentException)
        {
            throw new OptionException(
                $"无效的标记颜色： {value}. 请使用英文颜色名或十六进制 RGB/ARGB，例如 Red、#FF0000、#80FF0000。",
                "badge-color"
            );
        }
    }

    /// <summary>
    /// Parses a window position string in "x,y" format
    /// </summary>
    private static void ParseWindowPosition(string? value, ApplicationSettings settings)
    {
        if (value == null)
            return;

        var parts = value.Split(',');
        if (parts.Length != 2)
            throw new OptionException($"窗口位置格式无效： {value}. 格式应为 x,y，例如 100,200。", "window-position");

        if (!int.TryParse(parts[0].Trim(), out var x))
            throw new OptionException($"窗口横坐标无效： {parts[0]}. 必须为整数。", "window-position");

        if (!int.TryParse(parts[1].Trim(), out var y))
            throw new OptionException($"窗口纵坐标无效： {parts[1]}. 必须为整数。", "window-position");

        settings.Window.WindowX = x;
        settings.Window.WindowY = y;
    }

    /// <summary>
    /// Parses a window size string in "x&lt;width&gt;,x&lt;height&gt;" or "x&lt;width&gt;x&lt;height&gt;" format
    /// </summary>
    private static void ParseWindowSize(string? value, ApplicationSettings settings)
    {
        if (value == null)
            return;

        // Try both comma and 'x' as separators
        var parts = value.Split(',', 'x');

        if (parts.Length != 2)
            throw new OptionException($"窗口尺寸格式无效： {value}. 请使用 宽,高 或 宽x高，例如 1024,768 或 1024x768。", "window-size");

        if (!int.TryParse(parts[0].Trim(), out var width))
            throw new OptionException($"窗口宽度无效： {parts[0]}. 必须为整数。", "window-size");

        if (!int.TryParse(parts[1].Trim(), out var height))
            throw new OptionException($"窗口高度无效： {parts[1]}. 必须为整数。", "window-size");

        settings.Window.WindowWidth = width;
        settings.Window.WindowHeight = height;
    }

    /// <summary>
    /// Parses an audio mode string to AudioPlaybackMode enum
    /// </summary>
    private static AudioPlaybackMode? ParseAudioMode(string? value)
    {
        if (value == null)
            return null;

        switch (value.ToLowerInvariant())
        {
            case "local":
            case "play-local":
            case "0":
                return AudioPlaybackMode.PlayOnLocal;
            case "remote":
            case "play-remote":
            case "1":
                return AudioPlaybackMode.PlayOnRemote;
            case "none":
            case "dont-play":
            case "do-not-play":
            case "2":
                return AudioPlaybackMode.DoNotPlay;
            default:
                throw new OptionException($"声音播放模式无效： {value}. 可用值： local, remote, none (or 0, 1, 2)", "audio-mode");
        }
    }

    /// <summary>
    /// Parses a keyboard mode string to KeyboardMode enum
    /// </summary>
    private static KeyboardMode? ParseKeyboardMode(string? value)
    {
        if (value == null)
            return null;

        switch (value.ToLowerInvariant())
        {
            case "local":
            case "0":
                return KeyboardMode.OnLocalComputer;
            case "remote":
            case "1":
                return KeyboardMode.OnRemoteComputer;
            case "fullscreen":
            case "fullscreen-only":
            case "full-screen-only":
            case "full-screen":
            case "2":
                return KeyboardMode.InFullScreenOnly;
            default:
                throw new OptionException($"键盘模式无效： {value}. 可用值： local, remote, fullscreen-only (or 0, 1, 2)", "keyboard-mode");
        }
    }

    private static void ApplyKeyboardModeOption(ConnectionSettings settings, KeyboardMode? mode)
    {
        if (mode is KeyboardMode.InFullScreenOnly)
        {
            settings.KeyboardModeWindowed = KeyboardMode.OnLocalComputer;
            settings.KeyboardModeWindowFullscreen = KeyboardMode.OnLocalComputer;
            settings.KeyboardModeFullscreen = KeyboardMode.OnRemoteComputer;
            return;
        }

        settings.KeyboardModeWindowed = mode;
        settings.KeyboardModeWindowFullscreen = mode;
        settings.KeyboardModeFullscreen = mode;
    }

    /// <summary>
    /// Parses an authentication level string to AuthenticationLevel enum
    /// </summary>
    private static AuthenticationLevel? ParseAuthenticationLevel(string? value)
    {
        if (value == null)
            return null;

        switch (value.ToLowerInvariant())
        {
            case "no-warning":
            case "connect-without-warning":
            case "0":
                return AuthenticationLevel.ConnectWithoutWarning;
            case "no-connect":
            case "dont-connect":
            case "do-not-connect":
            case "1":
                return AuthenticationLevel.DoNotConnect;
            case "warn":
            case "warn-user":
            case "warning":
            case "2":
                return AuthenticationLevel.WarnUser;
            case "none":
            case "no-requirement":
            case "3":
                return AuthenticationLevel.NoRequirement;
            default:
                throw new OptionException($"身份验证级别无效： {value}. 可用值： no-warning, no-connect, warn, none (or 0, 1, 2, 3)", "authentication-level");
        }
    }

    private static void ShowHelpAndExit()
    {
        var helpText = GetHelpText();
        DisplayMessage(helpText, "PureRDP - 帮助", MessageBoxIcon.Information);
        Application.Exit();
    }

    /// <summary>
    /// Shows help information
    /// </summary>
    private static string GetHelpText()
    {
        using var writer = new System.IO.StringWriter();
        writer.WriteLine("PureRDP — 集中管理，一点即达的远程桌面客户端");
        writer.WriteLine("用法：PureRDP.exe [选项] [计算机地址或 .rdp 文件]");
        writer.WriteLine("默认自动连接；使用 --no-auto-connect 可关闭自动连接。");
        writer.WriteLine();
        CreateOptionSet(new CommandLineOptions(), new ApplicationSettings()).WriteOptionDescriptions(writer);
        writer.WriteLine();
        writer.WriteLine("示例：PureRDP.exe server01 --redirect-cameras=* --redirect-microphone --redirect-printers");
        return writer.ToString();
    }

    /// <summary>
    /// Displays a message either to the console or via MessageBox depending on how the application was launched
    /// </summary>
    /// <param name="message">The message to display</param>
    /// <param name="title">The title for the MessageBox (used only when not in console mode)</param>
    /// <param name="icon">The icon for the MessageBox (used only when not in console mode)</param>
    private static void DisplayMessage(string message, string title, MessageBoxIcon icon)
    {
        // If launched from console, attach to it and write output
        if (IsLaunchedFromConsole())
        {
            Console.WriteLine();
            Console.WriteLine(message);

            // Send a newline to release the console prompt
            SendKeys.SendWait("{ENTER}");
        }
        else
        {
            // Otherwise show message box (e.g., when double-clicked)
            MessageBox.Show(message, title, MessageBoxButtons.OK, icon);
        }
    }

    /// <summary>
    /// Checks if the application was launched from a console window
    /// </summary>
    private static bool IsLaunchedFromConsole()
    {
        // Try to attach to the parent process's console
        return Interop.AttachConsole(Interop.ATTACH_PARENT_PROCESS);
    }
}
