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
