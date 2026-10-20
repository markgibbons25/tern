// measure <result-file> <command> [args...]
// Runs the command inside a Job Object (so child processes count too) and appends one line to
// <result-file>: wall_ms peak_working_set_bytes peak_process_commit_bytes cpu_ms exit_code
// The command's own output goes to this process's stdout/stderr.
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: measure <result-file> <command> [args...]");
    return 2;
}

var job = Native.CreateJobObject(IntPtr.Zero, null);
if (job == IntPtr.Zero || !Native.AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
{
    Console.Error.WriteLine($"measure: could not set up a job object ({Marshal.GetLastWin32Error()})");
    return 2;
}
Native.GetProcessTimes(Process.GetCurrentProcess().Handle, out _, out _, out var selfKernel0, out var selfUser0);

var psi = new ProcessStartInfo(args[1]) { UseShellExecute = false };
foreach (var a in args.Skip(2))
{
    psi.ArgumentList.Add(a);
}
var watch = Stopwatch.StartNew();
using var child = Process.Start(psi)!;
// `dotnet sitecore` does its work in a child of dotnet.exe, so every process in the job is tracked:
// a handle kept open still reports a process's peak working set after it exits.
var self = Environment.ProcessId;
var handles = new Dictionary<uint, IntPtr>();
var ids = new uint[2 + 256];
while (!child.WaitForExit(10))
{
    Track();
}
Track();
watch.Stop();

ulong peakWs = 0;
foreach (var h in handles.Values.Append(child.Handle))
{
    var c = new Native.ProcessMemoryCounters { cb = (uint)Marshal.SizeOf<Native.ProcessMemoryCounters>() };
    if (Native.GetProcessMemoryInfo(h, out c, c.cb))
    {
        peakWs = Math.Max(peakWs, (ulong)c.PeakWorkingSetSize);
    }
}
var counters = new Native.ProcessMemoryCounters { PeakWorkingSetSize = (nuint)peakWs };

void Track()
{
    ids[0] = 256;
    if (!Native.QueryInformationJobObject(job, 3, ids, (uint)(ids.Length * 4), out _))
    {
        return;
    }
    // JOBOBJECT_BASIC_PROCESS_ID_LIST: two counts, then ULONG_PTR ids (8 bytes each on x64).
    var count = (int)ids[1];
    for (var i = 0; i < count; i++)
    {
        var pid = ids[2 + i * 2];
        if (pid != self && !handles.ContainsKey(pid))
        {
            var h = Native.OpenProcess(0x1000 | 0x0400 | 0x0010, false, pid);
            if (h != IntPtr.Zero)
            {
                handles[pid] = h;
            }
        }
    }
}

var accounting = new Native.BasicAccounting();
Native.QueryInformationJobObject(job, 1, ref accounting, (uint)Marshal.SizeOf<Native.BasicAccounting>(), out _);
var limits = new Native.ExtendedLimits();
Native.QueryInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>(), out _);
Native.GetProcessTimes(Process.GetCurrentProcess().Handle, out _, out _, out var selfKernel1, out var selfUser1);

// Job times are in 100 ns ticks and include this process; take its own share out.
var cpuTicks = accounting.TotalUserTime + accounting.TotalKernelTime - (selfKernel1 - selfKernel0) - (selfUser1 - selfUser0);
File.AppendAllText(args[0], $"{watch.ElapsedMilliseconds} {(long)counters.PeakWorkingSetSize} {(long)limits.PeakProcessMemoryUsed} {cpuTicks / 10_000} {child.ExitCode}\n");
return child.ExitCode;

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref BasicAccounting info, uint length, out uint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length, out uint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, uint[] info, uint length, out uint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ExtendedLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
