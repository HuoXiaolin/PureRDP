using PureRDP.Configuration;
using System;
using System.IO;
using System.Text.Json;
namespace PureRDP.Services;
internal static class GlobalSettingsService
{
    internal static event Action<int>? ConnectionQualityIntervalChanged;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductPaths.CurrentDataFolderName, "global-settings.json");
    public static GlobalSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(SettingsPath), Options) ?? new();
                settings.Windows ??= new();
                settings.Previews ??= new();
                settings.Monitoring ??= new();
                settings.Shortcuts ??= new();
                settings.RdpProxy ??= new();
                settings.RdpProxy.Connection = RdpProxySettingsService.Sanitize(settings.RdpProxy.Connection);
                RemoveDeletedShortcut(settings);
                // Alt+C was the historical built-in console shortcut. Treat
                // that untouched historical default as the new default while
                // leaving all other user-selected shortcuts unchanged.
                if (string.Equals(settings.Shortcuts.ShowControlConsole, "Alt+C",
                    StringComparison.OrdinalIgnoreCase))
                    settings.Shortcuts.ShowControlConsole = "Alt+W";
                settings.Monitoring.AvailabilityScanIntervalSeconds = NormalizeScanInterval(settings.Monitoring.AvailabilityScanIntervalSeconds);
                return settings;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return new GlobalSettings
        {
            Windows = new GlobalWindowPreferences { AutoTileSessions = ConnectionLibraryService.Load().AutoTileSessions },
            Monitoring = new GlobalMonitoringPreferences(),
            Shortcuts = new GlobalShortcutPreferences(),
        };
    }
    public static bool Save(GlobalSettings settings)
    {
        try
        {
            settings = settings.Clone();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, SettingsPath, true);
            ConnectionQualityIntervalChanged?.Invoke(settings.Monitoring.ConnectionQualityIntervalSeconds);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool Export(GlobalSettings settings, string filePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            var normalized = settings.Clone();
            normalized.Monitoring.AvailabilityScanIntervalSeconds =
                NormalizeScanInterval(normalized.Monitoring.AvailabilityScanIntervalSeconds);
            File.WriteAllText(filePath, JsonSerializer.Serialize(normalized, Options));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool TryImport(string filePath, out GlobalSettings settings, out string error)
    {
        settings = new GlobalSettings();
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                error = "文件不存在。";
                return false;
            }

            settings = JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(filePath), Options) ?? new();
            settings.Windows ??= new();
            settings.Previews ??= new();
            settings.Monitoring ??= new();
            settings.Shortcuts ??= new();
            settings.RdpProxy ??= new();
            settings.RdpProxy.Connection = RdpProxySettingsService.Sanitize(settings.RdpProxy.Connection);
            RemoveDeletedShortcut(settings);
            settings.Monitoring.AvailabilityScanIntervalSeconds =
                NormalizeScanInterval(settings.Monitoring.AvailabilityScanIntervalSeconds);
            return true;
        }
        catch (JsonException)
        {
            error = "JSON 格式无效。";
            return false;
        }
        catch (IOException)
        {
            error = "无法读取文件。";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有读取文件的权限。";
            return false;
        }
    }

    private static int NormalizeScanInterval(int seconds)
        => Math.Clamp(seconds <= 0 ? 3600 : seconds, 5, 86400);

    private static void RemoveDeletedShortcut(GlobalSettings settings)
        => settings.Shortcuts.AdditionalSettings?.Remove("releaseConnectionFocus");
}
