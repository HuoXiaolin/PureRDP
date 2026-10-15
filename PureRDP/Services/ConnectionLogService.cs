using PureRDP.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PureRDP.Services;

internal sealed class ConnectionLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? DisplayName { get; set; }
    public string? Hostname { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive { get; set; }
}

/// <summary>
/// Persists successful RDP session intervals for the local statistics page.
/// An open entry is intentionally retained so an unexpected process exit is
/// visible as an interrupted session rather than silently losing the record.
/// </summary>
internal static class ConnectionLogService
{
    private const int MaximumEntries = 5000;
    private static readonly object Sync = new();
    private static readonly HashSet<string> ActiveIds = new();
    private static readonly object QueueSync = new();
    private static Task _pending = Task.CompletedTask;
    private static List<ConnectionLogEntry>? _cache;

    private static Task<T> Enqueue<T>(Func<T> action)
    {
        lock (QueueSync)
        {
            var task = _pending.ContinueWith(_ => action(), default,
                TaskContinuationOptions.None, TaskScheduler.Default);
            // Keep the queue usable after one unexpected operation failure.
            // The operation task still reports the original exception to its
            // caller, while later log writes are not chained to a faulted task.
            _pending = task.ContinueWith(_ => { }, default,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    public static string Start(ConnectionSettings settings)
    {
        var snapshot = settings.Clone();
        var id = Guid.NewGuid().ToString("N");
        var started = DateTime.UtcNow;
        _ = Enqueue(() => StartCore(snapshot, id, started));
        return id;
    }

    public static void Complete(string? id)
    {
        var ended = DateTime.UtcNow;
        _ = Enqueue(() => { CompleteCore(id, ended); return true; });
    }

    public static void Heartbeat(string? id)
        => _ = Enqueue(() => { HeartbeatCore(id); return true; });

    public static Task<IReadOnlyList<ConnectionLogEntry>> LoadAllAsync() => Enqueue(LoadAllCore);
    public static Task<bool> ClearAsync() => Enqueue(ClearCore);
    public static void WriteDiagnostic(string message)
    {
        var line = $"{DateTime.Now:O} {message}{Environment.NewLine}";
        _ = Enqueue(() =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PureRDP-display.log"), line); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return true;
        });
    }

    public static void Flush()
    {
        Task pending;
        lock (QueueSync) pending = _pending;
        try { pending.GetAwaiter().GetResult(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (Exception) { }
    }
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string StartCore(ConnectionSettings settings, string id, DateTime started)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var entry = new ConnectionLogEntry
        {
            Id = id,
            DisplayName = settings.DisplayName,
            Hostname = settings.Hostname,
            StartedAtUtc = started,
            LastSeenAtUtc = started,
        };

        lock (Sync)
        {
            var entries = LoadUnsafe();
            entries.Add(entry);
            if (entries.Count > MaximumEntries)
                entries = entries.OrderByDescending(item => item.StartedAtUtc).Take(MaximumEntries).ToList();
            SaveUnsafe(entries);
            ActiveIds.Add(entry.Id);
        }

        return entry.Id;
    }

    private static void CompleteCore(string? id, DateTime ended)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        lock (Sync)
        {
            ActiveIds.Remove(id);
            var entries = LoadUnsafe();
            var entry = entries.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.EndedAtUtc.HasValue) return;
            entry.EndedAtUtc = ended;
            SaveUnsafe(entries);
        }
    }

    private static IReadOnlyList<ConnectionLogEntry> LoadAllCore()
    {
        lock (Sync)
            return LoadUnsafe()
                .OrderByDescending(item => item.StartedAtUtc)
                .Select(Clone)
                .ToList();
    }

    private static bool ClearCore()
    {
        lock (Sync)
        {
            if (!SaveUnsafe([])) return false;
            ActiveIds.Clear();
            return true;
        }
    }

    private static void HeartbeatCore(string? id)
    {
        if (id == null) return;
        lock (Sync)
        {
            if (!ActiveIds.Contains(id)) return;
            var entries = LoadUnsafe();
            var entry = entries.FirstOrDefault(item => item.Id == id);
            if (entry == null || entry.EndedAtUtc.HasValue ||
                DateTime.UtcNow - (entry.LastSeenAtUtc ?? entry.StartedAtUtc) < TimeSpan.FromSeconds(30)) return;
            entry.LastSeenAtUtc = DateTime.UtcNow;
            SaveUnsafe(entries);
        }
    }

    private static ConnectionLogEntry Clone(ConnectionLogEntry entry)
        => new()
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            Hostname = entry.Hostname,
            StartedAtUtc = entry.StartedAtUtc,
            EndedAtUtc = entry.EndedAtUtc,
            LastSeenAtUtc = entry.LastSeenAtUtc,
            IsActive = !entry.EndedAtUtc.HasValue && ActiveIds.Contains(entry.Id),
        };

    private static List<ConnectionLogEntry> LoadUnsafe()
    {
        if (_cache != null) return _cache;
        List<ConnectionLogEntry>? newestEntries = null;
        var newestWriteTime = DateTime.MinValue;
        foreach (var path in GetCandidateFilePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var entries = JsonSerializer.Deserialize<List<ConnectionLogEntry>>(
                    File.ReadAllText(path), JsonOptions) ?? [];
                var writeTime = File.GetLastWriteTimeUtc(path);
                if (newestEntries == null || writeTime > newestWriteTime)
                {
                    newestEntries = entries;
                    newestWriteTime = writeTime;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }

        return _cache = newestEntries ?? [];
    }

    private static bool SaveUnsafe(IReadOnlyList<ConnectionLogEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        foreach (var directory in GetCandidateDirectories())
        {
            var filePath = Path.Combine(directory, "connection-log.json");
            var temporaryPath = filePath + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, filePath, true);
                _cache = entries.ToList();
                return true;
            }
            catch (IOException) { TryDelete(temporaryPath); }
            catch (UnauthorizedAccessException) { TryDelete(temporaryPath); }
        }
        return false;
    }

    private static IEnumerable<string> GetCandidateDirectories()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddRoot(roots, Path.Combine(AppContext.BaseDirectory, "data"));
        AddRoot(roots, Path.Combine(Path.GetTempPath(), ProductPaths.CurrentDataFolderName));
        foreach (var root in roots)
            yield return Path.Combine(root, ProductPaths.CurrentDataFolderName);
    }

    private static IEnumerable<string> GetCandidateFilePaths()
    {
        foreach (var directory in GetCandidateDirectories())
            yield return Path.Combine(directory, "connection-log.json");
    }

    private static void AddRoot(HashSet<string> roots, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
