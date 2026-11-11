using Yaparr.Screen;

namespace Yaparr.Tests;

// AV1 screen sharing: telling an AV1 key frame (and its size) from the others without decoding it, what the encoder is asked for, and what is tried in what order.
public class Av1Tests
{
    private static readonly Display Monitor = new(0, "Monitor", 0, 0, 1920, 1080, true, 0x1234);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(640, 360)]
    public void A_key_frame_says_how_big_the_picture_is(int width, int height)
    {
        var frame = Av1.FakeKeyFrame(width, height);
        Assert.True(Av1.IsKeyFrame(frame));
        Assert.True(Av1.TryGetSize(frame, out int w, out int h));
        Assert.Equal((width, height), (w, h));
    }

    [Fact]
    public void A_unit_without_a_sequence_header_is_not_a_key_frame()
    {
        // A temporal delimiter and a frame OBU with a few bytes of payload: an ordinary picture.
        byte[] plain = { 0x12, 0x00, 0x32, 0x04, 1, 2, 3, 4 };
        Assert.False(Av1.IsKeyFrame(plain));
        Assert.False(Av1.TryGetSize(plain, out _, out _));
    }

    [Fact]
    public void The_codec_is_read_off_the_key_frame_and_never_mistaken_for_another()
    {
        Assert.True(ScreenCodecs.TryGetKeyFrame(Av1.FakeKeyFrame(1920, 1080), out var codec, out int w, out int h));
        Assert.Equal((ScreenCodec.Av1, 1920, 1080), (codec, w, h));
        Assert.True(ScreenCodecs.TryGetKeyFrame(Vp8.FakeKeyFrame(1280, 720), out codec, out w, out h));
        Assert.Equal((ScreenCodec.Vp8, 1280, 720), (codec, w, h));
        // An ordinary AV1 picture says nothing about size, so a viewer cannot start on it.
        Assert.False(ScreenCodecs.TryGetKeyFrame(new byte[] { 0x12, 0x00, 0x32, 0x04, 1, 2, 3, 4 }, out _, out _, out _));
        Assert.True(ScreenCodecs.IsKeyFrame(ScreenCodec.Av1, Av1.FakeKeyFrame(1280, 720)));
        Assert.False(ScreenCodecs.IsKeyFrame(ScreenCodec.Av1, Vp8.FakeKeyFrame(1280, 720)));
    }

    [Fact]
    public void Damaged_data_is_never_a_key_frame_and_never_throws()
    {
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[random.Next(0, 80)];
            random.NextBytes(junk);
            if (junk.Length > 1 && i % 2 == 0) { junk[0] = 0x12; junk[1] = 0x00; }
            _ = Av1.IsKeyFrame(junk);
            _ = Av1.TryGetSize(junk, out _, out _);
            _ = ScreenCodecs.TryGetKeyFrame(junk, out _, out _, out _);
        }
        // A sequence header cut short.
        var cut = Av1.FakeKeyFrame(1920, 1080)[..6];
        Assert.False(Av1.TryGetSize(cut, out _, out _));
    }

    [Fact]
    public void Av1_is_asked_for_on_the_graphics_card_and_written_as_ivf()
    {
        var options = new ScreenOptions { X11Display = null, Display = Monitor, Fps = 30, Kbps = 4000 };
        string nvidia = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Av1, HardwareEncoder.Nvenc));
        Assert.Contains("-c:v av1_nvenc", nvidia);
        Assert.Contains("-bf 0", nvidia);
        Assert.Contains("-g 30", nvidia);
        Assert.EndsWith("-f ivf pipe:1", nvidia);
        Assert.DoesNotContain("libvpx", nvidia);

        Assert.Contains("-c:v av1_amf", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Av1, HardwareEncoder.Amf)));
        string intel = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Av1, HardwareEncoder.Qsv));
        Assert.Contains("-c:v av1_qsv", intel);
        Assert.Contains("format=nv12", intel);   // Intel's encoder takes NV12
        Assert.Equal("av1_vaapi", HardwareEncoders.Name(ScreenCodec.Av1, HardwareEncoder.Vaapi));
        Assert.Equal("h264_nvenc", HardwareEncoders.Name(HardwareEncoder.Nvenc));
    }

    [Fact]
    public void Av1_on_vaapi_names_the_render_device_and_uploads_the_picture_to_it()
    {
        var options = new ScreenOptions { X11Display = ":0", Display = Monitor };
        string line = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Av1, HardwareEncoder.Vaapi));
        Assert.Contains("-vaapi_device", line);
        Assert.Contains("nv12,hwupload", line);
        Assert.Contains("-c:v av1_vaapi", line);
    }

    [Fact]
    public void The_viewer_decodes_av1_from_ivf()
    {
        Assert.Contains("-f ivf -i pipe:0", string.Join(' ', FfmpegArgs.DecodeForViewer(ScreenCodec.Av1, gpu: true)));
        Assert.DoesNotContain("d3d11va", string.Join(' ', FfmpegArgs.DecodeForViewer(ScreenCodec.Av1, gpu: true)));
        Assert.Contains("-f ivf -i pipe:0", string.Join(' ', FfmpegArgs.Decode(ScreenCodec.Av1)));
    }

    [Fact]
    public void Auto_never_picks_av1_and_av1_falls_back_to_what_auto_does()
    {
        Assert.DoesNotContain(ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor }), p => p.Codec == ScreenCodec.Av1);

        var plan = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.Av1 });
        var cards = HardwareEncoders.InOrder;
        Assert.Equal(cards.Select(c => (ScreenCodec.Av1, (HardwareEncoder?)c))
            .Concat(cards.Select(c => (ScreenCodec.H264, (HardwareEncoder?)c)))
            .Append((ScreenCodec.Vp8, (HardwareEncoder?)null)), plan);

        var one = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.Av1, Encoder = HardwareEncoder.Amf });
        Assert.Equal(new (ScreenCodec, HardwareEncoder?)[] { (ScreenCodec.Av1, HardwareEncoder.Amf), (ScreenCodec.H264, HardwareEncoder.Amf), (ScreenCodec.Vp8, null) }, one);
    }

    [Fact]
    public void A_share_says_which_codec_and_card_it_uses()
    {
        Assert.Equal("AV1 on the NVIDIA graphics card", ScreenCodecs.Describe(ScreenCodec.Av1, HardwareEncoder.Nvenc));
        Assert.Equal("AV1 on the graphics card", ScreenCodecs.Describe(ScreenCodec.Av1));
        Assert.Equal("H.264 on the AMD graphics card", ScreenCodecs.Describe(ScreenCodec.H264, HardwareEncoder.Amf));
    }

    [Fact]
    public void The_choice_of_av1_stays_when_another_monitor_is_shared()
    {
        var options = new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.Av1 };
        Assert.Equal(ScreenCodec.Av1, options.Retarget(Monitor, null).Codec);
    }

    [Fact]
    public void The_check_asks_each_card_for_a_tiny_av1_picture_in_ivf()
    {
        string nvidia = string.Join(' ', FfmpegArgs.ProbeEncoder(ScreenCodec.Av1, HardwareEncoder.Nvenc));
        Assert.Contains("-c:v av1_nvenc", nvidia);
        Assert.Contains("-f lavfi", nvidia);
        Assert.EndsWith("-f ivf pipe:1", nvidia);
        string vaapi = string.Join(' ', FfmpegArgs.ProbeEncoder(ScreenCodec.Av1, HardwareEncoder.Vaapi));
        Assert.Contains("-vaapi_device", vaapi);
        Assert.Contains("nv12,hwupload", vaapi);
    }

    [Fact]
    public void The_report_says_plainly_what_works_and_what_does_not()
    {
        var none = new Av1Report(Array.Empty<HardwareEncoder>(), new Dictionary<HardwareEncoder, string> { [HardwareEncoder.Nvenc] = "no device" }, false, null);
        Assert.False(none.CanEncode);
        Assert.Equal("encoding: no graphics card here does it (not usable: NVIDIA: no device); decoding: FFmpeg here has no AV1 decoder", none.Describe());
        var some = new Av1Report(new[] { HardwareEncoder.Amf }, new Dictionary<HardwareEncoder, string>(), true, "libdav1d");
        Assert.True(some.CanEncode);
        Assert.Equal("encoding: AMD; decoding: libdav1d", some.Describe());
    }

    [Fact]
    public async Task The_check_runs_against_the_real_ffmpeg_and_gives_an_answer_for_every_card()
    {
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        var report = await Av1Support.CheckAsync(ffmpeg);
        // Every card is either usable or has a reason it is not.
        foreach (var card in HardwareEncoders.InOrder) Assert.True(report.Encoders.Contains(card) || report.Refused.ContainsKey(card));
    }

    [Fact]
    public void The_video_format_picked_last_time_is_remembered()
    {
        Assert.False(new Yaparr.AppCore.AppSettings().ScreenShareAv1);
        var saved = System.Text.Json.JsonSerializer.Serialize(new Yaparr.AppCore.AppSettings { ScreenShareAv1 = true });
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<Yaparr.AppCore.AppSettings>(saved)!.ScreenShareAv1);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<Yaparr.AppCore.AppSettings>("{\"DisplayName\":\"Zed\"}")!.ScreenShareAv1);
    }
}
