using PureRDP.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace PureRDP.Services;

/// <summary>
/// Persists window settings without allowing an unwritable profile directory to crash the UI.
/// </summary>
internal static class WindowPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static WindowSettings? Load(string? hostname)
    {
        var key = string.IsNullOrEmpty(hostname) ? string.Empty : hostname;
        foreach (var filePath in GetCandidateFilePaths())
        {
            try
            {
                if (!File.Exists(filePath))
                    continue;

                var json = File.ReadAllText(filePath);
                var allSettings = JsonSerializer.Deserialize<Dictionary<string, WindowSettings>>(json, JsonOptions);
                if (allSettings != null && allSettings.TryGetValue(key, out var settings))
                    return settings;
            }
            catch (IOException)
            {
                // Try the next writable/readable location.
            }
            catch (UnauthorizedAccessException)
            {
                // The profile may belong to another Windows account.
            }
            catch (JsonException)
            {
                // Ignore a damaged optional settings file.
            }
        }

        return null;
    }

    public static void Save(this WindowSettings settings, string? hostname)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var key = string.IsNullOrEmpty(hostname) ? string.Empty : hostname;
        var allSettings = LoadAllSettings();
        allSettings[key] = settings;
        var json = JsonSerializer.Serialize(allSettings, JsonOptions);

        foreach (var directory in GetCandidateDirectories())
        {
            var filePath = Path.Combine(directory, "window-settings.json");
            var temporaryPath = filePath + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, filePath, true);
                return;
            }
            catch (IOException)
            {
                TryDelete(temporaryPath);
            }
            catch (UnauthorizedAccessException)
            {
                TryDelete(temporaryPath);
            }
        }

        // Window persistence is optional. Never surface an unhandled exception while closing.
    }

    public static void ApplyWindowSettings(this Form window, WindowSettings windowSettings)
    {
        var hasPosition = windowSettings.WindowX.HasValue && windowSettings.WindowY.HasValue;
        var hasSize = windowSettings.WindowWidth.HasValue || windowSettings.WindowHeight.HasValue;

        if (hasPosition || hasSize)
        {
            window.StartPosition = FormStartPosition.Manual;
            if (windowSettings.WindowX.HasValue)
                window.Left = windowSettings.WindowX.Value;
            if (windowSettings.WindowY.HasValue)
                window.Top = windowSettings.WindowY.Value;
            if (windowSettings.WindowWidth.HasValue)
                window.Width = windowSettings.WindowWidth.Value;
            if (windowSettings.WindowHeight.HasValue)
                window.Height = windowSettings.WindowHeight.Value;
        }

        if (windowSettings.Maximized == true)
            window.WindowState = FormWindowState.Maximized;
    }

    public static WindowSettings GetWindowSettings(this Form window)
    {
        return new WindowSettings
        {
            WindowX = window.WindowState == FormWindowState.Normal ? window.Left : window.RestoreBounds.Left,
            WindowY = window.WindowState == FormWindowState.Normal ? window.Top : window.RestoreBounds.Top,
            WindowWidth = window.WindowState == FormWindowState.Normal ? window.Width : window.RestoreBounds.Width,
            WindowHeight = window.WindowState == FormWindowState.Normal ? window.Height : window.RestoreBounds.Height,
            Maximized = window.WindowState == FormWindowState.Maximized,
        };
    }

    private static Dictionary<string, WindowSettings> LoadAllSettings()
    {
        foreach (var filePath in GetCandidateFilePaths())
        {
            try
            {
                if (!File.Exists(filePath))
                    continue;

                return JsonSerializer.Deserialize<Dictionary<string, WindowSettings>>(
                           File.ReadAllText(filePath), JsonOptions)
                       ?? new Dictionary<string, WindowSettings>();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }

        return new Dictionary<string, WindowSettings>();
    }

    private static IEnumerable<string> GetCandidateDirectories()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddCandidate(candidates, Path.Combine(AppContext.BaseDirectory, "data"));
        AddCandidate(candidates, Path.Combine(Path.GetTempPath(), ProductPaths.CurrentDataFolderName));

        foreach (var candidate in candidates)
            yield return Path.Combine(candidate, ProductPaths.CurrentDataFolderName);
    }

    private static IEnumerable<string> GetCandidateFilePaths()
    {
        foreach (var directory in GetCandidateDirectories())
            yield return Path.Combine(directory, "window-settings.json");
    }

    private static void AddCandidate(HashSet<string> candidates, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
            candidates.Add(root);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
