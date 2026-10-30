using PureRDP.Attributes;
using PureRDP.Configuration.Enums;
using PureRDP.Extensions;
using PureRDP.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Drawing;
using System.Linq;

namespace PureRDP.Configuration;

/// <summary>
/// Represents RDP connection specific settings
/// </summary>
internal class ConnectionSettings
{
    #region Connection options
    /// <summary>
    /// Gets or sets the hostname or IP address to connect to.
    /// Can include optional port using formats: hostname:port, IPv4:port, or [IPv6]:port
    /// Examples: server.com, server.com:3390, 192.168.1.1:3389, [2001:db8::1]:3389
    /// </summary>
    [RdpFile("full address")]
    public string? Hostname { get; set; }

    [RdpFile("purerdp device name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the domain for authentication
    /// </summary>
    [RdpFile("domain")]
    public string? Domain { get; set; }

    /// <summary>
    /// Gets or sets the username for authentication
    /// </summary>
    [RdpFile("username")]
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password for authentication.
    /// If not provided, RDP attempts Windows Credential Manager, then Kerberos/NTLM authentication.
    /// If both username and password are omitted, uses current Windows credentials (SSO).
    /// </summary>
    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Password { get; set; }

    // Windows DPAPI password from an external .rdp/.tmp file. Keep encrypted
    // until Connect, and never serialize it into a device or shared template.
    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EncryptedPassword51 { get; set; }

    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? PreserveImportedDesktop { get; set; }

    // Imported session cookies can contain short-lived bastion tickets. Keep
    // them in the active profile only, out of JSON and saved device mappings.
    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadBalanceInfo { get; set; }

    // An assistant's remote startup command can contain a one-time ticket.
    // Forward it to RDP only, never execute it locally or persist it.
    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AlternateShell { get; set; }

    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ShellWorkingDirectory { get; set; }

    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public NativeRdpFallback? NativeFallback { get; set; }

    [RdpFile("negotiate security layer")]
    public bool? NegotiateSecurityLayer { get; set; }

    /// <summary>
    /// Gets or sets whether to enable CredSSP (Kerberos/NTLM SSO) authentication
    /// </summary>
    [RdpFile("enablecredsspsupport", DefaultValue = "1")]
    public bool? EnableCredSsp { get; set; }

    [RdpFile("administrative session")]
    public bool? AdministrativeSession { get; set; }

    [RdpFile("prompt for credentials")]
    public bool? PromptForCredentials { get; set; }

    /// <summary>
    /// Gets or sets the server authentication level for certificate validation
    /// </summary>
    [RdpFile("authentication level", DefaultValue = "3")]
    public AuthenticationLevel? AuthenticationLevel { get; set; }

    /// <summary>
    /// Gets or sets the keep-alive interval in milliseconds. Sends keep-alive packets to maintain the connection.
    /// </summary>
    [NotMapped]
    public int? KeepAliveInterval { get; set; }

    /// <summary>
    /// Gets or sets the connection timeout in seconds. Determines how long to wait for the remote host to accept the connection.
    /// </summary>
    [RdpFile("connection timeout")]
    public int? ConnectionTimeout { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of automatic reconnection attempts when the connection is lost.
    /// </summary>
    [RdpFile("autoreconnect max retries", DefaultValue = "20")]
    public int? MaxReconnectAttempts { get; set; }
    #endregion Connection options

    #region Display options
    /// <summary>
    /// Gets or sets the desktop width
    /// </summary>
    [RdpFile("desktopwidth")]
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the desktop height
    /// </summary>
    [RdpFile("desktopheight")]
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets the color depth
    /// </summary>
    [RdpFile("session bpp", DefaultValue = "32")]
    public int? ColorDepth { get; set; }

    /// <summary>
    /// Gets or sets the desktop scale factor (DPI percentage) in the range from 100 to 500
    /// </summary>
    [RdpFile("desktopscalefactor")]
    public uint? ScaleFactor { get; set; }

    [RdpFile("purerdp tile scale factor", DefaultValue = "0")]
    public uint? TileScaleFactor { get; set; } = 0;

    [RdpFile("purerdp view scale factor", DefaultValue = "0")]
    public uint? ViewScaleFactor { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether to enable SmartSizing
    /// </summary>
    [RdpFile("smart sizing", DefaultValue = "0")]
    public bool? SmartSizing { get; set; }

    /// <summary>
    /// Gets or sets whether to enable automatic resize
    /// </summary>
    [RdpFile("dynamic resolution", DefaultValue = "1")]
    public bool? AutoResize { get; set; }

    /// <summary>
    /// Gets or sets whether a windowed connection should occupy the current
    /// monitor's working area while leaving the Windows taskbar visible.
    /// This is intentionally separate from RDP full-screen mode.
    /// </summary>
    [RdpFile("pure rdp window auto maximize", DefaultValue = "0")]
    public bool? AutoMaximizeWindow { get; set; }

    /// <summary>
    /// Determines whether a remote session window appears full screen
    /// </summary>
    [RdpFile("screen mode id", DefaultValue = "1")]
    public ScreenMode? ScreenMode { get; set; }

    /// <summary>
    /// Gets or sets whether to use multiple monitors (span across all monitors)
    /// </summary>
    [RdpFile("use multimon", DefaultValue = "0")]
    public bool? UseAllMonitors { get; set; }

    /// <summary>
    /// Gets or sets whether to enable compression
    /// </summary>
    [RdpFile("compression", DefaultValue = "1")]
    public bool? EnableCompression { get; set; }

    /// <summary>
    /// Gets or sets whether to enable bitmap persistence
    /// </summary>
    [RdpFile("bitmapcachepersistenable", DefaultValue = "1")]
    public bool? EnableBitmapPersistence { get; set; }
    #endregion Display options

    #region Experience options
    /// <summary>
    /// Gets or sets whether to display the connection bar in fullscreen mode
    /// </summary>
    [RdpFile("displayconnectionbar", DefaultValue = "1")]
    public bool? DisplayConnectionBar { get; set; }

    /// <summary>
    /// Gets or sets whether to pin the connection bar
    /// </summary>
    [RdpFile("pinconnectionbar", DefaultValue = "1")]
    public bool? PinConnectionBar { get; set; }
    #endregion Experience options

    #region Redirection options
    /// <summary>
    /// Gets or sets the audio playback mode
    /// </summary>
    [RdpFile("audiomode", DefaultValue = "0")]
    public AudioPlaybackMode? AudioPlaybackMode { get; set; }

    /// <summary>
    /// Gets or sets whether to enable audio input/microphone redirection
    /// </summary>
    [RdpFile("audiocapturemode", DefaultValue = "0")]
    public bool? RedirectAudioCapture { get; set; }

    /// <summary>
    /// Gets or sets the cameras to redirect. Use "*" for all cameras, device path for specific, or null for none
    /// </summary>
    [RdpFile("camerastoredirect")]
    public string? RedirectCameras { get; set; }

    /// <summary>
    /// Gets or sets whether to enable clipboard redirection
    /// </summary>
    [RdpFile("redirectclipboard", DefaultValue = "1")]
    public bool? RedirectClipboard { get; set; }

    /// <summary>
    /// Gets or sets whether to enable COM port redirection
    /// </summary>
    [RdpFile("redirectcomports", DefaultValue = "0")]
    public bool? RedirectComPorts { get; set; }

    /// <summary>
    /// Gets or sets the PnP devices to redirect. Use "*" for all, "DynamicDevices" for dynamic, specific device instance IDs or friendly names, or null for none.
    /// </summary>
    [RdpFile("devicestoredirect")]
    public string? RedirectPnpDevices { get; set; }

    /// <summary>
    /// Gets or sets whether to enable printer redirection
    /// </summary>
    [RdpFile("redirectprinters", DefaultValue = "0")]
    public bool? RedirectPrinters { get; set; }

    /// <summary>
    /// Gets or sets the drives to redirect. Use "*" for all, "DynamicDrives" for dynamic, specific drive letters like "C;E", or null for none.
    /// </summary>
    [RdpFile("drivestoredirect")]
    public string? RedirectDrives { get; set; }

    /// <summary>
    /// Gets or sets whether to enable location redirection
    /// </summary>
    [RdpFile("redirectlocation", DefaultValue = "0")]
    public bool? RedirectLocation { get; set; }

    /// <summary>
    /// Gets or sets whether to enable smart card redirection
    /// </summary>
    [RdpFile("redirectsmartcards", DefaultValue = "1")]
    public bool? RedirectSmartCards { get; set; }

    /// <summary>
    /// Gets or sets whether to enable WebAuthn redirection for Windows Hello and security keys
    /// </summary>
    [RdpFile("redirectwebauthn", DefaultValue = "1")]
    public bool? RedirectWebAuthn { get; set; }

    /// <summary>
    /// Gets or sets where Windows key combinations are handled in an ordinary
    /// resizable window.
    /// </summary>
    [RdpFile("keyboardhook", DefaultValue = "0")]
    public KeyboardMode? KeyboardModeWindowed { get; set; } = Configuration.Enums.KeyboardMode.OnLocalComputer;

    /// <summary>
    /// Gets or sets where Windows key combinations are handled in a maximized
    /// borderless window that still uses the working area.
    /// </summary>
    [RdpFile("purerdp keyboardhook window fullscreen", DefaultValue = "0")]
    public KeyboardMode? KeyboardModeWindowFullscreen { get; set; } = Configuration.Enums.KeyboardMode.OnLocalComputer;

    /// <summary>
    /// Gets or sets where Windows key combinations are handled in true RDP
    /// fullscreen mode.
    /// </summary>
    [RdpFile("purerdp keyboardhook fullscreen", DefaultValue = "0")]
    public KeyboardMode? KeyboardModeFullscreen { get; set; } = Configuration.Enums.KeyboardMode.OnLocalComputer;
    #endregion Redirection options

    #region PureRDP specific settings (non-standard)
    [NotMapped]
    public DateTime LastConnected { get; set; }

    /// <summary>
    /// Gets or sets a color override for the window icon badge.
    /// Allowed values are either a named color (parsed by <see cref="Color.FromName(string)"/>)
    /// or a hex value in the form RRGGBB or AARRGGBB (optional leading '#').
    /// Examples: "Red", "#FF0000", "00FF00", "#80FF0000"
    /// </summary>
    [RdpFile("pure rdp badge color")]
    public Color? BadgeColor { get; set; }

    /// <summary>
    /// Hides PureRDP's custom control strip in an ordinary window. This also
    /// applies to the borderless working-area "全屏窗口" state; true RDP
    /// fullscreen has its own setting below.
    /// </summary>
    [RdpFile("purerdp control bar hidden windowed", DefaultValue = "0")]
    public bool ControlBarHiddenWindowed { get; set; }

    /// <summary>
    /// Hides PureRDP's custom control strip in true RDP fullscreen mode.
    /// </summary>
    [RdpFile("purerdp control bar hidden fullscreen", DefaultValue = "0")]
    public bool ControlBarHiddenFullscreen { get; set; }

    #endregion PureRDP specific settings (non-standard)

    public ConnectionSettings Clone()
        => (ConnectionSettings)MemberwiseClone();

    /// <summary>
    /// Default connection settings.
    /// </summary>
    [NotMapped]
    public static readonly ConnectionSettings Default = CreateDefaultSettings();

    private static ConnectionSettings CreateDefaultSettings()
    {
        var defaultSettingValues = SettingsToRdpFileService.ConnectionMapping()
            .Select(map => new
            {
                map.Property,
                DefaultValue = map.DefaultValue.ToSettingValue(map.Property.PropertyType)
            })
            .Where(x => x.DefaultValue != null);

        var defaultSettings = new ConnectionSettings();
        foreach (var setting in defaultSettingValues)
            setting.Property.SetValue(defaultSettings, setting.DefaultValue, null);
        return defaultSettings;
    }

    #region Validation
    /// <summary>
    /// Indicates whether all validation checks pass
    /// </summary>
    public bool IsValid() => Validate().Count == 0;

    /// <summary>
    /// Validates the connection settings
    /// </summary>
    /// <returns>A list of validation error messages. Empty if validation succeeds.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Width is <= 0)
            errors.Add("宽度必须大于 0。");

        if (Height is <= 0)
            errors.Add("高度必须大于 0。");

        if (ColorDepth.HasValue && ColorDepth.Value is not (8 or 15 or 16 or 24 or 32))
            errors.Add("色彩深度必须为 8、15、16、24 或 32。");

        if (ScaleFactor is < 100 or > 500)
            errors.Add("默认 DPI 必须在 100% 到 500% 之间。");
        if (TileScaleFactor is > 0 and < 50 or > 200)
            errors.Add("宫格缩放必须在 50% 到 200% 之间，或选择适应窗口。");
        if (ViewScaleFactor is > 0 and < 50 or > 200)
            errors.Add("默认画面缩放必须在 50% 到 200% 之间，或选择适应当前窗口。");

        if (!Hostname.IsValidHostname())
            errors.Add("请输入有效的计算机名称或 IP 地址。");

        return errors;
    }
    #endregion Validation
}
