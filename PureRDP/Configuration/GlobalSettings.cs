using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using PureRDP.Configuration.Enums;
namespace PureRDP.Configuration;
internal sealed class GlobalSettings
{
    public GlobalWindowPreferences Windows { get; set; } = new();
    public GlobalPreviewPreferences Previews { get; set; } = new();
    public GlobalMonitoringPreferences Monitoring { get; set; } = new();
    public GlobalShortcutPreferences Shortcuts { get; set; } = new();
    public GlobalRdpProxyPreferences RdpProxy { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
    public GlobalSettings Clone()
    {
        var clone = JsonSerializer.Deserialize<GlobalSettings>(JsonSerializer.Serialize(this)) ?? new();
        clone.Windows ??= new();
        clone.Previews ??= new();
        clone.Monitoring ??= new();
        clone.Shortcuts ??= new();
        clone.RdpProxy ??= new();
        clone.RdpProxy.Connection = Services.RdpProxySettingsService.Sanitize(clone.RdpProxy.Connection);
        return clone;
    }
}
internal sealed class GlobalPreviewPreferences
{
    public bool Enabled { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}
internal sealed class GlobalWindowPreferences
{
    public bool RoundedConnectionCorners { get; set; } = true;
    public bool AutoTileSessions { get; set; }
    public ControlBarPosition ControlBarDock { get; set; } = ControlBarPosition.Right;
    public bool ControlBarAutoHide { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}
internal sealed class GlobalRdpProxyPreferences
{
    public bool PreferNativeClient { get; set; }
    public bool UseCommonSettings { get; set; }
    public ConnectionSettings Connection { get; set; } = new();
}

internal sealed class GlobalMonitoringPreferences
{
    public int AvailabilityScanIntervalSeconds { get; set; } = 3600;
    public int ConnectionQualityIntervalSeconds { get; set; } = 3;
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

internal sealed class GlobalShortcutPreferences
{
    public GlobalShortcutEffectMode EffectMode { get; set; } = GlobalShortcutEffectMode.SyncConnectionWindow;
    public string MinimizeConnectionWindow { get; set; } = "Ctrl+Alt+Z";
    public string MaximizeConnectionWindow { get; set; } = "Alt+X";
    public string ShowControlConsole { get; set; } = "Alt+W";
    public string ToggleTrueFullscreen { get; set; } = "Alt+V";
    public string CycleConnectionWindow { get; set; } = "Alt+N";
    public string CloseConnectionWindow { get; set; } = "Alt+M";
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public GlobalShortcutPreferences Clone()
        => new()
        {
            EffectMode = EffectMode,
            MinimizeConnectionWindow = MinimizeConnectionWindow,
            MaximizeConnectionWindow = MaximizeConnectionWindow,
            ShowControlConsole = ShowControlConsole,
            ToggleTrueFullscreen = ToggleTrueFullscreen,
            CycleConnectionWindow = CycleConnectionWindow,
            CloseConnectionWindow = CloseConnectionWindow,
            AdditionalSettings = AdditionalSettings == null
                ? null
                : new Dictionary<string, JsonElement>(AdditionalSettings),
        };
}
