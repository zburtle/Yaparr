using Yaparr.Screen;

namespace Yaparr.Tests;

public class ProcessLoopbackTests
{
    // The recording used to be opened on the calling thread and read on its own: on a thread with a COM apartment (the interface
    // thread) the first read failed with "no such interface" and an uncaught exception ended the program.
    [Fact]
    public void Records_when_started_from_a_single_threaded_apartment()
    {
        if (!ProcessLoopback.IsSupported) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int received = 0;
                using var loopback = ProcessLoopback.Start(Environment.ProcessId, exclude: true, (_, _) => Interlocked.Increment(ref received));
                Thread.Sleep(500);
                Assert.Null(loopback.Failure);
            }
            catch (Exception ex) { failure = ex; }
        });
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void A_program_that_does_not_exist_is_an_error_to_the_caller_and_not_a_crash()
    {
        if (!ProcessLoopback.IsSupported) return;
        try { using var loopback = ProcessLoopback.Start(int.MaxValue, exclude: false, (_, _) => { }); }
        catch (Exception) { }
    }
}
