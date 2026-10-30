using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PureRDP.Services;

internal static class RdpFileAssociationService
{
    internal const string ProgId = "PureRDP.RdpFile";
    internal const string DefaultAppsUri = "ms-settings:defaultapps?registeredAppUser=PureRDP";
    internal sealed record RegistrationEntry(string KeyPath, string ValueName, string Value);

    internal static IReadOnlyList<RegistrationEntry> BuildRegistrationPlan(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || executable.Contains('"'))
            throw new ArgumentException("请选择有效的 PureRDP 程序路径。");
        var command = $"\"{executable}\" \"%1\"";
        var icon = $"\"{executable}\",0";
        return new[] {
            new RegistrationEntry(@"Software\Classes\PureRDP.RdpFile", "", "PureRDP 远程桌面连接"),
            new RegistrationEntry(@"Software\Classes\PureRDP.RdpFile\DefaultIcon", "", icon),
            new RegistrationEntry(@"Software\Classes\PureRDP.RdpFile\shell\open\command", "", command),
            new RegistrationEntry(@"Software\Classes\Applications\PureRDP.exe", "FriendlyAppName", "PureRDP"),
            new RegistrationEntry(@"Software\Classes\Applications\PureRDP.exe\SupportedTypes", ".rdp", ""),
            new RegistrationEntry(@"Software\Classes\Applications\PureRDP.exe\shell\open\command", "", command),
            new RegistrationEntry(@"Software\Classes\.rdp\OpenWithProgids", ProgId, ""),
            new RegistrationEntry(@"Software\PureRDP\Capabilities", "ApplicationName", "PureRDP"),
            new RegistrationEntry(@"Software\PureRDP\Capabilities", "ApplicationDescription", "使用 PureRDP 打开远程桌面连接文件"),
            new RegistrationEntry(@"Software\PureRDP\Capabilities", "ApplicationIcon", icon),
            new RegistrationEntry(@"Software\PureRDP\Capabilities\FileAssociations", ".rdp", ProgId),
            new RegistrationEntry(@"Software\RegisteredApplications", "PureRDP", @"Software\PureRDP\Capabilities") };
    }

    internal static string ExecutablePath()
    {
        var executable = Environment.ProcessPath;
        if (string.Equals(Path.GetFileName(executable), "PureRDP.exe", StringComparison.OrdinalIgnoreCase))
            return executable!;
        var fallback = Path.Combine(AppContext.BaseDirectory, "PureRDP.exe");
        if (File.Exists(fallback)) return fallback;
        throw new InvalidOperationException("请运行发布目录中的 PureRDP.exe 后再注册打开方式。");
    }

    internal static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\PureRDP.RdpFile\shell\open\command");
        return string.Equals(key?.GetValue("") as string, $"\"{ExecutablePath()}\" \"%1\"", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Register()
    {
        var executable = ExecutablePath();
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到 PureRDP 程序。");
        // Register a candidate for this user. Windows owns UserChoice and the
        // user makes the default selection in Settings; never alter its hash.
        foreach (var entry in BuildRegistrationPlan(executable))
        {
            using var key = Registry.CurrentUser.CreateSubKey(entry.KeyPath);
            key.SetValue(entry.ValueName, entry.Value, RegistryValueKind.String);
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    internal static void OpenDefaultApps()
    {
        using var process = Process.Start(new ProcessStartInfo(DefaultAppsUri) { UseShellExecute = true });
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
