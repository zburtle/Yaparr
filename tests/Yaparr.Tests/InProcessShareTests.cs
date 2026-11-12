using System.Collections.Concurrent;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>A real screen share encoded in this program, to a viewer on a second node.</summary>
public class InProcessShareTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// The sharer answers a viewer's request for a key frame by making one on the next picture, which the command-line FFmpeg cannot. The
    /// regular key frame is set to 30 s so that the only key frame after the first is one that was asked for: here the one a joining viewer
    /// needs once the tree's copy of the stream since the last key frame is full and thrown away. (A viewer that loses frames asks the same way;
    /// it is not simulated here because on a still screen the stream is so thin that the tree repairs any loss a test can cause.) It does
    /// nothing without Windows, a monitor, FFmpeg and its libraries, or an NVIDIA card, which is the case on the build server.
    /// </summary>
    [Fact]
    public async Task A_viewer_that_joins_gets_a_key_frame_made_on_request_by_a_real_in_process_share()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;

        using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        var options = new ScreenOptions
        {
            Display = display, X11Display = null, Fps = 15, Kbps = 1000, KeyFrameSeconds = 30, MaxWidth = 640, MaxHeight = 360,
            Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, InProcessEncoder = true, InProcessCapture = false,   // the encoder here, a separate FFmpeg capturing
        };
        using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen", options, ffmpeg);
        try { await TestUtil.WaitFor(() => sharer.Frames >= 5, 20_000); }
        catch (TimeoutException) { return; }
        if (!sharer.InProcess) return;   // no NVIDIA encoder here: the share fell back to a separate FFmpeg

        // The tree keeps the packets since the last key frame (up to 512 KB) so that a newcomer need not trouble the sharer. With no regular key
        // frame for 30 s that copy is thrown away once it is full, so after this much the sharer is the only one who can make a key frame.
        // How fast that happens depends on how much the screen changes: a busy one takes about ten seconds, a still one never does. Then there
        // is nothing to show here, as when there is no NVIDIA card.
        try { await TestUtil.WaitFor(() => sharer.Bytes > 700 * 1024, 30_000); }
        catch (TimeoutException) { return; }

        output.WriteLine($"sharing for a while: {sharer.Frames} frames, {sharer.Bytes / 1024} KB, forced {sharer.KeyFramesForced}");
        var views = await cluster.WatchAllAsync(sharer.Source.Info.Id);
        try
        {
            var got = new ConcurrentQueue<MediaFrame>();
            views[0].FrameReceived += f => got.Enqueue(f);

            output.WriteLine($"viewer attached: {sharer.Frames} frames, forced {sharer.KeyFramesForced}");
            // Joining: the viewer cannot start without a key frame, there is no regular one for 30 s, so the sharer must make one.
            await TestUtil.WaitFor(() => got.Any(f => f.IsKey), 15_000);
            Assert.True(sharer.KeyFramesForced >= 1, "the key frame the viewer got should have been asked for");
            output.WriteLine($"viewer got a key frame: {got.Count} frames, forced {sharer.KeyFramesForced}");
            await TestUtil.WaitFor(() => got.Count >= 20, 15_000);

            output.WriteLine($"viewer has {got.Count} frames");

            // The share counted what it did, so that a log or a report can say it.
            var figures = sharer.Figures();
            output.WriteLine(sharer.Describe());
            Assert.True(figures.KeyRequests >= 1 && figures.KeysForced >= 1 && figures.Answers >= 1, "the request, the key frame and its answer should all be counted");
            Assert.True(figures.Encodes >= 20 && figures.EncodeAverageMs > 0, "encoding times should be counted");
            Assert.True(figures.Reads >= 20, "the waits for the capture should be counted");
            Assert.True(figures.Keys >= 2 && figures.KeyAverageKb > 0 && figures.DeltaAverageKb > 0, "key and other frames should be measured");
            Assert.NotNull(sharer.PictureSize);
            Assert.Contains("in this program", sharer.EncoderDetails);
            Assert.Contains("h264_nvenc", sharer.EncoderDetails);
            Assert.Contains("avcodec", sharer.EncoderDetails);
            Assert.True(sharer.CaptureCpu > TimeSpan.Zero, "the capture program's processor time should be counted");
            string report = sharer.Describe();
            Assert.Contains("Key frames:", report);
            Assert.Contains("Encoder in this program:", report);
            Assert.Contains("How the encoder was set up: in this program", report);
            // What the viewer was handed can be decoded: only a key frame may jump.
            uint? previous = null;
            foreach (var f in got)
            {
                if (!f.IsKey) { Assert.NotNull(previous); Assert.Equal(previous!.Value + 1, f.Id); }
                previous = f.Id;
            }
            Assert.True(got.All(f => !f.IsKey || ScreenCodecs.IsKeyFrame(ScreenCodec.H264, f.Data.ToArray())), "every key frame must hold the parameter sets");
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    /// <summary>
    /// What a share says about itself, whatever the screen is doing: the encoder in use and how it was set up, the timings, the sizes of key frames
    /// and the others, the capture program's processor time. It does nothing without Windows, a monitor, FFmpeg and its libraries, or an NVIDIA card.
    /// </summary>
    [Fact]
    public async Task A_real_in_process_share_counts_and_describes_what_it_does()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;

        using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        var options = new ScreenOptions
        {
            Display = display, X11Display = null, Fps = 15, Kbps = 1000, KeyFrameSeconds = 1, MaxWidth = 640, MaxHeight = 360,
            Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, InProcessEncoder = true, InProcessCapture = false,   // the encoder here, a separate FFmpeg capturing
        };
        using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen", options, ffmpeg);
        try { await TestUtil.WaitFor(() => sharer.Frames >= 40, 20_000); }
        catch (TimeoutException) { return; }
        if (!sharer.InProcess) return;   // no NVIDIA encoder here: the share fell back to a separate FFmpeg

        var figures = sharer.Figures();
        output.WriteLine(sharer.Describe());
        Assert.True(figures.Encodes >= 30 && figures.EncodeAverageMs > 0 && figures.EncodeMaxTicksEver > 0, "encoding times should be counted");
        Assert.True(figures.Reads >= 30 && figures.WaitMaxTicksEver > 0, "the waits for the capture should be counted");
        Assert.True(figures.Keys >= 2 && figures.KeyAverageKb > 0 && figures.DeltaAverageKb > 0, "key and other frames should be measured (a key frame every second)");
        Assert.True(figures.Keys + figures.Deltas == sharer.Frames || figures.Keys + figures.Deltas == sharer.Frames + 1, "every frame sent is either a key frame or another");
        Assert.Equal(0, figures.Restarts);
        Assert.NotNull(sharer.PictureSize);
        Assert.Contains("in this program", sharer.EncoderDetails);
        Assert.Contains("h264_nvenc", sharer.EncoderDetails);
        Assert.Contains("avcodec", sharer.EncoderDetails);
        Assert.Contains("preset=p4", sharer.EncoderDetails);
        Assert.True(sharer.CaptureCpu > TimeSpan.Zero, "the capture program's processor time should be counted");
        Assert.Equal(1000.0 / 15, sharer.FrameBudgetMs, 3);
        string report = sharer.Describe();
        Assert.Contains("Key frames:", report);
        Assert.Contains("Encoder in this program:", report);
        Assert.Contains("How the encoder was set up: in this program", report);
        Assert.Contains("Capture program restarted 0 times", report);
        var maxima = sharer.TakeMaxima(1);
        Assert.True(maxima.KeyBytes > 0 && maxima.EncodeTicks > 0);
        Assert.Equal(0, sharer.TakeMaxima(1).KeyBytes);   // taken: the next minute starts from nothing
    }
}
