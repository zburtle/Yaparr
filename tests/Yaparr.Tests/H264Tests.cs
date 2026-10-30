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
        string line = string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { Fps = 20, Kbps = 1000, KeyFrameSeconds = 2, Display = display }, ScreenCodec.H264));
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
    public void Unless_the_graphics_card_is_asked_for_the_picture_is_encoded_in_software()
    {
        var display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);
        var options = new ScreenOptions { Display = display };
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
