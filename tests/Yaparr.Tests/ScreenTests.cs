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
    public void A_monitor_is_captured_by_its_handle_with_windows_graphics_capture()
    {
        var display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);
        var args = FfmpegArgs.Capture(new ScreenOptions { Fps = 15, Kbps = 900, MaxWidth = 1280, MaxHeight = 720, KeyFrameSeconds = 2, Display = display });
        string line = string.Join(' ', args);
        Assert.Contains("gfxcapture=hmonitor=0x1A2B:max_framerate=15:capture_cursor=1:resize_mode=scale_aspect", line);
        Assert.Contains("hwdownload,format=bgra", line);
        Assert.Contains("min(1280,iw)", line);
        Assert.Contains("-b:v 900k", line);
        Assert.Contains("-g 30", line);
        Assert.EndsWith("-f ivf pipe:1", line);
        Assert.DoesNotContain("gdigrab", line);
    }

    [Fact]
    public void A_window_is_captured_by_its_handle_and_a_window_beats_a_monitor()
    {
        var display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);
        string line = string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { WindowHandle = 0xFF, Display = display, ShowCursor = false }));
        Assert.Contains("gfxcapture=hwnd=0xFF:", line);
        Assert.DoesNotContain("hmonitor", line);
        Assert.Contains("capture_cursor=0", line);
    }

    [Fact]
    public void Something_to_capture_has_to_be_chosen()
    {
        Assert.Throws<ArgumentException>(() => FfmpegArgs.Capture(new ScreenOptions()));
        Assert.Throws<ArgumentException>(() => FfmpegArgs.Capture(new ScreenOptions { Display = new Display(0, "x", 0, 0, 10, 10, true) }));
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

    [Fact]
    public void The_capture_command_refuses_silly_numbers()
    {
        var display = new Display(0, "x", 0, 0, 100, 100, true, Handle: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgs.Capture(new ScreenOptions { Display = display, Fps = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgs.Capture(new ScreenOptions { Display = display, Kbps = 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgs.Capture(new ScreenOptions { Display = display, MaxWidth = 2 }));
    }

    [Fact]
    public void The_decode_command_reads_ivf_and_writes_raw_pictures()
    {
        string line = string.Join(' ', FfmpegArgs.Decode());
        Assert.Contains("-f ivf -i pipe:0", line);
        Assert.Contains("-f rawvideo -pix_fmt bgra pipe:1", line);
    }

    [Fact]
    public void Ffmpeg_is_found_by_explicit_path_then_next_to_the_app_then_on_the_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "yaparr-ffmpeg-" + Guid.NewGuid().ToString("N"));
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var onPath = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var explicitFile = Path.Combine(root, "mine", exe);
            Directory.CreateDirectory(Path.GetDirectoryName(explicitFile)!);

            Assert.Null(FfmpegLocator.Find(app, "", Path.Combine(root, "missing"), Path.Combine(root, "none")));

            File.WriteAllText(Path.Combine(onPath, exe), "");
            Assert.Equal(Path.Combine(onPath, exe), FfmpegLocator.Find(app, onPath, Path.Combine(root, "missing"), Path.Combine(root, "none")));

            Directory.CreateDirectory(Path.Combine(app, "ffmpeg"));
            File.WriteAllText(Path.Combine(app, "ffmpeg", exe), "");
            Assert.Equal(Path.Combine(app, "ffmpeg", exe), FfmpegLocator.Find(app, onPath, Path.Combine(root, "missing"), Path.Combine(root, "none")));

            File.WriteAllText(explicitFile, "");
            Assert.Equal(explicitFile, FfmpegLocator.Find(app, onPath, explicitFile, Path.Combine(root, "none")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
