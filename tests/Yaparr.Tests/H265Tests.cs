using Yaparr.Screen;

namespace Yaparr.Tests;

// H.265 screen sharing: telling an H.265 key frame (and its size) from the others without decoding it, what the encoders are asked for, what is tried in what order, and the
// switch that turns the whole thing off for a release (ScreenFeatures.H265).
public class H265Tests
{
    private static readonly Display Monitor = new(0, "Monitor", 0, 0, 1920, 1080, true, 0x1234);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(640, 360)]
    [InlineData(1366, 768)]
    [InlineData(1000, 562)]
    public void A_key_frame_says_how_big_the_picture_is_with_the_cropping_applied(int width, int height)
    {
        var frame = H265.FakeKeyFrame(width, height);
        Assert.True(H265.IsKeyFrame(frame));
        Assert.True(H265.TryGetSize(frame, out int w, out int h));
        Assert.Equal((width, height), (w, h));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void Temporal_layers_in_the_parameter_set_are_skipped_exactly(int subLayers)
    {
        var frame = H265.FakeKeyFrame(1920, 1080, maxSubLayersMinus1: subLayers);
        Assert.True(H265.TryGetSize(frame, out int w, out int h));
        Assert.Equal((1920, 1080), (w, h));
    }

    [Fact]
    public void A_key_frame_needs_the_three_parameter_sets_and_a_picture_that_starts_a_sequence()
    {
        var full = H265.FakeKeyFrame(1280, 720);
        Assert.True(H265.IsKeyFrame(full));

        // Without a parameter set, or without the picture, a viewer who joined could not start on it.
        foreach (var missing in new[] { 32, 33, 34 })
            Assert.False(H265.IsKeyFrame(Without(full, missing)), $"without NAL type {missing}");
        Assert.False(H265.IsKeyFrame(Without(full, 19)));
        Assert.False(H265.IsKeyFrame(H265.FakeDeltaFrame()));

        // A clean random access picture (CRA, 21) and a broken link one (BLA, 16..18) start a sequence as well as an IDR (19, 20).
        foreach (var type in new[] { 16, 17, 18, 19, 20, 21 })
            Assert.True(H265.IsKeyFrame(WithPictureType(full, type)), $"type {type}");
        foreach (var type in new[] { 0, 1, 9, 22 })
            Assert.False(H265.IsKeyFrame(WithPictureType(full, type)), $"type {type}");
    }

    // The frame with every NAL unit of one type taken out (types are read from the two byte headers).
    private static byte[] Without(byte[] frame, int type)
    {
        var result = new List<byte>();
        int at = 0;
        foreach (var (t, start, end) in H265.Units(frame))
        {
            int begin = start - 4 >= 0 && frame[start - 4] == 0 ? start - 4 : start - 3;
            if (t != type) result.AddRange(frame.AsSpan(Math.Max(at, begin), end - Math.Max(at, begin)).ToArray());
            at = end;
        }
        return result.ToArray();
    }

    private static byte[] WithPictureType(byte[] frame, int type)
    {
        var copy = (byte[])frame.Clone();
        foreach (var (t, start, _) in H265.Units(copy))
            if (t == 19) copy[start] = (byte)(type << 1);
        return copy;
    }

    [Fact]
    public void Another_layer_or_a_damaged_header_is_not_the_video()
    {
        var frame = H265.FakeKeyFrame(1280, 720);
        foreach (var (type, start, _) in H265.Units(frame))
        {
            if (type != 33) continue;
            var otherLayer = (byte[])frame.Clone();
            otherLayer[start + 1] = 0x09;           // nuh_layer_id 1
            Assert.False(H265.IsKeyFrame(otherLayer));
            var forbidden = (byte[])frame.Clone();
            forbidden[start] |= 0x80;               // forbidden_zero_bit
            Assert.False(H265.IsKeyFrame(forbidden));
            var noTemporalId = (byte[])frame.Clone();
            noTemporalId[start + 1] = 0x00;         // nuh_temporal_id_plus1 must be at least 1
            Assert.False(H265.IsKeyFrame(noTemporalId));
        }
    }

    [Fact]
    public void The_codec_is_read_off_the_key_frame_and_never_mistaken_for_another()
    {
        // With H.265 on.
        Assert.True(ScreenCodecs.TryGetKeyFrame(H265.FakeKeyFrame(1920, 1080), true, out var codec, out int w, out int h));
        Assert.Equal((ScreenCodec.H265, 1920, 1080), (codec, w, h));
        Assert.True(ScreenCodecs.TryGetKeyFrame(H264.FakeKeyFrame(1920, 1080), true, out codec, out w, out h));
        Assert.Equal((ScreenCodec.H264, 1920, 1080), (codec, w, h));
        Assert.True(ScreenCodecs.TryGetKeyFrame(Vp8.FakeKeyFrame(1280, 720), true, out codec, out w, out h));
        Assert.Equal((ScreenCodec.Vp8, 1280, 720), (codec, w, h));
        Assert.True(ScreenCodecs.TryGetKeyFrame(Av1.FakeKeyFrame(1280, 720), true, out codec, out w, out h));
        Assert.Equal((ScreenCodec.Av1, 1280, 720), (codec, w, h));

        // Neither of the two start code codecs reads the other as its own.
        Assert.False(H264.IsKeyFrame(H265.FakeKeyFrame(1920, 1080)));
        Assert.False(H265.IsKeyFrame(H264.FakeKeyFrame(1920, 1080)));
        Assert.False(H265.IsKeyFrame(Vp8.FakeKeyFrame(1280, 720)));
        Assert.False(H265.IsKeyFrame(Av1.FakeKeyFrame(1280, 720)));
        Assert.False(H265.TryGetSize(H264.FakeKeyFrame(1920, 1080), out _, out _));
    }

    [Fact]
    public void With_the_switch_off_an_h265_frame_is_not_recognised_but_everything_else_still_is()
    {
        var frame = H265.FakeKeyFrame(1920, 1080);
        Assert.False(ScreenCodecs.TryGetKeyFrame(frame, false, out var codec, out _, out _));
        Assert.Equal(ScreenCodec.Auto, codec);
        Assert.True(H265.LooksLikeKeyFrame(frame));           // so that a viewer can still say what it is that it cannot play
        Assert.True(ScreenCodecs.TryGetKeyFrame(H264.FakeKeyFrame(1920, 1080), false, out codec, out _, out _));
        Assert.Equal(ScreenCodec.H264, codec);
        Assert.True(ScreenCodecs.TryGetKeyFrame(Av1.FakeKeyFrame(1280, 720), false, out codec, out _, out _));
        Assert.Equal(ScreenCodec.Av1, codec);
    }

    [Fact]
    public void The_switch_decides_what_the_build_may_use()
    {
        Assert.True(ScreenFeatures.Allows(ScreenCodec.H264));
        Assert.True(ScreenFeatures.Allows(ScreenCodec.Vp8));
        Assert.True(ScreenFeatures.Allows(ScreenCodec.Av1));
        Assert.Equal(ScreenFeatures.H265, ScreenFeatures.Allows(ScreenCodec.H265));
        Assert.Equal(ScreenFeatures.H265, ScreenCodecs.IsKeyFrame(ScreenCodec.H265, H265.FakeKeyFrame(1280, 720)));
        if (!ScreenFeatures.H265)
        {
            Assert.False(InProcessDecoder.IsAvailable(ScreenCodec.H265));
            Assert.Null(InProcessDecoder.DecoderName(ScreenCodec.H265));
            Assert.Throws<PlatformNotSupportedException>(() => new InProcessDecoder(ScreenCodec.H265));
            Assert.Throws<PlatformNotSupportedException>(() => new InProcessEncoder(640, 360, new ScreenOptions(), null, ScreenCodec.H265, HardwareEncoder.Nvenc));
            Assert.False(InProcessEncoder.Probe(ScreenCodec.H265, HardwareEncoder.Nvenc, out var why));
            Assert.Contains("switched off", why);
        }
    }

    [Fact]
    public void Damaged_data_is_never_a_key_frame_and_never_throws()
    {
        var random = new Random(11);
        var real = H265.FakeKeyFrame(1920, 1080);
        for (int i = 0; i < 3000; i++)
        {
            var junk = new byte[random.Next(0, 120)];
            random.NextBytes(junk);
            if (junk.Length > 8 && i % 3 == 0) { junk[0] = 0; junk[1] = 0; junk[2] = 1; junk[3] = 0x42; junk[4] = 0x01; }
            _ = H265.IsKeyFrame(junk);
            _ = H265.TryGetSize(junk, out _, out _);
            _ = ScreenCodecs.TryGetKeyFrame(junk, true, out _, out _, out _);
            // A real frame with a few bytes damaged.
            var hurt = (byte[])real.Clone();
            hurt[random.Next(0, hurt.Length)] ^= (byte)random.Next(1, 256);
            _ = H265.IsKeyFrame(hurt);
            _ = H265.TryGetSize(hurt, out _, out _);
        }
        // A parameter set cut short, and sizes that cannot be.
        Assert.False(H265.TryGetSize(real[..20], out _, out _));
        Assert.False(H265.TryGetSize(Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void A_size_the_parameter_set_gives_is_held_to_what_a_picture_can_be()
    {
        // 1x1 with no cropping is under the size of a coded block of 8, and a huge one is refused rather than allocated.
        Assert.False(H265.TryGetSize(H265.FakeKeyFrame(16, 16)[..0], out _, out _));
        Assert.True(H265.TryGetSize(H265.FakeKeyFrame(16, 16), out int w, out int h));
        Assert.Equal((16, 16), (w, h));
        Assert.False(H265.TryGetSize(H265.FakeKeyFrame(20000, 20000), out _, out _));
    }

    // ---- what the encoders are asked for

    [Fact]
    public void Every_graphics_card_has_options_for_h265_and_its_encoder_is_the_hevc_one()
    {
        foreach (var card in Enum.GetValues<HardwareEncoder>())
        {
            Assert.NotEmpty(InProcessEncoder.EncoderOptions(ScreenCodec.H265, card));
            Assert.Equal($"hevc_{card.ToString().ToLowerInvariant()}", HardwareEncoders.Name(ScreenCodec.H265, card));
        }
        // Low latency for all of them, main profile where the encoder takes one, a key frame on request, and an access unit delimiter before each picture.
        var nvenc = InProcessEncoder.EncoderOptions(ScreenCodec.H265, HardwareEncoder.Nvenc);
        Assert.Contains(("tune", "ll"), nvenc);
        Assert.Contains(("forced-idr", "1"), nvenc);
        Assert.Contains(("aud", "1"), nvenc);
        Assert.Contains(("profile", "main"), nvenc);
        Assert.Contains(("usage", "ultralowlatency"), InProcessEncoder.EncoderOptions(ScreenCodec.H265, HardwareEncoder.Amf));
        Assert.Contains(("scenario", "displayremoting"), InProcessEncoder.EncoderOptions(ScreenCodec.H265, HardwareEncoder.Qsv));
        // The names stay what they were for the other codecs.
        Assert.Equal("h264_nvenc", HardwareEncoders.Name(ScreenCodec.H264, HardwareEncoder.Nvenc));
        Assert.Equal("av1_qsv", HardwareEncoders.Name(ScreenCodec.Av1, HardwareEncoder.Qsv));
        Assert.Equal("nv12", HardwareEncoders.PixelFormat(ScreenCodec.H265, HardwareEncoder.Qsv));
        Assert.Equal("nv12,hwupload", HardwareEncoders.PixelFormat(ScreenCodec.H265, HardwareEncoder.Vaapi));
    }

    [Fact]
    public void There_is_no_software_encoder_for_h265()
    {
        // Software is VP8 only: asked for a card the encoder cannot be made without one.
        foreach (var codec in new[] { ScreenCodec.H265 })
            Assert.All(Enum.GetValues<HardwareEncoder>(), card => Assert.DoesNotContain("libx265", HardwareEncoders.Name(codec, card)));
        var plan = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.H265 }, new[] { GpuVendor.Nvidia }, adaptersGiven: true, h265: true);
        Assert.DoesNotContain(plan, p => p.Codec == ScreenCodec.H265 && p.Encoder == null);
    }

    // ---- what is tried, and in what order

    [Fact]
    public void Auto_tries_h265_first_and_falls_back_to_h264_then_vp8()
    {
        var cards = HardwareEncoders.InOrder;
        var all = new[] { GpuVendor.Nvidia, GpuVendor.Amd, GpuVendor.Intel };
        var expected = cards.Select(c => (ScreenCodec.H265, (HardwareEncoder?)c))
            .Concat(cards.Select(c => (ScreenCodec.H264, (HardwareEncoder?)c)))
            .Append((ScreenCodec.Vp8, (HardwareEncoder?)null)).ToList();
        Assert.Equal(expected, ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor }, all, adaptersGiven: true, h265: true));
        // Asked for on purpose it is the same list.
        Assert.Equal(expected, ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.H265 }, all, adaptersGiven: true, h265: true));

        var one = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.H265, Encoder = HardwareEncoder.Amf }, h265: true);
        Assert.Equal(new (ScreenCodec, HardwareEncoder?)[] { (ScreenCodec.H265, HardwareEncoder.Amf), (ScreenCodec.H264, HardwareEncoder.Amf), (ScreenCodec.Vp8, null) }, one);
        // H.264 or VP8 asked for: no H.265.
        Assert.DoesNotContain(ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.H264 }, h265: true), p => p.Codec == ScreenCodec.H265);
        Assert.DoesNotContain(ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.Vp8 }, h265: true), p => p.Codec == ScreenCodec.H265);
    }

    [Fact]
    public void With_the_switch_off_a_share_that_asks_for_h265_is_planned_as_h264()
    {
        var asked = new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.H265 };
        var off = ScreenSharer.Plan(asked, new[] { GpuVendor.Nvidia, GpuVendor.Amd, GpuVendor.Intel }, adaptersGiven: true, h265: false);
        Assert.DoesNotContain(ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor }, h265: false), p => p.Codec == ScreenCodec.H265);
        Assert.DoesNotContain(off, p => p.Codec == ScreenCodec.H265);
        var cards = HardwareEncoders.InOrder;
        Assert.Equal(cards.Select(c => (ScreenCodec.H264, (HardwareEncoder?)c)).Append((ScreenCodec.Vp8, (HardwareEncoder?)null)), off);
        // And for AV1 the switch changes nothing.
        var av1 = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.Av1 }, new[] { GpuVendor.Nvidia }, adaptersGiven: true, h265: false);
        Assert.Equal(ScreenCodec.Av1, av1[0].Codec);
    }

    [Fact]
    public void A_share_says_which_codec_and_card_it_uses()
    {
        Assert.Equal("H.265 on the NVIDIA graphics card", ScreenCodecs.Describe(ScreenCodec.H265, HardwareEncoder.Nvenc));
        Assert.Equal("H.265 on the AMD graphics card", ScreenCodecs.Describe(ScreenCodec.H265, HardwareEncoder.Amf));
        Assert.Equal("H.265 on the graphics card", ScreenCodecs.Describe(ScreenCodec.H265));
        Assert.Equal("H.264 on the Intel graphics card", ScreenCodecs.Describe(ScreenCodec.H264, HardwareEncoder.Qsv));
        Assert.Equal("AV1 on the NVIDIA graphics card", ScreenCodecs.Describe(ScreenCodec.Av1, HardwareEncoder.Nvenc));
        Assert.Equal("H.265", ScreenCodecs.Name(ScreenCodec.H265));
        Assert.Equal("H.264", ScreenCodecs.Name(ScreenCodec.H264));
    }

    [Fact]
    public void The_check_says_what_this_computer_can_do_in_one_line()
    {
        var report = new H265Report(new[] { HardwareEncoder.Nvenc }, new Dictionary<HardwareEncoder, string> { [HardwareEncoder.Amf] = "no such card" }, true, "hevc");
        Assert.True(report.CanEncode);
        Assert.Equal("encoding: NVIDIA (not usable: AMD: no such card); decoding: hevc", report.Describe());
        var none = new H265Report(Array.Empty<HardwareEncoder>(), new Dictionary<HardwareEncoder, string>(), false, null);
        Assert.False(none.CanEncode);
        Assert.Contains("no graphics card here does it", none.Describe());
        Assert.Contains("no H.265 decoder", none.Describe());
    }

    [Fact]
    public void The_codec_numbers_the_settings_and_other_code_know_are_not_moved()
    {
        // H265 was added at the end so that a number somebody kept (a saved choice, a log) still means what it did.
        Assert.Equal(0, (int)ScreenCodec.Auto);
        Assert.Equal(1, (int)ScreenCodec.Vp8);
        Assert.Equal(2, (int)ScreenCodec.H264);
        Assert.Equal(3, (int)ScreenCodec.Av1);
        Assert.Equal(4, (int)ScreenCodec.H265);
    }
}
