using System.Runtime.InteropServices;

namespace Yaparr.Screen;

/// <summary>
/// The processor time the calling thread has used, in <see cref="TimeSpan"/> ticks: what a decoder's own figure is measured with, so that time spent waiting
/// (for the graphics card, or for the processor while other programs have it) is not counted as work. -1 where it cannot be read.
/// </summary>
internal static partial class ThreadCpuTime
{
    private const int ClockThreadCpuTimeId = 3;   // CLOCK_THREAD_CPUTIME_ID on Linux

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public nint Seconds; public nint Nanoseconds; }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int ClockGetTime(int clock, out Timespec time);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    private static bool _unavailable;

    public static long Now()
    {
        if (_unavailable) return -1;
        try
        {
            if (OperatingSystem.IsWindows())
                return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? kernel + user : -1;   // both in 100 ns units, as TimeSpan ticks
            if (OperatingSystem.IsLinux())
                return ClockGetTime(ClockThreadCpuTimeId, out var t) == 0 ? (long)t.Seconds * TimeSpan.TicksPerSecond + (long)t.Nanoseconds / 100 : -1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        _unavailable = true;
        return -1;
    }
}
