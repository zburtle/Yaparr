using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class PreviewTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public PreviewTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    internal sealed class Frames : IMediaFrameSource
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

    // H.264 frames as the share makes them (a moving ramp), from NVIDIA's encoder; null where there is none.
    internal static List<(byte[] Frame, bool Key)>? H264Frames(int width, int height, int count)
    {
        if (!FfmpegLibraries.IsAvailable) return null;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(width, height, new ScreenOptions { Fps = 30, Kbps = 4000, KeyFrameSeconds = 1 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return null; }
        var frames = new List<(byte[], bool)>();
        using (encoder)
        {
            var picture = new byte[encoder.PictureBytes];
            for (int n = 0; n < count; n++)
            {
                for (int i = 0; i < width * height; i++) picture[i] = (byte)((i % width) / 5 + n * 3);
                Array.Fill(picture, (byte)128, width * height, picture.Length - width * height);
                encoder.Encode(picture, forceKey: n % 30 == 0, (frame, isKey) => frames.Add((frame, isKey)));
            }
        }
        return frames;
    }

    [Fact]
    public void A_preview_through_Direct3D_12_scales_on_the_card_and_copies_back_only_the_small_picture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var frames = H264Frames(1280, 720, 30);
        if (frames == null) return;
        InProcessDecoder decoder;
        try { decoder = new InProcessDecoder(ScreenCodec.H264, DecodeDevice.D3d12); }
        catch (PlatformNotSupportedException) { return; }   // no Direct3D 12 decoding here
        using (decoder)
        {
            int pictures = 0;
            foreach (var (frame, _) in frames)
                Assert.True(decoder.Decode(frame, 640, 360, (nv12, w, h) => { Assert.Equal((640, 360), (w, h)); Assert.Equal(Nv12.Size(640, 360), nv12.Length); pictures++; }), decoder.LastError);
            Assert.Equal(30, pictures);
            Assert.True(decoder.LastOnCard, "decoded on the card");
            Assert.True(decoder.ScaledOnCard, "scaled on the card: " + decoder.LastError);
            _out.WriteLine(decoder.Description);
        }
    }

    [Fact]
    public void A_watch_on_the_card_through_Direct3D_11_copies_back_the_whole_picture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var frames = H264Frames(1280, 720, 30);
        if (frames == null) return;
        InProcessDecoder decoder;
        try { decoder = new InProcessDecoder(ScreenCodec.H264, DecodeDevice.D3d11); }
        catch (PlatformNotSupportedException) { return; }
        using (decoder)
        {
            int pictures = 0;
            foreach (var (frame, _) in frames)
                Assert.True(decoder.Decode(frame, 0, 0, (nv12, w, h) => { Assert.Equal((1280, 720), (w, h)); pictures++; }), decoder.LastError);
            Assert.Equal(30, pictures);
            Assert.True(decoder.LastOnCard);
            Assert.False(decoder.ScaledOnCard);
        }
    }

    // Linux: NVIDIA's decoder (NVDEC, through CUDA) and VA-API, each tried where it opens. A preview through VA-API is scaled on the card (scale_vaapi);
    // through NVDEC the whole picture is copied back and scaled by swscale. Does nothing off Linux, or where neither the card nor the libraries can.
    [Theory]
    [InlineData(DecodeDevice.Cuda)]
    [InlineData(DecodeDevice.Vaapi)]
    public void On_Linux_H264_is_decoded_on_the_card_where_it_opens(DecodeDevice device)
    {
        if (!OperatingSystem.IsLinux()) return;
        var frames = H264Frames(1280, 720, 30);
        if (frames == null) return;
        InProcessDecoder decoder;
        try { decoder = new InProcessDecoder(ScreenCodec.H264, device); }
        catch (PlatformNotSupportedException ex) { _out.WriteLine($"{InProcessDecoder.Name(device)}: {ex.Message}"); return; }
        using (decoder)
        {
            int pictures = 0;
            foreach (var (frame, _) in frames)
                Assert.True(decoder.Decode(frame, 640, 360, (nv12, w, h) => { Assert.Equal((640, 360), (w, h)); pictures++; }), decoder.LastError);
            Assert.Equal(30, pictures);
            _out.WriteLine($"{decoder.Description}: pictures from the card {decoder.LastOnCard}, scaled on the card {decoder.ScaledOnCard}");
            Assert.True(decoder.LastOnCard, "decoded on the card");
            if (device == DecodeDevice.Vaapi) Assert.True(decoder.ScaledOnCard, decoder.LastError);
        }
    }

    [Fact]
    public void Only_H264_is_decoded_on_the_card()
    {
        if (!InProcessDecoder.IsAvailable(ScreenCodec.Vp8)) return;
        using var decoder = new InProcessDecoder(ScreenCodec.Vp8, DecodeDevice.D3d12);
        Assert.Equal(DecodeDevice.Processor, decoder.Device);
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
        using var viewer = new ScreenViewer(frames, preview: new PreviewSize(640, 360));
        for (int n = 0; n < 100; n++) frames.Push(new byte[5000], isKey: false);
        Assert.Equal(0, viewer.FramesIn);
        Assert.Equal(TimeSpan.Zero, viewer.DecoderCpu);

        var watch = new Frames();
        using var full = new ScreenViewer(watch);
        for (int n = 0; n < 100; n++) watch.Push(new byte[5000], isKey: false);
        Assert.Equal(100, full.FramesIn);   // a watch counts every frame
    }

    [Fact]
    public void A_preview_of_every_frame_takes_in_every_frame()
    {
        var frames = new Frames();
        using var viewer = new ScreenViewer(frames, preview: new PreviewSize(640, 360, 15));
        for (int n = 0; n < 100; n++) frames.Push(new byte[5000], isKey: false);
        Assert.Equal(100, viewer.FramesIn);
    }

    [Fact]
    public async Task A_preview_of_fifteen_a_second_shows_about_fifteen_pictures_a_second_scaled_down()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        const int width = 1280, height = 720;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(width, height, new ScreenOptions { Fps = 60, Kbps = 4000, KeyFrameSeconds = 1 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }   // no NVENC here
        using (encoder)
        {
            var frames = new Frames();
            var shown = new List<(int W, int H, long At)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var viewer = new ScreenViewer(frames, preview: new PreviewSize(640, 360, 15));
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
            // Direct3D 12 first, then 11, then the processor: whichever the libraries and the card can do, and a note of what did not work.
            _out.WriteLine($"decoded {viewer.DecoderKind}; {shown.Count} pictures, decoder processor time {viewer.DecoderCpu.TotalSeconds:0.00} s");
            Assert.StartsWith("on the ", viewer.DecoderKind);
        }
    }

    [Fact]
    public async Task A_preview_shows_each_key_frame_at_once_scaled_down_and_costs_next_to_nothing()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        const int width = 1280, height = 720;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(width, height, new ScreenOptions { Fps = 30, Kbps = 4000, KeyFrameSeconds = 1 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }   // no NVENC here
        using (encoder)
        {
            var frames = new Frames();
            var shown = new List<(int W, int H, long At)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var viewer = new ScreenViewer(frames, preview: new PreviewSize(640, 360));
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
                // Each key frame gives a picture at once (a separate FFmpeg needs the key frame twice to give it, so then there are two), not when the next key frame arrives.
                Assert.True(shown.Count >= 3, $"{shown.Count} pictures for 3 key frames");
                Assert.True(shown[0].At < 3000, $"the first picture came after {shown[0].At} ms");
            }
            Assert.True(deltas > 50);
            Assert.Equal(3, viewer.FramesIn);   // only the key frames were taken in
            Assert.True(viewer.DecoderCpu > TimeSpan.Zero, "the decoder's processor time is counted");
        }
    }
}
