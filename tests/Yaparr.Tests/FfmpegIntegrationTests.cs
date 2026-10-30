using System.Diagnostics;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>
/// Runs a real FFmpeg through the commands Yaparr uses. It is how a build of FFmpeg is shown to work with Yaparr before it is
/// pinned in <see cref="FfmpegPackage.Default"/>. It does nothing where there is no FFmpeg (set YAPARR_FFMPEG to try one), no
/// monitor, or the system is not Windows, which is the case on the build server.
/// </summary>
public class FfmpegIntegrationTests
{
    private static Process Start(string ffmpeg, IEnumerable<string> args, bool input, System.Text.StringBuilder? errors = null)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = input, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, e) => { if (errors != null && e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        return p;
    }

    [Fact]
    public async Task The_real_ffmpeg_captures_a_monitor_and_its_pictures_can_be_decoded_again()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;

        var options = new ScreenOptions { Display = display, Fps = 10, Kbps = 500, MaxWidth = 640, MaxHeight = 360 };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        // Capture and encode: an IVF stream of VP8 whose first frame is a key frame of the size the header promises.
        var frames = new List<byte[]>();
        IvfHeader header;
        var capture = Start(ffmpeg, FfmpegArgs.Capture(options), input: false);
        try
        {
            var stdout = capture.StandardOutput.BaseStream;
            header = await Ivf.ReadHeaderAsync(stdout, limit.Token);
            while (frames.Count < 12 && await Ivf.ReadFrameAsync(stdout, limit.Token) is { } frame) frames.Add(frame.Data);
        }
        finally { if (!capture.HasExited) capture.Kill(entireProcessTree: true); capture.Dispose(); }

        Assert.Equal("VP80", header.FourCc);
        Assert.True(header.Width <= 640 && header.Height <= 360 && header.Width % 2 == 0 && header.Height % 2 == 0, $"{header.Width}x{header.Height}");
        Assert.True(frames.Count >= 5, $"only {frames.Count} frames");
        Assert.True(Vp8.IsKeyFrame(frames[0]), "the first frame is not a key frame");
        Assert.True(Vp8.TryGetSize(frames[0], out int w, out int h));
        Assert.Equal((header.Width, header.Height), (w, h));

        // Decode: the same frames go in and a whole picture of that size comes out.
        var decodeErrors = new System.Text.StringBuilder();
        var decode = Start(ffmpeg, FfmpegArgs.Decode(), input: true, decodeErrors);
        try
        {
            var stdin = decode.StandardInput.BaseStream;
            await stdin.WriteAsync(new IvfHeader("VP80", w, h).ToBytes(), limit.Token);
            long pts = 0;
            foreach (var f in frames)
            {
                await stdin.WriteAsync(Ivf.FrameHeader(f.Length, pts++), limit.Token);
                await stdin.WriteAsync(f, limit.Token);
            }
            await stdin.FlushAsync(limit.Token);
            var picture = new byte[w * h * 4];
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            try { await decode.StandardOutput.BaseStream.ReadExactlyAsync(picture, wait.Token); }
            catch (OperationCanceledException) { Assert.Fail($"no picture came out of the decoder ({frames.Count} frames, {w}x{h}, exited={decode.HasExited}): {decodeErrors}"); }
            Assert.Contains(picture, b => b != 0); // not an empty picture
        }
        finally { if (!decode.HasExited) decode.Kill(entireProcessTree: true); decode.Dispose(); }
    }

    // Whether this computer has a graphics card that FFmpeg can encode H.264 on: asking for one tiny picture is the only reliable way.
    private static async Task<bool> NvencWorksAsync(string ffmpeg)
    {
        var p = Start(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=5:d=1", "-frames:v", "2", "-c:v", "h264_nvenc", "-f", "null", "-" }, input: false);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(limit.Token);
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException) { return false; }
        finally { if (!p.HasExited) p.Kill(entireProcessTree: true); p.Dispose(); }
    }

    [Fact]
    public async Task The_real_ffmpeg_captures_a_monitor_on_the_graphics_card_and_its_pictures_can_be_decoded_again()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;
        if (!await NvencWorksAsync(ffmpeg)) return;

        var options = new ScreenOptions { Display = display, Fps = 10, Kbps = 500, MaxWidth = 640, MaxHeight = 360, Codec = ScreenCodec.H264 };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        // Capture and encode: whole pictures, the first a key frame with the parameter sets and the size we asked for at most.
        var units = new List<byte[]>();
        var capture = Start(ffmpeg, FfmpegArgs.Capture(options), input: false);
        try
        {
            var reader = new H264Reader(capture.StandardOutput.BaseStream);
            while (units.Count < 12 && await reader.ReadAsync(limit.Token) is { } unit) units.Add(unit);
        }
        finally { if (!capture.HasExited) capture.Kill(entireProcessTree: true); capture.Dispose(); }

        Assert.True(units.Count >= 5, $"only {units.Count} pictures");
        var first = units.FindIndex(u => H264.IsKeyFrame(u));
        Assert.True(first >= 0, "no key frame");
        Assert.True(H264.TryGetSize(units[first], out int w, out int h));
        Assert.True(w <= 640 && h <= 360 && w % 2 == 0 && h % 2 == 0, $"{w}x{h}");
        Assert.Equal(ScreenCodec.H264, ScreenCodecs.TryGetKeyFrame(units[first], out var codec, out _, out _) ? codec : ScreenCodec.Auto);

        // Decode: from the key frame on, the same way the viewer feeds the decoder.
        var decodeErrors = new System.Text.StringBuilder();
        var decode = Start(ffmpeg, FfmpegArgs.Decode(ScreenCodec.H264), input: true, decodeErrors);
        try
        {
            var stdin = decode.StandardInput.BaseStream;
            foreach (var u in units.Skip(first))
            {
                await stdin.WriteAsync(u, limit.Token);
                await stdin.WriteAsync(H264.EndMarker.ToArray(), limit.Token);
                await stdin.FlushAsync(limit.Token);
            }
            var picture = new byte[w * h * 4];
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            try { await decode.StandardOutput.BaseStream.ReadExactlyAsync(picture, wait.Token); }
            catch (OperationCanceledException) { Assert.Fail($"no picture came out of the decoder ({units.Count - first} pictures, {w}x{h}, exited={decode.HasExited}): {decodeErrors}"); }
            Assert.Contains(picture, b => b != 0);
        }
        finally { if (!decode.HasExited) decode.Kill(entireProcessTree: true); decode.Dispose(); }
    }

    [Fact]
    public async Task The_real_ffmpeg_knows_the_graphics_capture_filter()
    {
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        var text = await FfmpegLocator.RunAsync(ffmpeg, new[] { "-hide_banner", "-filters" });
        Assert.Contains("gfxcapture", text);
        var help = await FfmpegLocator.RunAsync(ffmpeg, new[] { "-hide_banner", "-h", "filter=gfxcapture" });
        foreach (var option in new[] { "hwnd", "hmonitor", "max_framerate", "capture_cursor", "resize_mode" })
            Assert.Contains(option, help);
        var encoders = await FfmpegLocator.RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" });
        Assert.Contains("libvpx ", encoders);
    }
}
