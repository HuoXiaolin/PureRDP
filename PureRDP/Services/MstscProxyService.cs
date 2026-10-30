using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace PureRDP.Services;

internal static class MstscProxyService
{
    internal const string ProxyArgument = "--mstsc-proxy";
    internal const string ConfigureArgument = "--configure-mstsc-proxy";
    private const string TargetKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\mstsc.exe";
    private const string BackupKey = @"SOFTWARE\PureRDP\MstscProxy";

    internal static string BuildCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') ||
            !string.Equals(Path.GetFileName(executable), "PureRDP.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("代理需要有效的 PureRDP.exe 完整路径。");
        return $"\"{executable}\" {ProxyArgument}";
    }

    internal static bool IsEnabled()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var target = machine.OpenSubKey(TargetKey);
        using var backup = machine.OpenSubKey(BackupKey);
        return backup?.GetValue("Commands") is string[] commands &&
            commands.Contains(target?.GetValue("Debugger") as string ?? "", StringComparer.Ordinal);
    }

    // Only called by the explicit, elevated configuration helper, never by
    // imports, startup, ordinary settings Save, or automated GUI tests.
    internal static void Configure(bool enable)
    {
        using var gate = new Mutex(false, @"Global\PureRDP.MstscProxy.Configuration");
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("另一个代理设置操作尚未完成，请稍后重试。");
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            ConfigureRegistry(machine, enable, RdpFileAssociationService.ExecutablePath());
        }
        finally { if (acquired) gate.ReleaseMutex(); }
    }

    // Testable with an isolated, disposable root; production only passes HKLM.
    internal static void ConfigureRegistry(RegistryKey machine, bool enable, string executable)
    {
        if (enable)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("未找到代理程序。");
            var command = BuildCommand(executable);
            using var target = machine.CreateSubKey(TargetKey, true);
            using var backup = machine.CreateSubKey(BackupKey, true);
            var raw = target.GetValue("Debugger", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw != null && raw is not string) throw new InvalidOperationException("MSTSC 启动设置格式异常，未修改。");
            var current = raw as string;
            var owned = backup.GetValue("Command") as string;
            var commands = backup.GetValue("Commands") as string[] ?? Array.Empty<string>();
            if (!string.IsNullOrEmpty(current) && !commands.Contains(current, StringComparer.Ordinal))
                throw new InvalidOperationException("mstsc.exe 已由其他工具代理，请先在原工具中恢复；未覆盖其配置。");
            if (owned == null)
            {
                backup.SetValue("HadDebugger", target.GetValueNames().Contains("Debugger", StringComparer.OrdinalIgnoreCase) ? 1 : 0);
                backup.SetValue("PreviousDebugger", current ?? "");
                backup.SetValue("PreviousKind", raw == null ? (int)RegistryValueKind.String : (int)target.GetValueKind("Debugger"));
            }
            // Persist recovery metadata before the one launch setting changes.
            backup.SetValue("Command", command);
            backup.SetValue("Commands", commands.Append(command).Distinct(StringComparer.Ordinal).ToArray(), RegistryValueKind.MultiString);
            backup.Flush();
            target.SetValue("Debugger", command, RegistryValueKind.String);
            target.Flush();
        }
        else
        {
            using var backup = machine.OpenSubKey(BackupKey, true);
            if (backup?.GetValue("Command") is not string owned) return;
            using var target = machine.OpenSubKey(TargetKey, true);
            if (target != null)
            {
                var current = target.GetValue("Debugger", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var commands = backup.GetValue("Commands") as string[] ?? new[] { owned };
                var previous = backup.GetValue("PreviousDebugger") as string ?? "";
                var hadPrevious = Equals(backup.GetValue("HadDebugger"), 1);
                if (!commands.Contains(current ?? "", StringComparer.Ordinal) &&
                    !string.Equals(current ?? "", previous, StringComparison.Ordinal))
                    throw new InvalidOperationException("启动设置已被其他工具修改，未覆盖；请先核对当前代理。");
                if (hadPrevious)
                    target.SetValue("Debugger", previous, (RegistryValueKind)(backup.GetValue("PreviousKind") as int? ?? (int)RegistryValueKind.String));
                else target.DeleteValue("Debugger", false);
                target.Flush();
            }
            foreach (var name in new[] { "Command", "Commands", "HadDebugger", "PreviousDebugger", "PreviousKind" }) backup.DeleteValue(name, false);
        }
    }

    internal static async Task RequestConfiguration(bool enable)
    {
        var start = new ProcessStartInfo(RdpFileAssociationService.ExecutablePath())
        { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(ConfigureArgument);
        start.ArgumentList.Add(enable ? "enable" : "disable");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("未启动配置程序。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("代理设置未完成，请查看 Windows 授权或错误提示。");
    }

    internal static string[] NormalizeArguments(string[] args) => MstscArgumentParser.Normalize(args);
}
