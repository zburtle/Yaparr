using Yaparr.AppCore;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class H264FramePackerTests
{
    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    // A parameter set pair for a 64x48 baseline stream, written bit by bit as the standard lays it out (7.3.2.1.1 and 7.3.2.2).
    private static readonly byte[] Sps = Nal(0x67, w =>
    {
        w.Bits(66, 8); w.Bits(0xC0, 8); w.Bits(10, 8);       // baseline, constraint flags, level 1.0
        w.Ue(0);                                             // parameter set id
        w.Ue(0);                                             // log2_max_frame_num_minus4
        w.Ue(2);                                             // pic_order_cnt_type
        w.Ue(1);                                             // max_num_ref_frames
        w.Bits(0, 1);                                        // gaps_in_frame_num_allowed
        w.Ue(3); w.Ue(2);                                    // 4 x 3 macroblocks: 64 x 48
        w.Bits(1, 1); w.Bits(1, 1);                          // frame_mbs_only, direct_8x8_inference
        w.Bits(0, 1); w.Bits(0, 1);                          // no cropping, no VUI
    });
    private static readonly byte[] Pps = Nal(0x68, w => { w.Ue(0); w.Ue(0); w.Bits(0, 1); w.Bits(0, 1); w.Ue(0); w.Ue(0); w.Ue(0); w.Bits(0, 1); w.Bits(0, 2); w.Bits(0, 1); w.Bits(0, 1); w.Bits(0, 1); });
    private static readonly byte[] Idr = { 0x65, 0x88, 0x84, 0x00, 0x10, 0xFF, 0xFE, 0xF6, 0xF0, 0xFE, 0x05, 0x36, 0x56, 0x04, 0x50, 0x96 };
    private static readonly byte[] Slice = { 0x41, 0x9A, 0x24, 0x6C, 0x41, 0x4F, 0xFE, 0xD6 };

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _used;   // bits used in the last byte
        public void Bits(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_used == 0) _bytes.Add(0);
                if (((value >> i) & 1) == 1) _bytes[^1] |= (byte)(0x80 >> _used);
                _used = (_used + 1) % 8;
            }
        }
        public void Ue(int value)
        {
            int v = value + 1, length = 0;
            while ((v >> length) > 1) length++;
            Bits(0, length);
            Bits(v, length + 1);
        }
        public byte[] Finish() { Bits(1, 1); while (_used != 0) Bits(0, 1); return _bytes.ToArray(); }
    }

    // A NAL unit: its header byte and the data, with a zero byte of emulation prevention wherever 00 00 would be followed by 0..3.
    private static byte[] Nal(byte header, Action<BitWriter> write)
    {
        var w = new BitWriter();
        write(w);
        var result = new List<byte> { header };
        int zeros = 0;
        foreach (var b in w.Finish())
        {
            if (zeros >= 2 && b <= 3) { result.Add(3); zeros = 0; }
            result.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }

    private static byte[] Unit(params byte[][] nals) => nals.SelectMany(n => StartCode.Concat(n)).ToArray();

    [Fact]
    public void A_key_frame_gets_the_parameter_sets_the_encoder_gave_once_and_a_viewer_can_start_from_it()
    {
        var packer = new H264FramePacker();
        packer.SetConfig(Unit(Sps, Pps));
        Assert.True(packer.HasConfig);

        var frame = packer.Pack(Unit(Idr), isKey: true)!;
        Assert.True(H264.IsKeyFrame(frame));
        Assert.True(ScreenCodecs.TryGetKeyFrame(frame, out var codec, out var width, out var height));
        Assert.Equal(ScreenCodec.H264, codec);
        Assert.Equal(64, width);
        Assert.Equal(48, height);
    }

    [Fact]
    public void A_frame_between_key_frames_is_sent_as_it_is()
    {
        var packer = new H264FramePacker();
        packer.SetConfig(Unit(Sps, Pps));
        var slice = Unit(Slice);
        Assert.Equal(slice, packer.Pack(slice, isKey: false));
        Assert.False(H264.IsKeyFrame(slice));
    }

    [Fact]
    public void A_key_frame_that_already_has_them_is_not_given_them_twice()
    {
        var packer = new H264FramePacker();
        packer.SetConfig(Unit(Sps, Pps));
        var own = Unit(Sps, Pps, Idr);
        Assert.Equal(own, packer.Pack(own, isKey: true));
    }

    [Fact]
    public void A_key_frame_before_the_encoder_has_said_what_it_makes_cannot_be_sent()
    {
        var packer = new H264FramePacker();
        Assert.False(packer.HasConfig);
        Assert.Null(packer.Pack(Unit(Idr), isKey: true));
        Assert.Null(packer.Pack(ReadOnlySpan<byte>.Empty, isKey: false));
        // A configuration without both parameter sets is not one.
        packer.SetConfig(Unit(Sps));
        Assert.False(packer.HasConfig);
    }

    [Theory]
    [InlineData(1080, 2400, 1280, 576, 1280)]    // a phone in portrait: the long side is the height
    [InlineData(2400, 1080, 1280, 1280, 576)]    // the same in landscape
    [InlineData(720, 1280, 1280, 720, 1280)]     // already small enough
    [InlineData(1081, 2401, 1280, 576, 1280)]    // odd sizes are made even
    public void The_encoded_size_keeps_the_shape_and_is_even(int width, int height, int max, int expectedWidth, int expectedHeight)
    {
        var (w, h) = H264FramePacker.FitSize(width, height, max);
        Assert.Equal((expectedWidth, expectedHeight), (w, h));
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Fact]
    public void An_area_that_is_nothing_gets_a_size_that_can_be_encoded()
    {
        Assert.Equal((2, 2), H264FramePacker.FitSize(0, 0, 1280));
        Assert.Equal((2, 2), H264FramePacker.FitSize(1, 1, 1280));
    }

    [Fact]
    public void The_bitrate_grows_with_the_picture_and_stays_between_its_limits()
    {
        int small = H264FramePacker.SuggestKbps(360, 640, 30);
        int large = H264FramePacker.SuggestKbps(720, 1280, 30);
        Assert.InRange(small, 400, large);
        Assert.True(large > small);
        Assert.Equal(1000, H264FramePacker.SuggestKbps(4000, 4000, 30, ceiling: 1000));
        Assert.Equal(400, H264FramePacker.SuggestKbps(2, 2, 30));
    }
}

