using System.IO.Compression;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class InProcessSettingsTests
{
    [Theory]
    [InlineData(null, true, InProcessMode.CaptureOnCard, false)]    // the default on Windows: all of it
    [InlineData("", true, InProcessMode.CaptureOnCard, false)]
    [InlineData("something else", true, InProcessMode.CaptureOnCard, false)]
    [InlineData(null, false, InProcessMode.CaptureOnCpu, false)]    // elsewhere (Linux): the encoder and the X11 capture here
    [InlineData("0", true, InProcessMode.CaptureOnCard, false)]     // there is no separate FFmpeg to go back to: the old values mean nothing now
    [InlineData("off", false, InProcessMode.CaptureOnCpu, false)]
    [InlineData("encoder", true, InProcessMode.CaptureOnCard, false)]
    [InlineData("capture-cpu", true, InProcessMode.CaptureOnCpu, true)]
    [InlineData("capture", true, InProcessMode.CaptureOnCard, true)]
    [InlineData(" Capture ", true, InProcessMode.CaptureOnCard, true)]
    [InlineData("capture", false, InProcessMode.CaptureOnCpu, true)] // on Linux the pictures are in memory (an X11 screen)
    public void The_environment_variable_says_whether_the_pictures_stay_on_the_card(string? value, bool windows, InProcessMode mode, bool asked)
        => Assert.Equal((mode, asked), InProcessSettings.Parse(value, windows));

    [Fact]
    public void The_options_follow_the_mode()
    {
        // The default (nothing set) on Windows is all of it; what the test runner's own environment says is not what is checked here.
        var (mode, _) = InProcessSettings.Parse(null, windows: true);
        Assert.Equal(InProcessMode.CaptureOnCard, mode);
        var options = new ScreenOptions { CaptureOnCard = true, InProcessAsked = true };
        var again = options.Retarget(null, 5);
        Assert.Equal((options.CaptureOnCard, options.InProcessAsked), (again.CaptureOnCard, again.InProcessAsked));
    }

    private static string TempMark() => Path.Combine(Path.GetTempPath(), "yaparr-guard-" + Guid.NewGuid().ToString("N"), "mark.txt");

    [Fact]
    public void A_share_that_ends_well_leaves_nothing_behind_and_the_guard_stays_open()
    {
        var mark = TempMark();
        try
        {
            InProcessGuard.UseFile(mark);
            Assert.True(InProcessGuard.Allowed);
            InProcessGuard.Begin();
            Assert.Contains($"active={Environment.ProcessId}", File.ReadAllText(mark));
            InProcessGuard.End();
            Assert.Contains("active=0", File.ReadAllText(mark));
            Assert.Contains("unclean=0", File.ReadAllText(mark));
            InProcessGuard.UseFile(mark);   // the next start
            Assert.True(InProcessGuard.Allowed);
            Assert.Equal(0, InProcessGuard.UncleanEndings);
        }
        finally { InProcessGuard.UseFile(null); }
    }

    [Fact]
    public void Two_programs_in_a_row_that_ended_in_the_middle_of_a_share_turn_the_default_off_and_a_good_ending_turns_it_on_again()
    {
        var mark = TempMark();
        Directory.CreateDirectory(Path.GetDirectoryName(mark)!);
        // A program that is gone: a process id nothing runs under.
        const int gone = 2_000_000_000;
        try
        {
            File.WriteAllText(mark, $"unclean=0\nactive={gone}\n");
            InProcessGuard.UseFile(mark);
            Assert.Equal(1, InProcessGuard.UncleanEndings);
            Assert.True(InProcessGuard.Allowed, "one is allowed: the program may have been stopped from the task manager");
            InProcessGuard.Begin();   // and it ends in the middle of the next one too

            InProcessGuard.UseFile(mark);   // the start after that
            File.WriteAllText(mark, $"unclean=1\nactive={gone}\n");
            InProcessGuard.UseFile(mark);
            Assert.Equal(2, InProcessGuard.UncleanEndings);
            Assert.False(InProcessGuard.Allowed);

            // A person who asks for it in so many words is not stopped (the share checks that), and one that ends well forgives it all.
            InProcessGuard.End();
            InProcessGuard.UseFile(mark);
            Assert.True(InProcessGuard.Allowed);
        }
        finally { InProcessGuard.UseFile(null); try { Directory.Delete(Path.GetDirectoryName(mark)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_mark_of_a_program_that_is_still_running_is_not_an_ending_in_the_middle()
    {
        var mark = TempMark();
        Directory.CreateDirectory(Path.GetDirectoryName(mark)!);
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "sleep")
            { Arguments = OperatingSystem.IsWindows() ? "/c ping -n 20 127.0.0.1 >nul" : "20", UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            File.WriteAllText(mark, $"unclean=0\nactive={other.Id}\n");
            InProcessGuard.UseFile(mark);
            Assert.Equal(0, InProcessGuard.UncleanEndings);
        }
        finally { InProcessGuard.UseFile(null); try { other.Kill(true); } catch (InvalidOperationException) { } try { Directory.Delete(Path.GetDirectoryName(mark)!, true); } catch (IOException) { } }
    }

    private sealed class Serve(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
