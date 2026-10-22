using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

// Test infrastructure only: no desktop switching, user input, or existing-process control.
public static class PureRdpTestDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb; public string reserved, desktop, title;
        public uint x, y, width, height, charsX, charsY, fill, flags;
        public ushort show, reservedSize;
        public IntPtr reservedData, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
    [StructLayout(LayoutKind.Sequential)] struct Security { public int size; public IntPtr descriptor; [MarshalAs(UnmanagedType.Bool)] public bool inherit; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit
    {
        public long processTime, jobTime; public uint flags; public UIntPtr minWorkingSet, maxWorkingSet;
        public uint processCount; public UIntPtr affinity; public uint priority, scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong readCount, writeCount, otherCount, readBytes, writeBytes, otherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit
    {
        public BasicLimit basic; public IoCounters io;
        public UIntPtr processMemory, jobMemory, peakProcess, peakJob;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformationW(IntPtr obj, int index, StringBuilder text, uint length, out uint needed);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo start, out ProcessInfo process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileW(string path, uint access, uint sharing, ref Security security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObjectW(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static void Fail(string message) { throw new Win32Exception(Marshal.GetLastWin32Error(), message); }

    public static string CurrentDesktop()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _)) Fail("Cannot verify test desktop");
        return name.ToString();
    }
    static string CurrentWindowStation()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetProcessWindowStation(), 2, name, 512, out _)) Fail("Cannot verify test window station");
        return name.ToString();
    }
    static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }
    public static int Run(string executable, string[] arguments, string directory)
    {
        var desktop = IntPtr.Zero; var job = IntPtr.Zero; var input = IntPtr.Zero;
        var process = new ProcessInfo();
        var assigned = false;
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        try
        {
            var name = "PureRDPTests_" + Guid.NewGuid().ToString("N");
            // Deliberately omit DESKTOP_SWITCHDESKTOP permission.
            desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0xff, IntPtr.Zero);
            if (desktop == IntPtr.Zero) Fail("Cannot create isolated desktop; refusing visible GUI tests");
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) Fail("Cannot create test process job");
            var limits = new ExtendedLimit { basic = new BasicLimit { flags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())) Fail("Cannot configure test cleanup");
            var security = new Security { size = Marshal.SizeOf<Security>(), inherit = true };
            input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
            if (input == new IntPtr(-1)) Fail("Cannot open test stdin");
            var start = new StartupInfo {
                cb = Marshal.SizeOf<StartupInfo>(), desktop = CurrentWindowStation() + "\\" + name, flags = 0x100,
                input = input, output = pipe.ClientSafePipeHandle.DangerousGetHandle(), error = pipe.ClientSafePipeHandle.DangerousGetHandle()
            };
            var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000004, IntPtr.Zero, directory, ref start, out process)) Fail("Cannot start isolated tests");
            if (!AssignProcessToJobObject(job, process.process)) Fail("Cannot isolate test process lifetime");
            assigned = true;
            pipe.DisposeLocalCopyOfClientHandle();
            if (ResumeThread(process.thread) == uint.MaxValue) Fail("Cannot resume tests");
            Console.WriteLine("GUI tests use isolated desktop " + name + "; active desktop is not switched.");
            var output = Task.Run(async () => {
                using var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, true);
                string line;
                while ((line = await reader.ReadLineAsync()) != null) Console.WriteLine(line);
            });
            if (WaitForSingleObject(process.process, 900000) != 0) throw new TimeoutException("Isolated tests exceeded 15 minutes");
            if (!GetExitCodeProcess(process.process, out var exit)) Fail("Cannot read test exit code");
            // Closing the job also closes inherited pipe handles held by compiler servers.
            CloseHandle(job); job = IntPtr.Zero;
            if (!output.Wait(10000)) throw new TimeoutException("Test output did not close");
            return unchecked((int)exit);
        }
        finally
        {
            if (!assigned && process.process != IntPtr.Zero) TerminateProcess(process.process, 1);
            if (job != IntPtr.Zero) CloseHandle(job);
            if (process.thread != IntPtr.Zero) CloseHandle(process.thread);
            if (process.process != IntPtr.Zero) CloseHandle(process.process);
            if (input != IntPtr.Zero && input != new IntPtr(-1)) CloseHandle(input);
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        }
    }
}
