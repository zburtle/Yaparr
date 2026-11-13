using System.Runtime.InteropServices;

namespace Yaparr.Screen;

/// <summary>
/// A share's clock is a timer: sixty waits of 16.7 ms a second. Windows 11 treats a program whose window is minimized, covered or on another desktop as a
/// background program: it can run it at the efficiency level (EcoQoS) and ignore its request for a fine timer (<c>timeBeginPeriod</c>), so the waits
/// stretch to 15 ms or more and a share that sends 60 pictures a second sends 46. A share that is running should not be treated like that, so while one is
/// started this program asks Windows not to (<c>SetProcessInformation</c> with <c>ProcessPowerThrottling</c>: neither throttle the execution speed nor
/// ignore the timer resolution). Only for the program as a whole and only from the first share on; there is no need to undo it.
/// </summary>
public static class ProcessPower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    private const int ProcessPowerThrottling = 4;
    private const uint CurrentVersion = 1, ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int information, ref PowerThrottlingState state, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessInformation(IntPtr process, int information, ref PowerThrottlingState state, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    private static readonly object Gate = new();
    private static string? _result;

    /// <summary>Asks Windows not to throttle this program in the background (once); returns what came of it, in words, for the log. Null where there is nothing to ask (not Windows).</summary>
    public static string? KeepResponsive()
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (Gate)
        {
            if (_result != null) return _result;
            try
            {
                var state = new PowerThrottlingState { Version = CurrentVersion, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
                if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
                    return _result = $"Windows would not turn off background throttling for this program (error {Marshal.GetLastWin32Error()}); a share that runs minimized may send fewer pictures a second";
                return _result = "background throttling is turned off for this program (it asked Windows to keep its timers fine and its speed up while sharing)";
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return _result = "this Windows cannot turn off background throttling for a program";
            }
        }
    }

    /// <summary>What Windows says the throttling is set to for this program: whether the speed and the timer resolution are left alone (the bits asked about, with their state off).</summary>
    public static bool IsThrottlingTurnedOff()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var state = new PowerThrottlingState { Version = CurrentVersion };
            if (!GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>())) return false;
            const uint both = ExecutionSpeed | IgnoreTimerResolution;
            return (state.ControlMask & both) == both && (state.StateMask & both) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
