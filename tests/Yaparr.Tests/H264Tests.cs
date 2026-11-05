using Yaparr.Screen;

namespace Yaparr.Tests;

public class H264Tests
{
    // Hands out a few bytes per read, the way a pipe does.
    private sealed class TrickleStream : MemoryStream
    {
        private readonly int _chunk;
        public TrickleStream(byte[] data, int chunk) : base(data) => _chunk = chunk;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunk)], ct);
    }

    private static async Task<List<byte[]>> ReadAll(Stream stream)
    {
        var reader = new H264Reader(stream);
        var units = new List<byte[]>();
        while (await reader.ReadAsync() is { } unit) units.Add(unit);
        return units;
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)] // 1088 coded rows, cropped to 1080
    [InlineData(1138, 640)]
    [InlineData(2560, 1440)]
    [InlineData(642, 362)]
    public void A_key_frame_tells_the_size_of_its_pictures(int width, int height)
    {
        var key = H264.FakeKeyFrame(width, height, 200);
        Assert.True(H264.IsKeyFrame(key));
        Assert.True(H264.TryGetSize(key, out int w, out int h));
        Assert.Equal((width, height), (w, h));
    }

    [Fact]
    public void Only_a_picture_that_stands_alone_is_a_key_frame()
    {
        Assert.False(H264.IsKeyFrame(H264.FakeDeltaFrame()));
        Assert.False(H264.TryGetSize(H264.FakeDeltaFrame(), out _, out _));
        Assert.False(H264.IsKeyFrame(Array.Empty<byte>()));
        Assert.False(H264.IsKeyFrame(new byte[] { 0, 0, 1 }));

        // An IDR picture without the parameter sets is no use to someone who has just joined.
        var key = H264.FakeKeyFrame(1280, 720);
        var bare = new byte[] { 0, 0, 0, 1, 9, 0xF0, 0, 0, 0, 1, 0x65, 0x55, 0x55 };
        Assert.False(H264.IsKeyFrame(bare));
        Assert.True(H264.IsKeyFrame(key));
    }

    [Fact]
    public void A_damaged_parameter_set_gives_no_size_instead_of_a_wrong_one()
    {
        var key = H264.FakeKeyFrame(1280, 720);
        var cut = key.Take(14).ToArray(); // the parameter set stops after a few bytes
        Assert.False(H264.TryGetSize(cut, out _, out _));
    }

    [Fact]
    public async Task The_reader_cuts_a_stream_at_the_delimiters_however_the_bytes_arrive()
    {
        var units = new[] { H264.FakeKeyFrame(1280, 720, 5000), H264.FakeDeltaFrame(300), H264.FakeDeltaFrame(40), H264.FakeKeyFrame(1280, 720, 70_000), H264.FakeDeltaFrame(100) };
        var stream = units.SelectMany(u => u).ToArray();

        foreach (int chunk in new[] { 1, 7, 100, 4096, 1 << 20 })
        {
            var read = await ReadAll(new TrickleStream(stream, chunk));
            Assert.Equal(units.Length, read.Count);
            for (int i = 0; i < units.Length; i++) Assert.Equal(units[i], read[i]);
        }
    }

    [Fact]
    public async Task The_reader_skips_what_comes_before_the_first_delimiter_and_takes_short_start_codes()
    {
        var unit1 = new byte[] { 0, 0, 1, 9, 0x10, 0, 0, 1, 0x41, 1, 2, 3 };         // three-byte start codes
        var unit2 = new byte[] { 0, 0, 0, 1, 9, 0x10, 0, 0, 0, 1, 0x41, 4, 5, 6 };    // four-byte
        var junk = new byte[] { 7, 7, 0, 0, 7 };
        var read = await ReadAll(new TrickleStream(junk.Concat(unit1).Concat(unit2).ToArray(), 3));
        Assert.Equal(2, read.Count);
        Assert.Equal(unit1, read[0]);
        Assert.Equal(unit2, read[1]);

        Assert.Empty(await ReadAll(new MemoryStream(junk)));
        Assert.Empty(await ReadAll(new MemoryStream()));
    }

    [Fact]
    public async Task The_reader_refuses_a_unit_that_never_ends()
    {
        var stream = new byte[] { 0, 0, 0, 1, 9, 0x10 }.Concat(new byte[Ivf.MaxFrameBytes + 1000].Select(_ => (byte)0x55)).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(new MemoryStream(stream)));
    }

    [Fact]
    public void The_codec_is_read_from_the_key_frame()
    {
        Assert.True(ScreenCodecs.TryGetKeyFrame(Vp8.FakeKeyFrame(1280, 720), out var codec, out int w, out int h));
        Assert.Equal((ScreenCodec.Vp8, 1280, 720), (codec, w, h));

        Assert.True(ScreenCodecs.TryGetKeyFrame(H264.FakeKeyFrame(1138, 640), out codec, out w, out h));
        Assert.Equal((ScreenCodec.H264, 1138, 640), (codec, w, h));

        Assert.False(ScreenCodecs.TryGetKeyFrame(H264.FakeDeltaFrame(), out _, out _, out _));
        Assert.False(ScreenCodecs.TryGetKeyFrame(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, out _, out _, out _));
        Assert.False(Vp8.IsKeyFrame(H264.FakeKeyFrame(1280, 720)));
        Assert.False(H264.IsKeyFrame(Vp8.FakeKeyFrame(1280, 720)));
    }

    [Fact]
    public void The_graphics_card_command_makes_a_low_latency_stream_of_whole_pictures()
    {
        var display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);
        string line = string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { X11Display = null, Fps = 20, Kbps = 1000, KeyFrameSeconds = 2, Display = display }, ScreenCodec.H264));
        Assert.Contains("gfxcapture=hmonitor=0x1A2B:max_framerate=20", line);
        Assert.Contains("-c:v h264_nvenc", line);
        Assert.Contains("-bf 0", line);          // no reordering: a picture can be shown as soon as it arrives
        Assert.Contains("-aud 1", line);         // the reader needs the delimiters
        Assert.Contains("-forced-idr 1", line);
        Assert.Contains("-rc vbr", line);        // a fixed rate would pad a still screen with filler
        Assert.Contains("-b:v 1000k", line);
        Assert.Contains("-g 40", line);
        Assert.EndsWith("-f h264 pipe:1", line);
        Assert.DoesNotContain("libvpx", line);
    }

    [Fact]
    public void Amds_and_Intels_encoders_get_the_same_low_latency_stream_with_their_own_options()
    {
        var display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);
        var options = new ScreenOptions { X11Display = null, Fps = 20, Kbps = 1000, KeyFrameSeconds = 2, Display = display };

        string amd = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Amf));
        Assert.Contains("-c:v h264_amf", amd);
        Assert.Contains("-usage ultralowlatency", amd);
        Assert.Contains("-rc vbr_latency", amd);
        Assert.Contains("-bf 0", amd);
        Assert.Contains("-aud 1", amd);
        Assert.Contains("-forced_idr 1", amd);
        Assert.Contains("format=yuv420p", amd);
        Assert.EndsWith("-f h264 pipe:1", amd);

        // Intel's encoder takes NV12, not planar YUV, so the picture is converted to that.
        string intel = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Qsv));
        Assert.Contains("-c:v h264_qsv", intel);
        Assert.Contains("-bf 0", intel);
        Assert.Contains("-aud 1", intel);
        Assert.Contains("-forced_idr 1", intel);
        Assert.Contains("format=nv12", intel);
        Assert.DoesNotContain("yuv420p", intel);
        Assert.EndsWith("-f h264 pipe:1", intel);

        // NVIDIA's is the default and stays as it was.
        Assert.Equal(FfmpegArgs.Capture(options, ScreenCodec.H264), FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc));
        Assert.Contains("-c:v h264_nvenc", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264)));
        // The encoder chosen in the options is what the plain command uses.
        Assert.Contains("-c:v h264_qsv", string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { X11Display = null, Display = display, Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Qsv })));
        // VP8 never changes with the card.
        Assert.Equal(FfmpegArgs.Capture(options, ScreenCodec.Vp8), FfmpegArgs.Capture(options, ScreenCodec.Vp8, HardwareEncoder.Qsv));
    }

    [Fact]
    public void Auto_tries_the_graphics_cards_in_turn_and_software_last()
    {
        var display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);
        var auto = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = display });
        // The cards tried depend on the system: NVIDIA, AMD and Intel on Windows; NVIDIA and VA-API on Linux.
        Assert.Equal(HardwareEncoders.InOrder.Select(card => (ScreenCodec.H264, (HardwareEncoder?)card)).Append((ScreenCodec.Vp8, (HardwareEncoder?)null)), auto);
        Assert.Equal(OperatingSystem.IsLinux() ? 3 : 4, auto.Count);

        // Asking for H.264 never falls back to VP8; asking for VP8 never tries a card; naming a card tries only that one.
        Assert.DoesNotContain(ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = display, Codec = ScreenCodec.H264 }), p => p.Codec == ScreenCodec.Vp8);
        Assert.Equal(new (ScreenCodec, HardwareEncoder?)[] { (ScreenCodec.Vp8, null) }, ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = display, Codec = ScreenCodec.Vp8 }));
        Assert.Equal(new (ScreenCodec, HardwareEncoder?)[] { (ScreenCodec.H264, HardwareEncoder.Amf), (ScreenCodec.Vp8, null) },
            ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = display, Encoder = HardwareEncoder.Amf }));

        Assert.True(HardwareEncoders.TryParse("QSV", out var card) && card == HardwareEncoder.Qsv);
        Assert.True(HardwareEncoders.TryParse("amd", out card) && card == HardwareEncoder.Amf);
        Assert.False(HardwareEncoders.TryParse("x264", out _));
        Assert.Equal("Intel", HardwareEncoders.Maker(HardwareEncoder.Qsv));
        Assert.Equal("H.264 on the AMD graphics card", ScreenCodecs.Describe(ScreenCodec.H264, HardwareEncoder.Amf));
        Assert.Equal("VP8 in software", ScreenCodecs.Describe(ScreenCodec.Vp8, null));
    }

    // An access unit with an IDR picture and no parameter sets, as an encoder that writes them only once would produce.
    private static byte[] BareIdr(int totalBytes = 80)
    {
        var frame = new List<byte> { 0, 0, 0, 1, 9, 0xF0, 0, 0, 0, 1, 0x65 };
        while (frame.Count < totalBytes) frame.Add(0x55);
        return frame.ToArray();
    }

    [Fact]
    public async Task An_idr_picture_without_parameter_sets_gets_the_last_ones_so_a_late_viewer_can_start_on_it()
    {
        var key = H264.FakeKeyFrame(1280, 720, 300);
        var units = new[] { key, H264.FakeDeltaFrame(60), BareIdr(), H264.FakeDeltaFrame(60) };
        var stream = units.SelectMany(u => u).ToArray();

        // Left alone, the second IDR is not a key frame.
        var plain = await ReadAll(new MemoryStream(stream));
        Assert.False(H264.IsKeyFrame(plain[2]));

        foreach (int chunk in new[] { 3, 1 << 20 })
        {
            var reader = new H264Reader(new TrickleStream(stream, chunk)) { RepeatParameterSets = true };
            var read = new List<byte[]>();
            while (await reader.ReadAsync() is { } unit) read.Add(unit);

            Assert.Equal(4, read.Count);
            Assert.Equal(key, read[0]);                       // a picture that has its parameter sets is untouched
            Assert.Equal(units[1], read[1]);
            Assert.True(H264.IsKeyFrame(read[2]));
            Assert.True(H264.TryGetSize(read[2], out int w, out int h));
            Assert.Equal((1280, 720), (w, h));
            Assert.Equal(units[3], read[3]);
            // The delimiter stays first and the IDR slice itself is unchanged at the end.
            Assert.True(read[2].AsSpan(0, 6).SequenceEqual(new byte[] { 0, 0, 0, 1, 9, 0xF0 }));
            Assert.True(read[2].AsSpan(read[2].Length - 80 + 11).SequenceEqual(units[2].AsSpan(11)));
        }
    }

    [Fact]
    public async Task Nothing_is_added_before_any_parameter_sets_have_been_seen()
    {
        var units = new[] { BareIdr(), H264.FakeDeltaFrame(60), BareIdr() };
        var reader = new H264Reader(new MemoryStream(units.SelectMany(u => u).ToArray())) { RepeatParameterSets = true };
        var read = new List<byte[]>();
        while (await reader.ReadAsync() is { } unit) read.Add(unit);
        Assert.Equal(units, read);
    }

    [Fact]
    public void Unless_the_graphics_card_is_asked_for_the_picture_is_encoded_in_software()
    {
        var display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);
        var options = new ScreenOptions { X11Display = null, Display = display };
        Assert.Equal(ScreenCodec.Auto, options.Codec);
        foreach (var codec in new[] { ScreenCodec.Auto, ScreenCodec.Vp8 })
        {
            string line = string.Join(' ', FfmpegArgs.Capture(options, codec));
            Assert.Contains("-c:v libvpx", line);
            Assert.EndsWith("-f ivf pipe:1", line);
        }
        Assert.Equal(FfmpegArgs.Capture(options), FfmpegArgs.Capture(options, ScreenCodec.Auto));
    }

    [Fact]
    public void The_decoder_reads_h264_or_ivf()
    {
        string h264 = string.Join(' ', FfmpegArgs.Decode(ScreenCodec.H264));
        Assert.Contains("-f h264 -i pipe:0", h264);
        Assert.Contains("-f rawvideo -pix_fmt bgra pipe:1", h264);
        Assert.DoesNotContain("nobuffer", h264);
        Assert.Contains("-f ivf -i pipe:0", string.Join(' ', FfmpegArgs.Decode(ScreenCodec.Vp8)));
        Assert.Equal(FfmpegArgs.Decode(), FfmpegArgs.Decode(ScreenCodec.Vp8));
    }
}
