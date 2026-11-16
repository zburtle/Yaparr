using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>The decoder inside the program (stage 6 of #225). Those that need FFmpeg's libraries do nothing where they are not there.</summary>
public class InProcessDecoderTests
{
    private readonly ITestOutputHelper _out;
    public InProcessDecoderTests(ITestOutputHelper output) => _out = output;

    // A short clip of VP8 made by the encoder in this program (a moving ramp), as the share sends it: a key frame first.
    private static List<byte[]>? VpEightClip(int width, int height, int frames)
    {
        if (!FfmpegLibraries.IsAvailable) return null;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(width, height, new ScreenOptions { Fps = 30, Kbps = 1000, KeyFrameSeconds = 1, X11Display = null }, null, ScreenCodec.Vp8, null); }
        catch (PlatformNotSupportedException) { return null; }   // libraries without libvpx
        var list = new List<byte[]>();
        using (encoder)
        {
            var picture = new byte[encoder.PictureBytes];
            for (int n = 0; n < frames; n++)
            {
                for (int i = 0; i < width * height; i++) picture[i] = (byte)(40 + (i % width + n * 5) % 160);
                Array.Fill(picture, (byte)128, width * height, picture.Length - width * height);
                encoder.Encode(picture, false, (frame, _) => list.Add(frame));
            }
        }
        return list;
    }

    [Fact]
    public void Frames_of_VP8_come_out_as_pictures_of_the_size_of_the_stream()
    {
        if (!FfmpegLibraries.IsAvailable || !InProcessDecoder.IsAvailable(ScreenCodec.Vp8)) return;
        var clip = VpEightClip(640, 360, 20);
        if (clip == null) return;
        using var decoder = new InProcessDecoder(ScreenCodec.Vp8);
        int pictures = 0;
        long sum = 0;
        foreach (var frame in clip)
            Assert.True(decoder.Decode(frame, 0, 0, (nv12, w, h) =>
            {
                Assert.Equal((640, 360), (w, h));
                Assert.Equal(Nv12.Size(640, 360), nv12.Length);
                pictures++;
                foreach (var b in nv12[..(640 * 360)]) sum += b;   // not all black, not all one value
            }), decoder.LastError);
        Assert.Equal(clip.Count, pictures);
        Assert.InRange(sum / (pictures * 640L * 360), 20, 235);
        _out.WriteLine($"decoded {pictures} pictures with {decoder.Description}");
    }

    [Fact]
    public void A_preview_gets_a_smaller_picture_that_keeps_the_proportions_with_even_sides()
    {
        if (!FfmpegLibraries.IsAvailable || !InProcessDecoder.IsAvailable(ScreenCodec.Vp8)) return;
        var clip = VpEightClip(640, 360, 5);
        if (clip == null) return;
        using var decoder = new InProcessDecoder(ScreenCodec.Vp8);
        var seen = new List<(int W, int H)>();
        foreach (var frame in clip) decoder.Decode(frame, 200, 200, (nv12, w, h) => { Assert.Equal(Nv12.Size(w, h), nv12.Length); seen.Add((w, h)); });
        Assert.NotEmpty(seen);
        Assert.All(seen, s => Assert.Equal((200, 112), s));   // 640x360 fitted into 200x200, as PreviewSize.Fit has it
    }

    [Fact]
    public void A_frame_that_is_not_a_frame_is_refused_without_stopping_the_decoder()
    {
        if (!FfmpegLibraries.IsAvailable || !InProcessDecoder.IsAvailable(ScreenCodec.Vp8)) return;
        var clip = VpEightClip(320, 180, 6);
        if (clip == null) return;
        using var decoder = new InProcessDecoder(ScreenCodec.Vp8);
        int pictures = 0;
        Assert.False(decoder.Decode(new byte[] { 1, 2, 3 }, 0, 0, (_, _, _) => pictures++));
        Assert.NotEmpty(decoder.LastError);
        foreach (var frame in clip) decoder.Decode(frame, 0, 0, (_, _, _) => pictures++);
        Assert.Equal(clip.Count, pictures);   // the clip starts with a key frame, so it decodes after the bad one
    }

    private static string TempMark() => Path.Combine(Path.GetTempPath(), "yaparr-dec-guard-" + Guid.NewGuid().ToString("N"), "mark.txt");

    [Fact]
    public void The_mark_stays_while_any_of_several_decoders_is_still_running()
    {
        var mark = TempMark();
        try
        {
            var guard = new NativeGuard("unused.txt");
            guard.UseFile(mark);
            guard.Begin(); guard.Begin();
            guard.End();
            Assert.Contains($"active={Environment.ProcessId}", File.ReadAllText(mark));   // one is still going
            guard.End();
            Assert.Contains("active=0", File.ReadAllText(mark));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(mark)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void Decoding_here_is_turned_off_after_two_endings_in_the_middle_of_it_and_comes_back_after_a_good_one()
    {
        var mark = TempMark();
        Directory.CreateDirectory(Path.GetDirectoryName(mark)!);
        const int gone = 2_000_000_000;   // a process id nothing runs under
        try
        {
            var guard = new NativeGuard("unused.txt");
            File.WriteAllText(mark, $"unclean=1\nactive={gone}\n");
            guard.UseFile(mark);
            Assert.Equal(2, guard.UncleanEndings);
            Assert.False(guard.Allowed);
            guard.Begin(); guard.End();   // a decoder that ends well
            guard.UseFile(mark);
            Assert.True(guard.Allowed);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(mark)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_viewer_whose_guard_is_closed_does_not_use_the_card_and_says_why()
    {
        if (!FfmpegLibraries.IsAvailable) return;
        var mark = TempMark();
        Directory.CreateDirectory(Path.GetDirectoryName(mark)!);
        const int gone = 2_000_000_000;
        string? before = Environment.GetEnvironmentVariable("YAPARR_CARD_DECODE");
        try
        {
            File.WriteAllText(mark, $"unclean=1\nactive={gone}\n");
            InProcessDecodeGuard.UseFile(mark);
            Environment.SetEnvironmentVariable("YAPARR_CARD_DECODE", null);
            // A preview of every frame would decode on the card; with the guard closed it decodes on the processor.
            using var viewer = new ScreenViewer(new PreviewTests.Frames(), preview: new PreviewSize(640, 360, 15));
            var card = InProcessDecoder.FirstCard(preferD3d12: true);   // Direct3D 12 on Windows, NVDEC on Linux
            if (card == DecodeDevice.Processor) { Assert.Equal("on the processor", viewer.DecoderKind); return; }   // no card decoding here
            Assert.Equal("on the processor (the card is not used: the program ended in the middle of decoding 2 times in a row (see " + mark
                         + "); YAPARR_CARD_DECODE=1 asks for it)", viewer.DecoderKind);

            // asked for in so many words: the guard does not stop it
            Environment.SetEnvironmentVariable("YAPARR_CARD_DECODE", "1");
            using var asked = new ScreenViewer(new PreviewTests.Frames(), preview: new PreviewSize(640, 360, 15));
            Assert.Equal("on the card through " + InProcessDecoder.Name(card), asked.DecoderKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YAPARR_CARD_DECODE", before);
            InProcessDecodeGuard.UseFile(null);
            try { Directory.Delete(Path.GetDirectoryName(mark)!, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task H264_from_the_in_process_encoder_is_shown_by_a_viewer()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        const int width = 1280, height = 720;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(width, height, new ScreenOptions { Fps = 30, Kbps = 4000, KeyFrameSeconds = 1 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }   // no NVENC here
        using (encoder)
        {
            var frames = new PreviewTests.Frames();
            var shown = new List<(int W, int H)>();
            using var viewer = new ScreenViewer(frames);
            viewer.PictureReceived += p => { lock (shown) shown.Add((p.Width, p.Height)); viewer.Return(p); };

            var picture = new byte[encoder.PictureBytes];
            for (int n = 0; n < 60; n++)
            {
                for (int i = 0; i < width * height; i++) picture[i] = (byte)((i % width) / 5 + n * 3);
                Array.Fill(picture, (byte)128, width * height, picture.Length - width * height);
                encoder.Encode(picture, forceKey: n % 30 == 0, (frame, isKey) => frames.Push(frame, isKey));
                await Task.Delay(10);
            }
            await TestUtil.WaitFor(() => { lock (shown) return shown.Count >= 55; }, 10_000);
            lock (shown)
            {
                Assert.True(shown.Count >= 55, $"{shown.Count} pictures of 60 frames");
                Assert.All(shown, s => Assert.Equal((width, height), s));
            }
            Assert.Equal("on the processor", viewer.DecoderKind);
            Assert.True(viewer.DecoderCpu > TimeSpan.Zero);
            _out.WriteLine($"{viewer.DecoderKind}; decoding took {viewer.DecoderCpu.TotalMilliseconds:0} ms for {shown.Count} pictures");
        }
    }
}
