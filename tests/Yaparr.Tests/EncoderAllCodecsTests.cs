using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>
/// Every codec and card is encoded inside the program (no FFmpeg program is run): the options each encoder is given, VP8 in software, the probe that tells what a card
/// can do, and a real share in VP8. Those that need FFmpeg's libraries, a card or a screen do nothing where there is none.
/// </summary>
public class EncoderAllCodecsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Each_encoder_gets_low_latency_no_b_frames_and_key_frames_a_viewer_can_start_from()
    {
        string Of(ScreenCodec codec, HardwareEncoder? card) => string.Join(' ', InProcessEncoder.EncoderOptions(codec, card).Select(o => o.Name + "=" + o.Value));
        // NVIDIA: the options it always had, and each packet handed back at once
        Assert.Equal(string.Join(' ', FfmpegArgs.NvencH264Options.Select(o => o.Name + "=" + o.Value)) + " delay=0", Of(ScreenCodec.H264, HardwareEncoder.Nvenc));
        Assert.Contains("usage=ultralowlatency", Of(ScreenCodec.H264, HardwareEncoder.Amf));
        Assert.Contains("forced_idr=1", Of(ScreenCodec.H264, HardwareEncoder.Amf));
        Assert.Contains("aud=1", Of(ScreenCodec.H264, HardwareEncoder.Amf));
        Assert.Contains("scenario=displayremoting", Of(ScreenCodec.H264, HardwareEncoder.Qsv));
        Assert.Contains("forced_idr=1", Of(ScreenCodec.H264, HardwareEncoder.Qsv));
        Assert.Contains("aud=1", Of(ScreenCodec.H264, HardwareEncoder.Vaapi));
        Assert.Contains("forced-idr=1", Of(ScreenCodec.Av1, HardwareEncoder.Nvenc));
        Assert.Contains("header_insertion_mode=frame", Of(ScreenCodec.Av1, HardwareEncoder.Amf));
        Assert.Contains("profile=main", Of(ScreenCodec.Av1, HardwareEncoder.Vaapi));
        // VP8 in software: realtime, nothing held back, every frame decodable on its own after a loss
        Assert.Equal("deadline=realtime cpu-used=6 lag-in-frames=0 auto-alt-ref=0 error-resilient=1 threads=4", Of(ScreenCodec.Vp8, null));
        Assert.Equal(Of(ScreenCodec.Vp8, null), Of(ScreenCodec.Vp8, HardwareEncoder.Nvenc));   // a card is not used for VP8
    }

    [Fact]
    public void Only_NVIDIAs_and_AMDs_encoders_read_the_pictures_from_the_card()
    {
        bool windows = OperatingSystem.IsWindows();
        Assert.Equal(windows, InProcessEncoder.ReadsFromCard(ScreenCodec.H264, HardwareEncoder.Nvenc));
        Assert.Equal(windows, InProcessEncoder.ReadsFromCard(ScreenCodec.Av1, HardwareEncoder.Amf));
        Assert.False(InProcessEncoder.ReadsFromCard(ScreenCodec.H264, HardwareEncoder.Qsv));
        Assert.False(InProcessEncoder.ReadsFromCard(ScreenCodec.Vp8, null));
        Assert.False(InProcessEncoder.ReadsFromCard(ScreenCodec.H264, HardwareEncoder.Vaapi));
    }

    [Fact]
    public void A_share_with_silly_numbers_or_nothing_to_share_is_refused_before_anything_is_announced()
    {
        var display = new Display(0, "primary", 0, 0, 1920, 1080, true, 0xAB);
        new ScreenOptions { Display = display, Fps = 30, Kbps = 2000 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenOptions { Display = display, Fps = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenOptions { Display = display, Fps = 61 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenOptions { Display = display, Kbps = 10 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenOptions { Display = display, MaxWidth = 8 }.Validate());
        Assert.Throws<ArgumentException>(() => new ScreenOptions { X11Display = null }.Validate());
    }

    [Fact]
    public void A_key_frame_every_two_seconds_is_the_default_because_a_viewer_that_loses_frames_asks_for_one_and_gets_it_at_once()
        => Assert.Equal(EncoderTuning.DefaultKeySeconds, new ScreenOptions().KeyFrameSeconds, 3);

    [Fact]
    public void VP8_is_encoded_here_in_software_and_decodes_with_a_key_frame_when_asked()
    {
        if (!FfmpegLibraries.IsAvailable || !InProcessDecoder.IsAvailable(ScreenCodec.Vp8)) return;
        const int width = 640, height = 360;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(width, height, new ScreenOptions { Fps = 30, Kbps = 1000, KeyFrameSeconds = 10, X11Display = null }, null, ScreenCodec.Vp8, null); }
        catch (PlatformNotSupportedException) { return; }   // libraries without libvpx
        using (encoder)
        using (var decoder = new InProcessDecoder(ScreenCodec.Vp8))
        {
            Assert.Null(encoder.Card);
            Assert.False(encoder.LiveBitrate);
            Assert.StartsWith("libvpx 640x360", encoder.Describe());
            var picture = new byte[encoder.PictureBytes];
            var keys = new List<int>();
            int pictures = 0;
            for (int n = 0; n < 30; n++)
            {
                for (int i = 0; i < width * height; i++) picture[i] = (byte)((i % width) / 3 + n * 4);
                Array.Fill(picture, (byte)128, width * height, picture.Length - width * height);
                encoder.Encode(picture, forceKey: n == 20, (frame, isKey) =>
                {
                    if (isKey) { Assert.True(ScreenCodecs.IsKeyFrame(ScreenCodec.Vp8, frame)); keys.Add(n); }
                    Assert.True(decoder.Decode(frame, 0, 0, (_, w, h) => { Assert.Equal((width, height), (w, h)); pictures++; }), decoder.LastError);
                });
            }
            Assert.Equal(new[] { 0, 20 }, keys);   // the first, and the one asked for (the spacing is ten seconds)
            Assert.Equal(30, pictures);
        }
    }

    [Fact]
    public void The_probe_says_whether_a_card_can_encode_and_why_not()
    {
        if (!FfmpegLibraries.IsAvailable) return;
        bool h264 = InProcessEncoder.Probe(ScreenCodec.H264, HardwareEncoder.Nvenc, out var why);
        output.WriteLine($"H.264 on NVIDIA: {(h264 ? "yes" : why)}");
        if (!h264) Assert.NotEmpty(why);
        bool av1 = InProcessEncoder.Probe(ScreenCodec.Av1, HardwareEncoder.Nvenc, out var whyAv1);
        output.WriteLine($"AV1 on NVIDIA: {(av1 ? "yes" : whyAv1)}");
        if (!av1) Assert.NotEmpty(whyAv1);
        var report = Av1Support.Check();
        output.WriteLine("AV1 check: " + report.Describe());
        Assert.Equal(HardwareEncoders.InOrder.Count, report.Encoders.Count + report.Refused.Count);
    }

    [Fact]
    public async Task A_real_share_in_VP8_is_captured_and_encoded_in_this_program()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;
        var options = new ScreenOptions { Display = display, X11Display = null, Fps = 30, Kbps = 1000, KeyFrameSeconds = 1, MaxWidth = 640, MaxHeight = 360, Codec = ScreenCodec.Vp8 };
        using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen", options);
        try { await TestUtil.WaitFor(() => sharer.Frames >= 60, 30_000); }
        catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
        output.WriteLine(sharer.Describe());
        Assert.Equal(ScreenCodec.Vp8, sharer.Codec);
        Assert.Null(sharer.Encoder);
        Assert.StartsWith("in this program: libvpx", sharer.EncoderDetails);
        Assert.False(sharer.CaptureOnCard);
        Assert.True(sharer.Figures().Keys >= 2, "a key frame every second");
    }
}
