using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PureRDP.Services;

// Parse launch formats, not vendor names. Never execute the input as a shell
// command or silently remove unsupported authentication/security options.
internal static class MstscArgumentParser
{
    internal static string[] Normalize(string[] args)
    {
        if (args.Length == 0 || args[0] != MstscProxyService.ProxyArgument) return args;
        var payload = GetProxyPayload(args).Select(Unquote).ToArray();
        var start = 0;
        while (start < payload.Length && IsMstsc(payload[start])) start++;
        var result = new List<string> { "--proxy-connection" };
        var fileSeen = false;
        for (var index = start; index < payload.Length; index++)
        {
            var argument = payload[index];
            if (TryConnectionFile(argument, out var file))
            {
                if (fileSeen) throw new ArgumentException("启动请求包含多个连接文件，请一次打开一个连接。");
                fileSeen = true;
                result.Add(Path.GetExtension(file).Equals(".rdp", StringComparison.OrdinalIgnoreCase) ? file : "--rdp-file=" + file);
                continue;
            }
            if (!argument.StartsWith('/') && !argument.StartsWith('-'))
                throw new ArgumentException($"第 {index + 1} 个启动参数不是可识别的连接文件或 MSTSC 选项。临时文件需存在且包含有效的 RDP 配置。");
            var option = argument.TrimStart('/', '-');
            var separator = option.IndexOfAny(new[] { ':', '=' });
            var name = (separator < 0 ? option : option[..separator]).ToLowerInvariant();
            var value = separator < 0 ? null : Unquote(option[(separator + 1)..]);
            if (name is "v" or "w" or "h")
            {
                if (value == null && index + 1 < payload.Length) value = payload[++index];
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"MSTSC /{name} 缺少参数值。");
                if (name is "w" or "h" && (!int.TryParse(value, out var dimension) || dimension <= 0))
                    throw new ArgumentException($"MSTSC /{name} 需要大于 0 的像素值。");
                result.Add((name == "v" ? "--hostname=" : name == "w" ? "--width=" : "--height=") + value);
                continue;
            }
            if (value != null) throw new ArgumentException($"当前代理不支持 /{SafeName(name)} 的此种参数形式。可关闭代理后使用原生远程桌面。");
            switch (name)
            {
                case "f": case "fullscreen": result.Add("--fullscreen"); break;
                case "admin": result.Add("--administrative-session"); break;
                case "prompt": result.Add("--prompt-for-credentials"); break;
                case "edit": result.Add("--edit"); break;
                case "multimon": result.Add("--fullscreen"); result.Add("--multimon"); break;
                case "?": result.Add("--help"); break;
                default: throw new ArgumentException($"当前代理不支持 MSTSC /{SafeName(name)}，未忽略该选项。可关闭代理后使用原生远程桌面。");
            }
        }
        return result.ToArray();
    }

    internal static string[] GetProxyPayload(string[] args)
    {
        var rawPayload = args.Skip(1).ToArray();
        var payload = rawPayload;
        if (payload.Length == 1 && payload[0].Contains(' '))
        {
            var expanded = SplitWindowsCommand(rawPayload[0]);
            if (expanded.Length <= 1) expanded = SplitWindowsCommand(Unquote(payload[0]));
            if (expanded.Length > 1 && IsMstsc(expanded[0])) payload = expanded;
        }
        return payload;
    }

    private static bool TryConnectionFile(string argument, out string path)
    {
        path = Environment.ExpandEnvironmentVariables(argument);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        if (path.StartsWith('/') || path.StartsWith('-')) return false;
        if (Path.GetExtension(path).Equals(".rdp", StringComparison.OrdinalIgnoreCase))
        { path = Path.GetFullPath(path); return true; }
        // Some assistants use .tmp or extensionless files. Validate their
        // contents rather than assuming every path is a connection profile.
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return false;
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            var profile = RdpFileService.ReadFromFile(path);
            if (string.IsNullOrWhiteSpace(profile.Hostname)) return false;
            path = Path.GetFullPath(path);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentException or OverflowException)
        { return false; }
    }

    internal static bool IsMstsc(string value)
        => Path.GetFileName(value).Equals("mstsc.exe", StringComparison.OrdinalIgnoreCase) ||
           Path.GetFileName(value).Equals("mstsc", StringComparison.OrdinalIgnoreCase);
    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    private static string SafeName(string name)
        => new string(name.Take(40).Where(character => char.IsAsciiLetterOrDigit(character) || character == '?').ToArray());

    private static string[] SplitWindowsCommand(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) return Array.Empty<string>();
        try { return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
