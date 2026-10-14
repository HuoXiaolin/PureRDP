using Microsoft.Win32;
using PureRDP.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;

namespace PureRDP.Services;

/// <summary>Read only the current user's mstsc computer dropdown; never credentials.</summary>
internal static class WindowsRdpHistoryService
{
    internal const string HistoryKey = @"Software\Microsoft\Terminal Server Client\Default";

    internal static string[] LoadAddresses()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HistoryKey, writable: false);
            if (key == null) return [];
            return ParseAddresses(key.GetValueNames().Select(name =>
                new KeyValuePair<string, object?>(name, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        { return []; }
    }

    internal static string[] ParseAddresses(IEnumerable<KeyValuePair<string, object?>> values)
    {
        return values.Select(pair => (Index: MruIndex(pair.Key), Address: (pair.Value as string)?.Trim()))
            .Where(item => item.Index >= 0 && item.Address.IsValidHostname())
            .OrderBy(item => item.Index)
            .Select(item => item.Address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int MruIndex(string name) => name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : -1;

    internal static string[] AdditionalAddresses(IEnumerable<string?> existing, IEnumerable<string> native)
    {
        var known = new HashSet<string>(existing.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()), StringComparer.OrdinalIgnoreCase);
        return native.Where(value => known.Add(value)).ToArray();
    }

    internal static string? LoadUsernameHint(string endpoint)
    {
        if (!endpoint.IsValidHostname()) return null;
        try
        {
            using var servers = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Terminal Server Client\Servers", writable: false);
            if (servers == null) return null;
            foreach (var target in UsernameHintTargets(endpoint))
            {
                using var server = servers.OpenSubKey(target, writable: false);
                if (server?.GetValue("UsernameHint", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string username && !string.IsNullOrWhiteSpace(username))
                    return username.Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { }
        return null;
    }

    internal static string[] UsernameHintTargets(string endpoint)
    {
        var value = endpoint.Trim();
        var host = value;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end > 0) host = value[..(end + 1)];
        }
        else if (value.Count(c => c == ':') == 1) host = value[..value.IndexOf(':')];
        return new[] { value, host, host.Trim('[', ']') }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
