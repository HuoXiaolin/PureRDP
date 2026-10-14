using PureRDP.Configuration;
using PureRDP.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PureRDP.Services;

/// <summary>
/// Stores recent connections in the first location that the current Windows user can write.
/// </summary>
internal static class RecentConnectionsService
{
    public static List<ConnectionSettings> LoadAll()
    {
        var connections = new List<ConnectionSettings>();
        foreach (var directory in GetCandidateDirectories())
        {
            try
            {
                if (!Directory.Exists(directory))
                    continue;

                foreach (var filePath in Directory.GetFiles(directory, "*.rdp", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var settings = RdpFileService.ReadFromFile(filePath);
                        if (settings.Hostname.IsValidHostname())
                            connections.Add(settings);
                    }
                    catch (Exception)
                    {
                        // One damaged recent file should not hide all other devices.
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return connections
            .GroupBy(setting => setting.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(setting => setting.LastConnected).First())
            .OrderByDescending(setting => setting.LastConnected)
            .ToList();
    }

    public static bool AddOrUpdate(ConnectionSettings settings, bool markConnected = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Hostname))
            throw new ArgumentException("Hostname cannot be null or empty.", nameof(settings));

        // Editing a saved device must not make it look as if it was connected.
        // Only the successful-login path explicitly marks a new connection.
        var lastConnected = markConnected ? DateTime.UtcNow : settings.LastConnected;
        var fileName = $"{SanitizeHostnameForFilename(settings.Hostname)}.rdp";
        var saved = false;
        foreach (var directory in GetCandidateDirectories())
        {
            var filePath = Path.Combine(directory, fileName);

            // The first writable directory is the canonical location. If a
            // previous installation left copies in other candidate locations,
            // update those copies too so a later startup cannot resurrect an
            // older username or address. Do not create extra copies in every
            // candidate directory.
            if (saved && !File.Exists(filePath))
                continue;

            try
            {
                RdpFileService.SaveToFile(settings, filePath);
                // LastConnected is intentionally kept outside the exported RDP
                // properties. Preserve it through the file timestamp, while a
                // never-connected device gets an old timestamp instead of the
                // save time.
                try
                {
                    File.SetLastWriteTimeUtc(filePath,
                        lastConnected == default ? DateTime.UnixEpoch :
                        lastConnected.Kind == DateTimeKind.Utc ? lastConnected : lastConnected.ToUniversalTime());
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                saved = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (saved)
            settings.LastConnected = lastConnected;
        return saved;
    }

    public static bool Remove(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            throw new ArgumentException("Hostname cannot be null or empty.", nameof(hostname));

        var fileName = $"{SanitizeHostnameForFilename(hostname)}.rdp";
        var removed = false;
        foreach (var directory in GetCandidateDirectories())
        {
            var filePath = Path.Combine(directory, fileName);
            try
            {
                if (!File.Exists(filePath))
                    continue;

                File.Delete(filePath);
                removed = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return removed;
    }

    private static IEnumerable<string> GetCandidateDirectories()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddRoot(roots, Path.Combine(AppContext.BaseDirectory, "data"));
        AddRoot(roots, Path.Combine(Path.GetTempPath(), ProductPaths.CurrentDataFolderName));

        foreach (var root in roots)
            yield return Path.Combine(root, ProductPaths.CurrentDataFolderName, "Recent");
    }

    private static void AddRoot(HashSet<string> roots, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
            roots.Add(root);
    }

    private static string SanitizeHostnameForFilename(string hostname)
    {
        return Path.GetInvalidFileNameChars()
            .Concat([':', '[', ']'])
            .Aggregate(hostname, (current, invalidChar) => current.Replace(invalidChar, '_'));
    }
}
