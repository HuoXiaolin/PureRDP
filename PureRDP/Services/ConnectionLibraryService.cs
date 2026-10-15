using PureRDP.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PureRDP.Services;

/// <summary>
/// Persists the connection library layout separately from the RDP connection files.
/// Layout preferences are best-effort and never prevent the application from opening.
/// </summary>
internal sealed class ConnectionLibraryLayoutSettings
{
    public string ViewMode { get; set; } = nameof(ConnectionLibraryViewMode.Cards);
    public List<string> Order { get; set; } = [];
    public bool AutoTileSessions { get; set; }
}

internal static class ConnectionLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ConnectionLibraryLayoutSettings Load()
    {
        foreach (var path in GetCandidatePaths())
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                return JsonSerializer.Deserialize<ConnectionLibraryLayoutSettings>(
                           File.ReadAllText(path), JsonOptions)
                       ?? new ConnectionLibraryLayoutSettings();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }

        return new ConnectionLibraryLayoutSettings();
    }

    public static void Save(ConnectionLibraryViewMode viewMode, IEnumerable<string> hostnames)
    {
        var settings = Load();
        settings.ViewMode = viewMode.ToString();
        settings.Order = hostnames
                .Where(hostname => !string.IsNullOrWhiteSpace(hostname))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        Write(settings);
    }

    private static bool Write(ConnectionLibraryLayoutSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        foreach (var path in GetCandidatePaths())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                Directory.CreateDirectory(directory);
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, true);
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddRoot(roots, Path.Combine(AppContext.BaseDirectory, "data"));
        AddRoot(roots, Path.Combine(Path.GetTempPath(), ProductPaths.CurrentDataFolderName));

        foreach (var root in roots)
            yield return Path.Combine(root, ProductPaths.CurrentDataFolderName, "connection-library.json");
    }

    private static void AddRoot(HashSet<string> roots, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
            roots.Add(root);
    }
}
