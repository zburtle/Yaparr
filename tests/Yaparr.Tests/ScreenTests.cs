using Yaparr.Screen;

namespace Yaparr.Tests;

public class ScreenTests
{

    [Fact]
    public async Task An_ivf_stream_reads_back_what_was_written()
    {
        var header = new IvfHeader("VP80", 1280, 720);
        var frames = new[] { Vp8.FakeKeyFrame(1280, 720, 300), new byte[] { 1, 2, 3 }, new byte[] { 4, 5 } };
        var ms = new MemoryStream();
        ms.Write(header.ToBytes());
        long pts = 0;
        foreach (var f in frames) { ms.Write(Ivf.FrameHeader(f.Length, pts++)); ms.Write(f); }
        ms.Position = 0;

        var read = await Ivf.ReadHeaderAsync(ms);
        Assert.Equal(header, read);
        for (int i = 0; i < frames.Length; i++)
        {
            var (data, p) = (await Ivf.ReadFrameAsync(ms))!.Value;
            Assert.Equal(frames[i], data);
            Assert.Equal(i, p);
        }
        Assert.Null(await Ivf.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task A_damaged_ivf_stream_is_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Ivf.ReadHeaderAsync(new MemoryStream(new byte[32])));

        var tooBig = new MemoryStream(Ivf.FrameHeader(Ivf.MaxFrameBytes + 1, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => Ivf.ReadFrameAsync(tooBig));

        var cut = new MemoryStream(Ivf.FrameHeader(10, 0).Concat(new byte[4]).ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() => Ivf.ReadFrameAsync(cut));
    }

    [Fact]
    public void Key_frames_and_their_size_are_recognised()
    {
        var key = Vp8.FakeKeyFrame(1920, 1080);
        Assert.True(Vp8.IsKeyFrame(key));
        Assert.True(Vp8.TryGetSize(key, out int w, out int h));
        Assert.Equal((1920, 1080), (w, h));

        var delta = (byte[])key.Clone();
        delta[0] |= 1; // bit 0 set: not a key frame
        Assert.False(Vp8.IsKeyFrame(delta));
        Assert.False(Vp8.TryGetSize(delta, out _, out _));
        Assert.False(Vp8.IsKeyFrame(new byte[] { 0, 1, 2 }));
    }

    [Fact]
    public void The_list_of_windows_only_has_real_ones_and_a_missing_handle_does_not_exist()
    {
        foreach (var w in ShareWindows.List())
        {
            Assert.False(string.IsNullOrWhiteSpace(w.Title));
            Assert.True(w.Width >= 120 && w.Height >= 80);
            Assert.NotEqual(0, w.Handle);
            Assert.True(ShareWindows.Exists(w.Handle) || !OperatingSystem.IsWindows());
        }
        Assert.False(ShareWindows.Exists(0));
        Assert.False(ShareWindows.Exists(0x7FFFFFF0));
    }
}
