using PureRDP.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PureRDP.Services;

// Ordinary external desktop files try the embedded client first. Only active
// features which cannot be implemented equivalently are routed immediately.
internal static class RdpCompatibilityService
{
    internal static NativeMstscLaunch? Plan(string[] args, bool preferNative)
    {
        if (args.Length == 0) return null;
        if (args[0] == MstscProxyService.ProxyArgument)
        {
            var payload = MstscArgumentParser.GetProxyPayload(args);
            var executable = NativeMstscLauncher.SystemExecutable;
            var start = 0;
            while (start < payload.Length && MstscArgumentParser.IsMstsc(payload[start]))
            {
                if (start == 0) executable = NativeMstscLauncher.ResolveSystemExecutable(payload[start]);
                start++;
            }
            var original = payload.Skip(start).ToArray();
            NativeMstscLaunch Native() => new(executable, original);
            if (preferNative || original.Length == 0) return Native();
            // /edit and /? must retain the native editor/help, not substitute
            // a device editor which cannot preserve every property/signature.
            if (original.Any(value => value.Equals("/edit", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("/multimon", StringComparison.OrdinalIgnoreCase) || value == "/?")) return Native();
            string[] normalized;
            try { normalized = MstscArgumentParser.Normalize(args); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { return Native(); }
            foreach (var value in normalized.Skip(1))
            {
                var path = value.StartsWith("--rdp-file=", StringComparison.Ordinal) ? value[11..] :
                    value.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase) ? value : null;
                if (path != null && NeedsNativeFile(path)) return Native();
            }
            return null;
        }

        // Only shell/file requests, never reinterpret arbitrary PureRDP CLI
        // options or in-memory device settings as native MSTSC arguments.
        if (args.Length != 1) return null;
        var file = args[0].StartsWith("--rdp-file=", StringComparison.Ordinal) ? args[0][11..] :
            args[0].EndsWith(".rdp", StringComparison.OrdinalIgnoreCase) ? args[0] : null;
        if (file == null || RecentConnectionsService.IsSavedConnectionFile(file)) return null;
        return preferNative || NeedsNativeFile(file) ? new(NativeMstscLauncher.SystemExecutable, new[] { file }) : null;
    }

    internal static bool NeedsNativeFile(string path)
    {
        try
        {
            // No raw values in errors/logs; the original file is never edited,
            // copied, decrypted or re-signed for native compatibility.
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return true;
            foreach (var line in RdpFileService.ReadRdpFileLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(':', 3);
                if (parts.Length != 3) continue;
                var name = parts[0].Trim().ToLowerInvariant();
                var value = parts[2].Trim();
                // Empty gateway/RemoteApp fields and common presentation or
                // performance options must not divert working broker sessions.
                if (value.Length == 0) continue;
                if (name is "signature" or "signscope" or "pcb" or "gatewayhostname" or "gatewayaccesstoken" or "kdcproxyname" or "remoteapplicationprogram") return true;
                if (name is "remoteapplicationmode" or "use multimon" or "span monitors" or
                    "enablerdsaadauth" or "targetisaadjoined" or "restricted admin mode" or "public mode")
                    if (int.TryParse(value, out var enabled) && enabled != 0) return true;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
        { return true; }
    }
}

internal sealed class NativeMstscLaunch
{
    internal string Executable { get; }
    internal string[] Arguments { get; }
    internal NativeMstscLaunch(string executable, string[] arguments) { Executable = executable; Arguments = arguments.ToArray(); }
    public override string ToString() => "Native MSTSC compatibility request (arguments omitted)";
}
