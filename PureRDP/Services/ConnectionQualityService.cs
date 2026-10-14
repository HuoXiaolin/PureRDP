using PureRDP.Configuration;
using System;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PureRDP.Services;

internal enum ConnectionQualityGrade
{
    Unknown,
    Poor,
    Fair,
    Good,
    Excellent,
}

/// <summary>
/// A small, UI-friendly view of the metrics reported by the native RDP
/// control. Bandwidth is stored in Mbps and latency in milliseconds.
/// </summary>
internal sealed record ConnectionQualitySnapshot(
    bool IsActive,
    ConnectionQualityGrade Grade,
    double? SpeedMbps,
    int? LatencyMs,
    double? PacketLossPercent)
{
    internal static ConnectionQualitySnapshot Measuring { get; } = new(
        true, ConnectionQualityGrade.Unknown, null, null, null);

    internal static ConnectionQualitySnapshot FromRdp(
        uint qualityLevel, int bandwidthKbps, int rttMs, double? packetLossPercent = null)
    {
        double? speed = bandwidthKbps > 0 ? bandwidthKbps / 1000.0 : null;
        int? latency = rttMs > 0 ? rttMs : null;
        return new ConnectionQualitySnapshot(
            true,
            ResolveGrade(qualityLevel, speed, latency, packetLossPercent),
            speed,
            latency,
            packetLossPercent);
    }

    internal ConnectionQualitySnapshot WithPacketLoss(double packetLossPercent)
        => this with
        {
            PacketLossPercent = Math.Clamp(packetLossPercent, 0, 100),
            Grade = ResolveGrade(null, SpeedMbps, LatencyMs,
                Math.Clamp(packetLossPercent, 0, 100), Grade),
        };

    internal ConnectionQualitySnapshot WithLatency(long latencyMs)
    {
        var normalizedLatency = latencyMs > 0
            ? (int)Math.Min(int.MaxValue, latencyMs)
            : LatencyMs;
        return this with
        {
            LatencyMs = normalizedLatency,
            Grade = ResolveGrade(null, SpeedMbps, normalizedLatency,
                PacketLossPercent, Grade),
        };
    }

    private static ConnectionQualityGrade ResolveGrade(
        uint? nativeQualityLevel,
        double? speedMbps,
        int? latencyMs,
        double? packetLossPercent,
        ConnectionQualityGrade? existingGrade = null)
    {
        if (packetLossPercent >= 10 || latencyMs >= 300 || speedMbps is < 0.5)
            return ConnectionQualityGrade.Poor;
        if (packetLossPercent >= 3 || latencyMs >= 150 || speedMbps is < 2)
            return ConnectionQualityGrade.Fair;

        if (nativeQualityLevel is uint nativeLevel)
        {
            return nativeLevel switch
            {
                >= 4 => ConnectionQualityGrade.Excellent,
                3 => ConnectionQualityGrade.Good,
                2 => ConnectionQualityGrade.Fair,
                1 => ConnectionQualityGrade.Poor,
                _ => existingGrade ?? ConnectionQualityGrade.Unknown,
            };
        }

        return existingGrade ?? ConnectionQualityGrade.Unknown;
    }

    internal string ToDisplayText()
    {
        if (!IsActive)
            return string.Empty;

        var grade = Grade switch
        {
            ConnectionQualityGrade.Excellent or ConnectionQualityGrade.Good => "优",
            ConnectionQualityGrade.Fair => "良",
            ConnectionQualityGrade.Poor => "差",
            _ => "检测中",
        };
        var speed = SpeedMbps is double value
            ? $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} Mbps"
            : "-- Mbps";
        var latency = LatencyMs is int milliseconds ? $"{milliseconds} ms" : "-- ms";
        var loss = PacketLossPercent is double percent
            ? $"{percent.ToString("0.#", CultureInfo.InvariantCulture)}%"
            : "--";
        return $"质量 {grade}\n速度 {speed}\n延迟 {latency}\n丢包率 {loss}";
    }

    internal string ToAccessibleText()
        => IsActive
            ? $"连接质量 {ToDisplayText().Replace("\n", "，", StringComparison.Ordinal)}"
            : string.Empty;
}

internal readonly record struct ConnectionProbeResult(bool Success, long LatencyMs);

/// <summary>
/// Measures packet loss without touching the existing RDP session. ICMP is
/// preferred; a TCP handshake fallback prevents hosts that block ping from
/// being reported as lost while their RDP port is reachable.
/// </summary>
internal static class ConnectionQualityProbe
{
    internal static async Task<ConnectionProbeResult?> ProbeAsync(
        ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        if (!TryParseHostAndPort(settings.Hostname, out var host, out var port))
            return null;

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1200).WaitAsync(cancellationToken)
                .ConfigureAwait(true);
            if (reply.Status == IPStatus.Success)
                return new ConnectionProbeResult(true, Math.Max(1, reply.RoundtripTime));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Fall through to the RDP-port probe for ICMP-blocked hosts.
        }

        try
        {
            using var client = new TcpClient();
            var started = Environment.TickCount64;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(true);
            return new ConnectionProbeResult(true, Math.Max(1, Environment.TickCount64 - started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ConnectionProbeResult(false, 0);
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
            return TryPortSuffix(value[(closing + 1)..], ref port);
        }

        var firstColon = value.IndexOf(':');
        if (firstColon >= 0 && firstColon == value.LastIndexOf(':'))
        {
            host = value[..firstColon];
            return host.Length > 0 && TryPortSuffix(value[firstColon..], ref port);
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
}
