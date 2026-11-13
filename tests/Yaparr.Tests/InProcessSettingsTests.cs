using System.IO.Compression;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class InProcessSettingsTests
{
    [Theory]
    [InlineData(null, true, InProcessMode.CaptureOnCard, false)]    // the default on Windows: all of it
    [InlineData("", true, InProcessMode.CaptureOnCard, false)]
    [InlineData("something else", true, InProcessMode.CaptureOnCard, false)]
    [InlineData(null, false, InProcessMode.Encoder, false)]         // elsewhere (Linux): the encoder here, a separate FFmpeg captures
    [InlineData("0", true, InProcessMode.Off, true)]
    [InlineData("off", true, InProcessMode.Off, true)]
    [InlineData("OFF", false, InProcessMode.Off, true)]
    [InlineData("1", true, InProcessMode.Encoder, true)]
    [InlineData("1", false, InProcessMode.Encoder, true)]
    [InlineData("encoder", true, InProcessMode.Encoder, true)]
    [InlineData("capture-cpu", true, InProcessMode.CaptureOnCpu, true)]
    [InlineData("capture", true, InProcessMode.CaptureOnCard, true)]
    [InlineData(" Capture ", true, InProcessMode.CaptureOnCard, true)]
    [InlineData("capture", false, InProcessMode.Encoder, true)]     // capture here is Windows only so far
    public void The_environment_variable_says_how_much_is_done_in_this_program(string? value, bool windows, InProcessMode mode, bool asked)
        => Assert.Equal((mode, asked), InProcessSettings.Parse(value, windows));

    [Fact]
    public void The_options_follow_the_mode()
    {
        // The default (nothing set) on Windows is all of it; what the test runner's own environment says is not what is checked here.
        var (mode, _) = InProcessSettings.Parse(null, windows: true);
        Assert.Equal(InProcessMode.CaptureOnCard, mode);
        var options = new ScreenOptions { InProcessEncoder = true, InProcessCapture = true, CaptureOnCard = true };
        Assert.True(options.InProcessEncoder && options.InProcessCapture && options.CaptureOnCard);
        var again = options.Retarget(null, 5);
        Assert.Equal((options.InProcessEncoder, options.InProcessCapture, options.CaptureOnCard, options.InProcessAsked),
                     (again.InProcessEncoder, again.InProcessCapture, again.CaptureOnCard, again.InProcessAsked));
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

    [Fact]
    public async Task The_libraries_in_the_package_are_kept_next_to_the_program_and_the_app_finds_them_there()
    {
        string program = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-libs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zipPath = Path.Combine(dir, "pkg.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
                Add("pkg/bin/" + program, "program");
                Add("pkg/bin/" + (OperatingSystem.IsWindows() ? "avcodec-63.dll" : "libavcodec.so.63"), "library");
                Add("pkg/bin/" + (OperatingSystem.IsWindows() ? "avutil-61.dll" : "libavutil.so.61"), "library");
                Add("pkg/bin/notes.txt", "not a library");
                Add("pkg/bin/sub/other.dll", "not in that folder");
                Add("pkg/LICENSE.txt", "licence");
            }
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zipPath)));
            var package = new FfmpegPackage("test-shared", new Uri("https://example.invalid/pkg.zip"), sha, new FileInfo(zipPath).Length, "pkg/bin/" + program, "pkg/LICENSE.txt", "pkg");
            var exe = await FfmpegInstaller.InstallAsync(package, null, Path.Combine(dir, "install"), new Serve(File.ReadAllBytes(zipPath)));
            var folder = Path.GetDirectoryName(exe)!;
            Assert.True(File.Exists(Path.Combine(folder, OperatingSystem.IsWindows() ? "avcodec-63.dll" : "libavcodec.so.63")));
            Assert.True(File.Exists(Path.Combine(folder, OperatingSystem.IsWindows() ? "avutil-61.dll" : "libavutil.so.61")));
            Assert.False(File.Exists(Path.Combine(folder, "notes.txt")), "only libraries come with the program");
            Assert.False(File.Exists(Path.Combine(folder, "other.dll")), "only from the program's own folder in the zip");
            // The app looks for the libraries next to the FFmpeg it would run.
            Assert.Equal(Path.GetFullPath(folder), FfmpegLibraries.Find(Path.Combine(dir, "app"), explicitFolder: Path.Combine(dir, "nothing"), installDirectory: Path.Combine(dir, "install")));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed class Serve(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
