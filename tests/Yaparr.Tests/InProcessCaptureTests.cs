using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>
/// Windows Graphics Capture inside this program. These need Windows, a monitor, FFmpeg's libraries with the Graphics Capture fixes of October 2026
/// (the libraries from the pinned FFmpeg commit, or a recent master build; an older release lacks the capture's <c>dup_frames</c> option) and, for the
/// share, an NVIDIA card, so they do nothing on the build server.
/// </summary>
public class InProcessCaptureTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static ScreenOptions Options(Display display, int fps = 30, bool onCard = false) => new()
    {
        Display = display, X11Display = null, Fps = fps, Kbps = 1000, KeyFrameSeconds = 1, MaxWidth = 640, MaxHeight = 360,
        Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, InProcessEncoder = true, InProcessCapture = true, CaptureOnCard = onCard,
    };

    [Fact]
    public void The_capture_filter_text_is_what_the_command_line_uses()
    {
        var options = Options(new Display(0, "primary", 0, 0, 1920, 1080, true, 0xAB), 60);
        string graph = FfmpegArgs.CaptureFilter(options, raw: true);
        Assert.StartsWith("gfxcapture=hmonitor=", graph);
        Assert.Contains("max_framerate=60", graph);
        Assert.Contains("dup_frames=1", graph);
        Assert.Contains("hwdownload,format=bgra,scale='min(640,iw)':'min(360,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=nv12", graph);
        Assert.Contains(graph, string.Join(' ', FfmpegArgs.CaptureRaw(options)));   // the command line is the same graph
    }

    [Fact]
    public async Task A_capture_in_this_program_gives_even_sized_NV12_pictures_that_can_be_taken_again_and_again()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;
        InProcessCapture capture;
        try { capture = new InProcessCapture(Options(display)); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("dup_frames") || ex.Message.Contains("gfxcapture")) { output.WriteLine("libraries too old: " + ex.Message); return; }
        using (capture)
        {
            await capture.WaitForFirstAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            Assert.True(capture.Width is >= 16 and <= 640 && capture.Height is >= 16 and <= 360, $"{capture.Width}x{capture.Height}");
            Assert.True(capture.Width % 2 == 0 && capture.Height % 2 == 0);
            Assert.Equal(capture.Width * capture.Height * 3 / 2, capture.PictureBytes);

            int taken = 0, fresh = 0;
            for (int i = 0; i < 20; i++)
            {
                using var picture = capture.TakeNewest(out bool isNew, out _);
                Assert.NotNull(picture);
                Assert.Equal((capture.Width, capture.Height), (picture!.Width, picture.Height));
                taken++;
                if (isNew) fresh++;
                await Task.Delay(33);
            }
            output.WriteLine($"{taken} taken, {fresh} of them new; the capture gave {capture.Pictures} pictures");
            Assert.True(fresh >= 1, "the first take is a new picture");
            Assert.Null(capture.EndedBecause);
        }
    }

    [Fact]
    public async Task A_real_share_with_the_capture_in_this_program_sends_an_even_stream_and_runs_no_ffmpeg()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;

        using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen", Options(display, fps: 30), ffmpeg);
        try { await TestUtil.WaitFor(() => sharer.Frames >= 60, 30_000); }
        catch (TimeoutException) { output.WriteLine(sharer.Describe()); return; }
        output.WriteLine(sharer.Describe());
        if (!sharer.InProcess) return;   // no NVIDIA encoder here

        if (!sharer.CaptureInProcess)
        {
            // The libraries lack what the capture needs: the share fell back to the encoder here and a separate FFmpeg capturing, and said why.
            Assert.Contains("too old", sharer.FallbackReason ?? "");
            return;
        }
        var figures = sharer.Figures();
        Assert.Equal(TimeSpan.Zero, sharer.CaptureCpu);   // there is no capture program
        Assert.Contains("Capture: in this program", sharer.Describe());
        Assert.Contains("gfxcapture", sharer.EncoderDetails);
        Assert.True(figures.Encodes >= 50);
        Assert.True(figures.Keys >= 2, "a key frame every second");
        Assert.Equal(0, figures.Restarts);
        // The share's own clock sets the pace: 30 pictures a second whatever the capture does, and none arriving in a burst after another.
        Assert.True(figures.Bunched * 10 <= figures.Reads + 1, $"{figures.Bunched} of {figures.Reads} pictures came straight after another");
        Assert.Null(sharer.FallbackReason);
    }

    [Fact]
    public void The_capture_filter_for_pictures_on_the_card_scales_there_and_ends_at_the_capture()
    {
        var options = Options(new Display(0, "primary", 0, 0, 3840, 2160, true, 0xAB), 60, onCard: true);
        string graph = FfmpegArgs.CaptureFilterOnCard(options)!;
        Assert.Equal("gfxcapture=hmonitor=0xAB:max_framerate=60:capture_cursor=1:resize_mode=scale_aspect:dup_frames=1:width=640:height=360", graph);
        Assert.DoesNotContain("hwdownload", graph);
        // The shape is kept, in even numbers, and a small monitor is not enlarged.
        Assert.EndsWith(":width=640:height=360", FfmpegArgs.CaptureFilterOnCard(Options(new Display(0, "x", 0, 0, 1920, 1080, true, 1), onCard: true))!);
        Assert.EndsWith(":width=480:height=270", FfmpegArgs.CaptureFilterOnCard(Options(new Display(0, "x", 0, 0, 480, 270, true, 1), onCard: true))!);
        Assert.EndsWith(":width=360:height=360", FfmpegArgs.CaptureFilterOnCard(Options(new Display(0, "x", 0, 0, 1000, 1000, true, 1), onCard: true))!);
        // No monitor and no window: no size to scale to, so no graph (the share goes on with the copy to memory).
        Assert.Null(FfmpegArgs.CaptureFilterOnCard(new ScreenOptions { X11Display = null, Fps = 30 }));
    }

    [Fact]
    public async Task A_real_share_with_the_pictures_kept_on_the_card_runs_without_copying_them_to_memory()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;

        using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen", Options(display, fps: 30, onCard: true), ffmpeg);
        try { await TestUtil.WaitFor(() => sharer.Frames >= 90, 30_000); }
        catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
        output.WriteLine(sharer.Describe());
        if (!sharer.InProcess) return;   // no NVIDIA encoder here
        if (!sharer.CaptureInProcess) { Assert.Contains("too old", sharer.FallbackReason ?? ""); return; }
        Assert.True(sharer.CaptureOnCard, "the share fell back to copying the pictures to memory: " + sharer.FallbackReason);
        Assert.Contains("pictures kept on the graphics card", sharer.EncoderDetails);
        Assert.Contains("pictures read from the graphics card", sharer.EncoderDetails);
        Assert.DoesNotContain("hwdownload", sharer.EncoderDetails);
        var figures = sharer.Figures();
        Assert.True(figures.Encodes >= 80);
        Assert.True(figures.Keys >= 2, "a key frame every second");
        Assert.Equal(TimeSpan.Zero, sharer.CaptureCpu);
        Assert.Equal(0, figures.Restarts);
    }

    [Fact]
    public async Task A_real_share_lowered_to_another_frame_rate_and_size_builds_the_capture_and_encoder_again_and_goes_on_sending()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;

        // Asked for at the top preset: 1080p60 at 6 Mbit/s (the pictures are the monitor's, scaled down to the box).
        var asked = new ScreenOptions
        {
            Display = display, X11Display = null, Fps = 60, Kbps = 6000, KeyFrameSeconds = 1, MaxWidth = 1920, MaxHeight = 1080,
            Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, InProcessEncoder = true, InProcessCapture = true, CaptureOnCard = true,
        };
        using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen", asked, ffmpeg);
        try { await TestUtil.WaitFor(() => sharer.Frames >= 120, 30_000); }
        catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
        if (!sharer.InProcess || !sharer.CaptureInProcess) return;   // no NVIDIA encoder here, or libraries too old for the capture
        Assert.Equal(60, sharer.RunFps);
        var before = sharer.PictureSize!.Value;

        sharer.ApplyStepForTest(1, "test");   // 1080p30
        long frames = sharer.Frames;
        await TestUtil.WaitFor(() => sharer.RunFps == 30 && sharer.Frames >= frames + 60, 30_000);
        output.WriteLine($"step 1: {sharer.PictureSize} at {sharer.RunFps} fps, {sharer.EncoderDetails}");

        Assert.Contains("at 30 fps, 4000 kbit/s", sharer.EncoderDetails);

        var atStep1 = sharer.PictureSize!.Value;
        sharer.ApplyStepForTest(2, "test");   // the same picture at 2.8 Mbit/s: only the bitrate changes, nothing is built again
        frames = sharer.Frames;
        await TestUtil.WaitFor(() => sharer.PendingKbpsForTest == 0 && sharer.Frames >= frames + 60, 30_000);
        Assert.Equal(2800, sharer.Source.EffectiveKbps);
        Assert.Contains("at 30 fps, 4000 kbit/s", sharer.EncoderDetails);   // the description is of how the encoder was set up
        Assert.Equal(atStep1, sharer.PictureSize!.Value);
        Assert.True(sharer.IsRunning);

        sharer.ApplyStepForTest(3, "test");   // 720p30
        frames = sharer.Frames;
        await TestUtil.WaitFor(() => sharer.PictureSize is { Height: <= 720 } && sharer.Frames >= frames + 60, 30_000);
        output.WriteLine($"step 2: {sharer.PictureSize} at {sharer.RunFps} fps, {sharer.EncoderDetails}");
        Assert.Contains("at 30 fps, 2000 kbit/s", sharer.EncoderDetails);
        Assert.True(sharer.PictureSize!.Value.Width <= 1280 && sharer.PictureSize.Value.Height <= 720);

        sharer.ApplyStepForTest(0, "test");   // back to what was asked for
        frames = sharer.Frames;
        await TestUtil.WaitFor(() => sharer.RunFps == 60 && sharer.Frames >= frames + 120, 30_000);
        Assert.Equal(before, sharer.PictureSize!.Value);
        Assert.Contains("at 60 fps, 6000 kbit/s", sharer.EncoderDetails);
        Assert.True(sharer.IsRunning);
    }
}