public class ShareQualityTests
{
    [Fact]
    public void The_standard_quality_is_the_one_in_the_middle_and_nothing_is_known_about_the_upload_yet()
    {
        Assert.Equal(ShareQuality.All[1], ShareQuality.Standard);
        Assert.Equal(ShareQuality.Standard, ShareQuality.BestFor(null));
        Assert.Equal(ShareQuality.Standard, ShareQuality.BestFor(0));
        Assert.Equal("", ShareQuality.Standard.Hint(null));
    }

    [Fact]
    public void A_slow_connection_gets_the_lightest_and_a_fast_one_the_best_that_reaches_three_people()
    {
        Assert.Equal(ShareQuality.All[0], ShareQuality.BestFor(50_000));          // 0.4 Mbit/s up
        var fast = ShareQuality.BestFor(10_000_000);                              // 80 Mbit/s up
        Assert.Equal(ShareQuality.All[^1], fast);
        Assert.True(Yaparr.Chat.ChatNode.DirectViewersAt(10_000_000, fast.Kbps) >= 3);
    }

    [Fact]
    public void The_qualities_grow_in_size_and_bitrate_and_the_hint_says_how_many_can_be_reached()
    {
        for (int i = 1; i < ShareQuality.All.Count; i++)
        {
            Assert.True(ShareQuality.All[i].MaxLongSide > ShareQuality.All[i - 1].MaxLongSide);
            Assert.True(ShareQuality.All[i].Kbps > ShareQuality.All[i - 1].Kbps);
        }
        Assert.Contains("directly to", ShareQuality.All[0].Hint(10_000_000));
        Assert.Contains("suits sending this to 1 person", ShareQuality.All[2].Hint(150_000));
    }
}
