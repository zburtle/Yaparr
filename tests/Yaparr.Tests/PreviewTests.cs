using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class PreviewTests
{
    private sealed class Frames : IMediaFrameSource
    {
        public event Action<MediaFrame>? FrameReceived;
        public void Push(byte[] data, bool isKey) => FrameReceived?.Invoke(new MediaFrame(default, 0, data, isKey));
    }

    [Theory]
    [InlineData(1920, 1032, 640, 344)]    // as large as fits
    [InlineData(1920, 1080, 640, 360)]
    [InlineData(1080, 1920, 202, 360)]    // a tall window: the height decides, both sides even
    [InlineData(320, 180, 320, 180)]      // never enlarged
    [InlineData(2, 2, 2, 2)]
    public void A_picture_is_shown_as_large_as_fits_in_the_box_and_never_larger_than_it_is(int width, int height, int expectedWidth, int expectedHeight)
    {
        var (w, h) = new PreviewSize(640, 360).Fit(width, height);
        Assert.Equal((expectedWidth, expectedHeight), (w, h));
        Assert.True(w % 2 == 0 && h % 2 == 0);
    }

    [Fact]
    public void A_preview_decodes_with_one_thread_and_scales_in_ffmpeg_and_never_uses_the_card()
    {
        string line = string.Join(' ', FfmpegArgs.DecodeForViewer(ScreenCodec.H264, gpu: true, previewSize: (640, 344)));
        Assert.Contains("-threads 1", line);
        Assert.Contains("-vf scale=640:344", line);
        Assert.DoesNotContain("-hwaccel", line);
        Assert.Contains("-f rawvideo -pix_fmt nv12 pipe:1", line);
        Assert.True(line.IndexOf("-threads 1", StringComparison.Ordinal) < line.IndexOf("-i pipe:0", StringComparison.Ordinal), "an input option, so before the input");
        // A watch is unchanged.
        string watch = string.Join(' ', FfmpegArgs.DecodeForViewer(ScreenCodec.H264, gpu: false));
        Assert.DoesNotContain("-threads", watch);
        Assert.DoesNotContain("scale", watch);
    }

    [Fact]
    public void A_preview_of_every_frame_decodes_on_the_card_and_scales_what_it_downloads()
    {
        string card = string.Join(' ', FfmpegArgs.DecodeForViewer(ScreenCodec.H264, gpu: true, previewSize: (640, 344), keyFramesOnly: false));
        Assert.Contains("-hwaccel d3d11va", card);
        Assert.Contains("-vf hwdownload,format=nv12,scale=640:344", card);
        Assert.DoesNotContain("-threads", card);   // every frame goes in, so nothing is held back for long
        string software = string.Join(' ', FfmpegArgs.DecodeForViewer(ScreenCodec.H264, gpu: false, previewSize: (640, 344), keyFramesOnly: false));
        Assert.Contains("-vf scale=640:344", software);
        Assert.DoesNotContain("-hwaccel", software);
        // Whatever is asked, a card is for H.264 only.
        Assert.DoesNotContain("-hwaccel", string.Join(' ', FfmpegArgs.DecodeForViewer(ScreenCodec.Vp8, gpu: true, previewSize: (640, 344), keyFramesOnly: false)));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(15, false)]
    public void Up_to_two_pictures_a_second_is_key_frames_only(int fps, bool keysOnly) => Assert.Equal(keysOnly, new PreviewSize(640, 360, fps).KeyFramesOnly);

    [Fact]
    public void A_preview_does_not_even_copy_the_frames_between_key_frames()
    {
        var frames = new Frames();
        using var viewer = new ScreenViewer(frames, "ffmpeg-that-is-never-started", preview: new PreviewSize(640, 360));
        for (int n = 0; n < 100; n++) frames.Push(new byte[5000], isKey: false);
        Assert.Equal(0, viewer.FramesIn);
        Assert.Equal(TimeSpan.Zero, viewer.DecoderCpu);

        var watch = new Frames();
        using var full = new ScreenViewer(watch, "ffmpeg-that-is-never-started");
        for (int n = 0; n < 100; n++) watch.Push(new byte[5000], isKey: false);
        Assert.Equal(100, full.FramesIn);   // a watch counts every frame
    }

    [Fact]
    public void A_preview_of_every_frame_takes_in_every_frame()
    {
        var frames = new Frames();
        using var viewer = new ScreenViewer(frames, "ffmpeg-that-is-never-started", preview: new PreviewSize(640, 360, 15));
        for (int n = 0; n < 100; n++) frames.Push(new byte[5000], isKey: false);
        Assert.Equal(100, viewer.FramesIn);
    }

    [Fact]
    public async Task A_preview_of_fifteen_a_second_shows_about_fifteen_pictures_a_second_scaled_down()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        string? ffmpeg = FfmpegLocator.Find();
        if (ffmpeg == null) return;
        const int width = 1280, height = 720;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(width, height, new ScreenOptions { Fps = 60, Kbps = 4000, KeyFrameSeconds = 1 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }   // no NVENC here
        using (encoder)
        {
            var frames = new Frames();
            var shown = new List<(int W, int H, long At)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var viewer = new ScreenViewer(frames, ffmpeg, preview: new PreviewSize(640, 360, 15));
            viewer.PictureReceived += p => { lock (shown) shown.Add((p.Width, p.Height, clock.ElapsedMilliseconds)); viewer.Return(p); };

            var picture = new byte[encoder.PictureBytes];
            for (int n = 0; n < 240; n++)   // frames as the share sends them (the delay is the nearest a timer gets to 60 a second)
            {
                for (int i = 0; i < width * height; i++) picture[i] = (byte)((i % width) / 5 + n * 3);
                Array.Fill(picture, (byte)128, width * height, picture.Length - width * height);
                encoder.Encode(picture, forceKey: n % 60 == 0, (frame, isKey) => frames.Push(frame, isKey));
                await Task.Delay(16);
            }
            await Task.Delay(500);

            lock (shown)
            {
                Assert.All(shown, s => Assert.Equal((640, 360), (s.W, s.H)));
                // Judged by the rate between the first and the last picture (the test's own pacing decides how long the share lasts): about 15 a second,
                // not the 60 a second that were sent, and not one a second.
                Assert.True(shown.Count > 20, $"{shown.Count} pictures");
                double rate = (shown.Count - 1) * 1000.0 / Math.Max(1, shown[^1].At - shown[0].At);
                Assert.True(rate is >= 11 and <= 18, $"{rate:0.0} pictures a second");
            }
            Assert.True(viewer.FramesIn > 200, $"{viewer.FramesIn} frames went in; a preview of every frame takes them all");
            Assert.True(viewer.DecoderCpu > TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task A_preview_shows_each_key_frame_at_once_scaled_down_and_costs_next_to_nothing()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        string? ffmpeg = FfmpegLocator.Find();
        if (ffmpeg == null) return;
        const int width = 1280, height = 720;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(width, height, new ScreenOptions { Fps = 30, Kbps = 4000, KeyFrameSeconds = 1 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }   // no NVENC here
        using (encoder)
        {
            var frames = new Frames();
            var shown = new List<(int W, int H, long At)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var viewer = new ScreenViewer(frames, ffmpeg, preview: new PreviewSize(640, 360));
            viewer.PictureReceived += p => { lock (shown) shown.Add((p.Width, p.Height, clock.ElapsedMilliseconds)); viewer.Return(p); };

            var picture = new byte[encoder.PictureBytes];
            long deltas = 0;
            for (int n = 0; n < 75; n++)   // 2.5 s at 30 fps: key frames at 0 and 1 and 2 s
            {
                for (int i = 0; i < width * height; i++) picture[i] = (byte)((i % width) / 5 + n * 3);   // a moving ramp
                Array.Fill(picture, (byte)128, width * height, picture.Length - width * height);
                encoder.Encode(picture, forceKey: n % 30 == 0, (frame, isKey) => { if (!isKey) deltas++; frames.Push(frame, isKey); });
                await Task.Delay(33);
            }
            await TestUtil.WaitFor(() => { lock (shown) return shown.Count >= 3; }, 10_000);

            lock (shown)
            {
                Assert.All(shown, s => Assert.Equal((640, 360), (s.W, s.H)));
                // One key frame goes in twice, so each gives two pictures, the first at once rather than when the next key frame arrives.
                Assert.True(shown.Count >= 5, $"{shown.Count} pictures for 3 key frames");
                Assert.True(shown[0].At < 3000, $"the first picture came after {shown[0].At} ms");
            }
            Assert.True(deltas > 50);
            Assert.Equal(3, viewer.FramesIn);   // only the key frames were taken in
            Assert.True(viewer.DecoderCpu > TimeSpan.Zero, "the decoder's processor time is counted");
        }
    }
}
