using System.Collections.Concurrent;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>The share's second, lower stream (issue #254): its level, and a real share whose lower stream is encoded only while someone watches it.</summary>
public class LowStreamTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(1920, 1080, 60, 6000, 1280, 720, 30, 1500)]   // the top preset: 720p30 at the most a lower stream carries
    [InlineData(1920, 1080, 30, 2800, 1280, 720, 30, 933)]
    [InlineData(1280, 720, 30, 2000, 852, 480, 30, 666)]      // never more than two thirds of each side of the main box
    [InlineData(1280, 720, 15, 600, 852, 480, 15, 300)]       // never more pictures than the main stream; 300 kbit/s at the least
    [InlineData(640, 360, 10, 200, 426, 240, 10, 200)]        // and never more than the main bitrate
    public void The_lower_stream_is_always_below_the_main_one(int width, int height, int fps, int kbps, int lowWidth, int lowHeight, int lowFps, int lowKbps)
    {
        var level = LowStream.LevelFor(new ScreenOptions { MaxWidth = width, MaxHeight = height, Fps = fps, Kbps = kbps, X11Display = null });
        Assert.Equal((lowKbps, lowFps, lowWidth, lowHeight), level);
    }

    [Fact]
    public void It_is_a_stream_of_its_own_kind_so_no_list_shows_it_and_no_sound_is_made_for_it()
    {
        Assert.Equal("screen-low", LowStream.Kind);
        Assert.NotEqual("screen", LowStream.Kind);
        Assert.Equal("low:abc", LowStream.NameFor("abc"));
        Assert.True(LowStream.Kind.Length <= 16);   // what a stream's kind may be
    }

    [Fact]
    public void A_smaller_picture_keeps_the_shape_and_fits_the_box()
    {
        if (!FfmpegLibraries.IsAvailable) return;
        using var scaler = new PictureScaler();
        var picture = new byte[Nv12.Size(1920, 1080)];
        Array.Fill(picture, (byte)200, 0, 1920 * 1080);
        Array.Fill(picture, (byte)128, 1920 * 1080, picture.Length - 1920 * 1080);
        scaler.Scale(picture, 1920, 1080, 1280, 720);
        Assert.Equal((1280, 720), (scaler.Width, scaler.Height));
        Assert.Equal(Nv12.Size(1280, 720), scaler.Output.Length);
        Assert.InRange(scaler.Output[1280 * 360 + 640], 195, 205);   // the picture, not black
        scaler.Scale(picture, 1920, 1080, 852, 480);
        Assert.Equal((852, 478), (scaler.Width, scaler.Height));      // 16:9 into 852x480, even sides
    }

    [Fact]
    public async Task A_real_share_encodes_its_lower_stream_only_while_someone_watches_it()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;
        var options = new ScreenOptions
        {
            Display = display, X11Display = null, Fps = 30, Kbps = 3000, KeyFrameSeconds = 1, MaxWidth = 1920, MaxHeight = 1080,
            Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, CaptureOnCard = true,
        };
        using var c = await MediaTreeTests.Cluster.StartAsync(3, fanout: 2, rootFanout: 2);
        using var sharer = ScreenSharer.Start(c.Root, c.Rooms[0], "Test screen", options);
        try { await TestUtil.WaitFor(() => sharer.Frames >= 30, 30_000); }
        catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
        if (sharer.Encoder != HardwareEncoder.Nvenc) return;   // no NVIDIA encoder here
        var low = sharer.Low;
        Assert.NotNull(low);

        // Nobody is in trouble: there is no lower stream at all.
        MediaGroupInfo? Group() => c.Rooms[1].MediaGroups.FirstOrDefault(g => g.Kind == LowStream.Kind && g.Name == LowStream.NameFor(sharer.Source.Info.Id));
        await Task.Delay(2000);
        Assert.False(low!.Started);
        Assert.Null(Group());
        Assert.Contains("Lower stream: 0 watching; not started", sharer.Describe());

        // A viewer of the main stream reports trouble (as the quality watch passes it on each second): the stream is started, and still nothing is encoded for it.
        var started = new ConcurrentQueue<bool>();
        low.StartedChanged += started.Enqueue;
        long t0 = Environment.TickCount64;
        low.Tick(t0, troubled: 1);
        Assert.True(low.Started);
        Assert.Equal(new[] { true }, started);
        await TestUtil.WaitFor(() => Group() != null, 15_000);
        var group = Group();
        await Task.Delay(1000);
        Assert.False(low.Encoding);
        Assert.Equal(0, low.Frames);

        // A viewer watches it: smaller pictures come, a key frame first, while the main stream goes on as it was.
        using var view = c.Nodes[1].WatchMedia(c.Rooms[1], group!.Id);
        var sizes = new ConcurrentQueue<(int W, int H)>();
        view.FrameReceived += f => { if (f.IsKey && ScreenCodecs.TryGetKeyFrame(f.Data.Span, out _, out int w, out int h)) sizes.Enqueue((w, h)); };
        await TestUtil.WaitFor(() => low.Frames >= 30 && !sizes.IsEmpty, 30_000);
        output.WriteLine("lower stream: " + low.Describe());
        output.WriteLine(sharer.Describe());
        Assert.True(low.Encoding);
        Assert.True(sizes.TryPeek(out var size));
        Assert.True(size.W <= 1280 && size.H <= 720, $"the lower stream's picture is {size.W}x{size.H}");
        Assert.True(size.W < sharer.PictureSize!.Value.Width, "smaller than the main stream's");
        Assert.Contains("Lower stream: 1 watching; encoding h264_nvenc", sharer.Describe());
        long mainFrames = sharer.Frames;
        await Task.Delay(1000);
        Assert.True(sharer.Frames > mainFrames + 20, "the main stream goes on");

        // The viewer leaves: after the linger nothing is encoded for it any more.
        view.Dispose();
        await TestUtil.WaitFor(() => !low.Encoding, (int)(LowStream.Linger.TotalMilliseconds + 15_000));
        long lowFrames = low.Frames;
        await Task.Delay(1500);
        Assert.InRange(low.Frames - lowFrames, 0, 2);

        // Nobody in trouble and nobody on it: kept for a minute, then taken away (the clock is passed in, so the minute is not waited for).
        long now = Environment.TickCount64;
        low.Tick(now, troubled: 0);
        Assert.True(low.Started);
        low.Tick(now + (long)LowStream.Unneeded.TotalMilliseconds + 1000, troubled: 0);
        Assert.False(low.Started);
        Assert.Equal(new[] { true, false }, started);
        await TestUtil.WaitFor(() => Group() == null, 15_000);
    }
}
