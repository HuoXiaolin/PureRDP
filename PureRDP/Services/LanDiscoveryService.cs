using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace PureRDP.Services;

internal readonly record struct Ipv4ScanRange(uint First, uint Last)
{
    internal int Count => checked((int)((ulong)Last - First + 1));

    internal static IPAddress GetAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    public override string ToString()
        => First == Last ? GetAddress(First).ToString() : $"{GetAddress(First)}–{GetAddress(Last)}";
}

internal sealed record LanScanBatch(int CheckedCount, IPAddress[] ReachableAddresses);

internal sealed record LanScanNetwork(string AdapterId, string AdapterName, IPAddress LocalAddress, int PrefixLength)
{
    // A /20 fits the per-network discovery limit. Limit broader networks to the
    // local /24 so a single quick scan does not walk a large enterprise block.
    internal Ipv4ScanRange ScanRange => LanDiscoveryService.GetSubnetRange(LocalAddress, PrefixLength < 20 ? 24 : PrefixLength);

    public override string ToString()
        => $"{AdapterName}  ·  {LocalAddress}/{PrefixLength}" + (PrefixLength < 20 ? "（快扫本地 /24）" : string.Empty);
}

internal readonly record struct LanScanScope(Ipv4ScanRange Range, IPAddress? LocalAddress = null);

internal static class LanDiscoveryService
{
    internal const int DefaultPort = 3389;
    internal const int MaxAddresses = 4096;
    private const int BatchSize = 64;

    internal static LanScanNetwork[] GetAdapterNetworks()
        => GetAdapterIpv4Addresses()
            .Where(item => item.Adapter.OperationalStatus == OperationalStatus.Up &&
                item.Adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) &&
                !IPAddress.IsLoopback(item.Local.Address) && item.Local.PrefixLength is >= 1 and <= 32)
            .Select(item => new LanScanNetwork(item.Adapter.Id, item.Adapter.Name, item.Local.Address, item.Local.PrefixLength))
            .OrderBy(network => network.AdapterName, StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal static Func<IPAddress, bool> CreateLocalAddressFilter()
    {
        // Even a virtual or tunnel adapter that is not offered as a quick scan
        // scope can have a local address in a manually entered range.
        var localAddresses = GetAdapterIpv4Addresses().Select(item => item.Local.Address).ToHashSet();
        return address => IPAddress.IsLoopback(address) || localAddresses.Contains(address);
    }

    private static IEnumerable<(NetworkInterface Adapter, UnicastIPAddressInformation Local)> GetAdapterIpv4Addresses()
    {
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { adapters = []; }

        foreach (var adapter in adapters)
        {
            // Hyper-V's internal switch can have an address without a gateway.
            UnicastIPAddressInformationCollection addresses;
            try { addresses = adapter.GetIPProperties().UnicastAddresses; }
            catch (NetworkInformationException) { continue; }
            foreach (var local in addresses)
                if (local.Address.AddressFamily == AddressFamily.InterNetwork && !local.Address.Equals(IPAddress.Any))
                    yield return (adapter, local);
        }
    }

    internal static Ipv4ScanRange GetSubnetRange(IPAddress localAddress, int prefix)
        => Subnet(ToNumber(localAddress), prefix);

    internal static bool TryParseRange(string input, out Ipv4ScanRange range, out string error)
    {
        range = default;
        error = "请输入 IPv4 地址、IP 段或 CIDR，例如 192.168.1.0/24。";
        var text = input.Trim().Replace('–', '-');
        var separator = text.IndexOfAny(['/', '-']);
        var start = separator < 0 ? text : text[..separator].Trim();
        if (!TryParseAddress(start, out var first)) return false;

        var last = first;
        if (separator >= 0)
        {
            var suffix = text[(separator + 1)..].Trim();
            if (text[separator] == '/')
            {
                if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
                    prefix is < 0 or > 32)
                    return false;
                (first, last) = Subnet(first, prefix);
            }
            else if (byte.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var endOctet))
                last = (first & 0xffffff00u) | endOctet;
            else if (!TryParseAddress(suffix, out last))
                return false;
        }

        if (last < first)
        {
            error = "结束 IP 地址必须大于或等于起始地址。";
            return false;
        }
        if ((ulong)last - first + 1 > MaxAddresses)
        {
            error = $"一次最多扫描 {MaxAddresses} 个地址，请缩小 IP 段。";
            return false;
        }
        if (first < 0x01000000u || last >= 0xe0000000u)
        {
            error = "请输入可用于设备连接的 IPv4 单播地址。";
            return false;
        }

        range = new Ipv4ScanRange(first, last);
        error = string.Empty;
        return true;
    }

    internal static async IAsyncEnumerable<LanScanBatch> ScanAsync(LanScanScope scope, int port, Func<IPAddress, bool> isLocalAddress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var range = scope.Range;
        for (var offset = 0; offset < range.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(BatchSize, range.Count - offset);
            var addresses = Enumerable.Range(offset, count)
                .Select(index => Ipv4ScanRange.GetAddress(range.First + (uint)index))
                .Where(address => !isLocalAddress(address)).ToArray();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(650));
            var reachable = await Task.WhenAll(addresses.Select(address =>
                DeviceAvailabilityScanner.TryConnectAsync(address, port, timeout.Token, cancellationToken, scope.LocalAddress)))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LanScanBatch(offset + count, addresses.Where((_, index) => reachable[index]).ToArray());
        }
    }

    internal static string FormatEndpoint(IPAddress address, int port)
        => port == DefaultPort ? address.ToString() : $"{address}:{port.ToString(CultureInfo.InvariantCulture)}";

    internal static string NormalizeEndpoint(string? endpoint)
    {
        if (DeviceAvailabilityScanner.TryParseHostAndPort(endpoint, out var host, out var port) &&
            IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            return FormatEndpoint(address, port);
        return endpoint?.Trim() ?? string.Empty;
    }

    private static uint ToNumber(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static bool TryParseAddress(string text, out uint address)
    {
        address = 0;
        if (!IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork ||
            !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
            return false;
        address = ToNumber(parsed);
        return true;
    }

    private static Ipv4ScanRange Subnet(uint address, int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var first = address & mask;
        var last = first | ~mask;
        return prefix >= 31 ? new(first, last) : new(first + 1, last - 1);
    }
}
