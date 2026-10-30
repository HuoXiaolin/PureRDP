using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PureRDP.Services;

internal static class NativeMstscLauncher
{
    internal const string GuardVariable = "PURERDP_NATIVE_HANDOFF_DEPTH";
    internal static string SystemExecutable => Path.Combine(Environment.SystemDirectory, "mstsc.exe");

    internal static string ResolveSystemExecutable(string requested)
    {
        if (!Path.IsPathFullyQualified(requested)) return SystemExecutable;
        var full = Path.GetFullPath(requested);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var directory in new[] { "System32", "SysWOW64", "Sysnative" })
        {
            var allowed = Path.Combine(windows, directory, "mstsc.exe");
            if (full.Equals(allowed, StringComparison.OrdinalIgnoreCase) && File.Exists(allowed)) return allowed;
        }
        // Never turn an IFEO request into a generic executable launcher.
        return SystemExecutable;
    }

    internal static int Run(NativeMstscLaunch launch)
    {
        var executable = ResolveSystemExecutable(launch.Executable);
        return RunDetachedProcess(executable, launch.Arguments);
    }

    // Internal seam for an isolated synthetic child, not exposed as CLI.
    // Create as the debugger, immediately detach before resuming the primary
    // thread. This avoids IFEO recursion without touching global registry
    // state, renaming mstsc.exe, reading memory or injecting any code/input.
    internal static int RunDetachedProcess(string executable, string[] arguments, Action<uint>? started = null)
    {
        var command = new StringBuilder(BuildCommandLine(executable, arguments));
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);
        environment[GuardVariable] = "1";
        var block = string.Join('\0', environment.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Key + "=" + entry.Value)) + "\0\0";
        var environmentPointer = Marshal.StringToHGlobalUni(block);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        ProcessInfo child = default;
        var resumed = false;
        var attached = false;
        try
        {
            // DEBUG_ONLY_THIS_PROCESS | CREATE_SUSPENDED |
            // CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW (console helpers).
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000406,
                environmentPointer, null, ref startup, out child)) throw LaunchError();
            attached = true;
            if (!DebugSetProcessKillOnExit(false)) throw LaunchError();
            if (!DebugActiveProcessStop(child.ProcessId)) throw LaunchError();
            attached = false;
            if (ResumeThread(child.Thread) == uint.MaxValue) throw LaunchError();
            resumed = true;
            started?.Invoke(child.ProcessId);
            // Keep the proxy helper alive until the native session exits: some
            // assistants remove their temporary file as soon as the child ends.
            if (WaitForSingleObject(child.Process, uint.MaxValue) != 0) throw LaunchError();
            if (!GetExitCodeProcess(child.Process, out var code)) throw LaunchError();
            return unchecked((int)code);
        }
        finally
        {
            // Failure cleanup is limited to this newly created, not-yet-run
            // child. Never terminate an existing native or PureRDP session.
            if (child.Process != IntPtr.Zero && !resumed) TerminateProcess(child.Process, 1);
            if (attached) DebugActiveProcessStop(child.ProcessId);
            if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
            if (child.Process != IntPtr.Zero) CloseHandle(child.Process);
            Marshal.FreeHGlobal(environmentPointer);
        }
    }

    private static Win32Exception LaunchError() => new(Marshal.GetLastWin32Error(),
        "原生 MSTSC 兼容启动未完成。请检查系统策略或关闭启动代理后重试；本次没有修改全局代理设置。");

    internal static string BuildCommandLine(string executable, string[] arguments)
    {
        static string Quote(string value)
        {
            if (value.Contains('\0')) throw new ArgumentException("启动参数包含无效字符。");
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));
        if (command.Length > 32766) throw new ArgumentException("启动参数超过 Windows 长度限制。");
        return command;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedSize;
        internal IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugActiveProcessStop(uint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
}
