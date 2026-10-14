using PureRDP.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PureRDP.Services;

internal enum DeviceAvailabilityState
{
    Unknown,
    Online,
    Offline,
}

internal sealed class DeviceAvailabilityChangedEventArgs : EventArgs
{
    public DeviceAvailabilityChangedEventArgs(string hostname, DeviceAvailabilityState state)
    {
        Hostname = hostname;
        State = state;
    }

    public string Hostname { get; }
    public DeviceAvailabilityState State { get; }
}

/// <summary>
/// Performs lightweight TCP checks against each saved RDP endpoint without
/// touching the RDP session or logging in.  Checks are concurrent per cycle,
/// but cycles never overlap.
/// </summary>
internal sealed class DeviceAvailabilityScanner : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, ConnectionSettings> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceAvailabilityState> _states = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _stop = new();
    private Task? _worker;
    private int _intervalSeconds = 3600;
    private TaskCompletionSource<bool> _intervalChanged = CreateIntervalSignal();
    private bool _disposed;

    private static TaskCompletionSource<bool> CreateIntervalSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler<DeviceAvailabilityChangedEventArgs>? StatusChanged;

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _worker is { IsCompleted: false }) return;
            _worker = RunAsync(_stop.Token);
        }
    }

    public void SetInterval(int seconds)
    {
        lock (_sync)
        {
            _intervalSeconds = Math.Clamp(seconds <= 0 ? 3600 : seconds, 5, 86400);
            _intervalChanged.TrySetResult(true);
            _intervalChanged = CreateIntervalSignal();
        }
    }

    public void SetDevices(IEnumerable<ConnectionSettings> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        lock (_sync)
        {
            _devices.Clear();
            foreach (var device in devices)
            {
                var hostname = device.Hostname?.Trim();
                if (string.IsNullOrWhiteSpace(hostname)) continue;
                var normalized = device.Clone();
                normalized.Hostname = hostname;
                _devices[hostname] = normalized;
                if (!_states.ContainsKey(hostname))
                    _states[hostname] = DeviceAvailabilityState.Unknown;
            }

            foreach (var key in _states.Keys.Where(key => !_devices.ContainsKey(key)).ToArray())
                _states.Remove(key);
        }

        // Refresh immediately after the console loads or changes its device
        // list; the configured interval controls subsequent periodic scans.
        ScanNow();
    }

    public void ScanNow()
        => _ = ScanNowAsync();

    internal Task ScanNowAsync()
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            return ScanAllAsync(_stop.Token);
        }
    }

    /// <summary>
    /// An established RDP session is stronger evidence than a periodic probe.
    /// Publish it immediately so a successful connection cannot leave the card
    /// showing the previous failed scan result.
    /// </summary>
    public void MarkOnline(string? endpoint)
    {
        var hostname = endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(hostname)) return;

        var changed = false;
        lock (_sync)
        {
            if (_disposed || !_devices.ContainsKey(hostname)) return;
            if (!_states.TryGetValue(hostname, out var previous) || previous != DeviceAvailabilityState.Online)
            {
                _states[hostname] = DeviceAvailabilityState.Online;
                changed = true;
            }
        }

        if (changed)
        {
            try { StatusChanged?.Invoke(this, new DeviceAvailabilityChangedEventArgs(hostname, DeviceAvailabilityState.Online)); }
            catch (Exception) { }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ScanAllAsync(cancellationToken).ConfigureAwait(false);
            int interval;
            Task intervalChanged;
            lock (_sync)
            {
                interval = _intervalSeconds;
                intervalChanged = _intervalChanged.Task;
            }

            var delay = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            await Task.WhenAny(delay, intervalChanged).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
        }
    }

    private async Task ScanAllAsync(CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            // Do not drop a scan requested while the worker's initial scan is
            // still running.  At startup the worker can briefly scan the
            // empty device list before the connection library has loaded;
            // abandoning the queued request leaves the cards Unknown until
            // the next settings save or periodic interval.
            await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ConnectionSettings[] devices;
            lock (_sync) devices = _devices.Values.Select(device => device.Clone()).ToArray();
            await Task.WhenAll(devices.Select(device => ScanDeviceAsync(device, cancellationToken))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (entered) _scanGate.Release();
        }
    }

    private async Task ScanDeviceAsync(ConnectionSettings device, CancellationToken cancellationToken)
    {
        var hostname = device.Hostname?.Trim() ?? string.Empty;
        var state = await IsReachableAsync(hostname, cancellationToken).ConfigureAwait(false)
            ? DeviceAvailabilityState.Online
            : DeviceAvailabilityState.Offline;
        var changed = false;
        lock (_sync)
        {
            if (_disposed || !_devices.ContainsKey(hostname)) return;
            if (!_states.TryGetValue(hostname, out var previous) || previous != state)
            {
                _states[hostname] = state;
                changed = true;
            }
        }

        if (changed)
        {
            try { StatusChanged?.Invoke(this, new DeviceAvailabilityChangedEventArgs(hostname, state)); }
            catch (Exception) { }
        }
    }

    private static async Task<bool> IsReachableAsync(string? endpoint, CancellationToken cancellationToken)
    {
        if (!TryParseHostAndPort(endpoint, out var host, out var port)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            // A hostname can resolve to an unreachable IPv6 address followed by
            // a reachable IPv4 address (or vice versa). Probe every resolved
            // address concurrently within one overall timeout, matching the
            // fallback behavior users get from the native RDP client.
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            if (addresses.Length == 0) return false;

            var attempts = addresses.Select(address => TryConnectAsync(
                address, port, timeout.Token, cancellationToken)).ToArray();
            var results = await Task.WhenAll(attempts).ConfigureAwait(false);
            return results.Any(result => result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> TryConnectAsync(
        IPAddress address,
        int port,
        CancellationToken timeoutToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, timeoutToken).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryParseHostAndPort(string? endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 3389;
        var value = endpoint?.Trim() ?? string.Empty;
        if (value.Length == 0) return false;

        if (value[0] == '[')
        {
            var closing = value.IndexOf(']');
            if (closing <= 1) return false;
            host = value[1..closing];
            var suffix = value[(closing + 1)..];
            return TryPortSuffix(suffix, ref port);
        }

        var firstColon = value.IndexOf(':');
        if (firstColon >= 0 && firstColon == value.LastIndexOf(':'))
        {
            host = value[..firstColon];
            return TryPortSuffix(value[firstColon..], ref port) && host.Length > 0;
        }

        host = value;
        return true;
    }

    private static bool TryPortSuffix(string suffix, ref int port)
    {
        if (suffix.Length == 0) return true;
        if (suffix[0] != ':' || !int.TryParse(suffix[1..], out var parsed) || parsed is < 1 or > 65535)
            return false;
        port = parsed;
        return true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _stop.Dispose();
        }
        // Do not dispose the gate while an in-flight scan may still be
        // reaching its finally block on a thread-pool thread.
    }
}
