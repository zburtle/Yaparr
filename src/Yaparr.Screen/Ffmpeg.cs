using System.Diagnostics;
using System.Globalization;

namespace Yaparr.Screen;

/// <summary>Finds the FFmpeg program. Yaparr runs it as a separate process and links nothing of it.</summary>
public static class FfmpegLocator
{
    /// <summary>
    /// The first FFmpeg found: the path in the YAPARR_FFMPEG environment variable, then next to the app (an "ffmpeg" folder
    /// or the app's own folder), then the copy the app downloaded itself (<see cref="FfmpegInstaller"/>), then the system PATH.
    /// Null if there is none.
    /// </summary>
    public static string? Find(string? appDirectory = null, string? pathVariable = null, string? explicitPath = null, string? installDirectory = null)
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        explicitPath ??= Environment.GetEnvironmentVariable("YAPARR_FFMPEG");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return Path.GetFullPath(explicitPath);

        appDirectory ??= AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(appDirectory, "ffmpeg", exe), Path.Combine(appDirectory, exe) })
            if (File.Exists(candidate)) return candidate;

        if (FfmpegInstaller.FindAny(installDirectory) is { } downloaded) return downloaded;

        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Asks FFmpeg what it can do; used to pick an encoder.</summary>
    public static async Task<string> RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return await output + await error;
    }
}

/// <summary>
/// How the picture is compressed. Auto uses the graphics card when there is one that can (H264 on NVIDIA's NVENC) and VP8 in
/// software otherwise.
/// </summary>
public enum ScreenCodec { Auto, Vp8, H264 }

/// <summary>What to capture and how hard to compress it. Name a monitor or a window.</summary>
public sealed class ScreenOptions
{
    /// <summary>One monitor, from <see cref="Displays.List"/>. Ignored when <see cref="WindowHandle"/> is set.</summary>
    public Display? Display { get; init; }

    /// <summary>One window, from <see cref="ShareWindows.List"/>.</summary>
    public long? WindowHandle { get; init; }

    public int Fps { get; init; } = 10;

    /// <summary>The picture is scaled down to fit this box, never up; the shape is kept.</summary>
    public int MaxWidth { get; init; } = 1280;
    public int MaxHeight { get; init; } = 720;

    public int Kbps { get; init; } = 800;

    /// <summary>Seconds between key frames. A viewer that joins or loses packets waits at most this long for one.</summary>
    public double KeyFrameSeconds { get; init; } = 2;

    public ScreenCodec Codec { get; init; } = ScreenCodec.Auto;

    /// <summary>Whether to draw the mouse pointer into the picture.</summary>
    public bool ShowCursor { get; init; } = true;
}

/// <summary>The command lines for FFmpeg. Kept apart from running it so that they can be checked without FFmpeg.</summary>
public static class FfmpegArgs
{
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Hex(long v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>
    /// Capture a monitor or a window and write VP8 in an IVF container to standard output. The capture is Windows Graphics
    /// Capture (FFmpeg's gfxcapture filter, FFmpeg 8.1 or newer): it gets the content of browsers and games, which the older
    /// screen-copying way returns black, it is not spoiled by windows on top, and it takes less processor time.
    /// dup_frames=1 repeats the last picture when nothing changed, so the rate is steady and a still screen still sends key
    /// frames. Builds before 24 October 2026 (FFmpeg 8.1.3 and 9.0.2 included) lack that option and the fixes that came with it:
    /// they deliver one frame and then nothing, so asking for the option makes them fail at once, with a clear message.
    /// </summary>
    public static List<string> Capture(ScreenOptions o) => Capture(o, o.Codec);

    /// <summary>
    /// The same for one codec. <see cref="ScreenCodec.H264"/> writes an Annex B stream (read with <see cref="H264Reader"/>),
    /// anything else VP8 in IVF; <see cref="ScreenCodec.Auto"/> is for the sharer to resolve and here means VP8, which needs no
    /// particular hardware.
    /// </summary>
    public static List<string> Capture(ScreenOptions o, ScreenCodec codec)
    {
        if (o.Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(o), "Frames per second must be between 1 and 60.");
        if (o.Kbps is < 50 or > 20_000) throw new ArgumentOutOfRangeException(nameof(o), "The bitrate must be between 50 and 20000 kbit/s.");
        if (o.MaxWidth < 16 || o.MaxHeight < 16) throw new ArgumentOutOfRangeException(nameof(o), "The picture box is too small.");

        string source;
        if (o.WindowHandle is { } window) source = "hwnd=" + Hex(window);
        else if (o.Display is { Handle: not 0 } display) source = "hmonitor=" + Hex(display.Handle);
        else throw new ArgumentException("Choose a monitor or a window to share.", nameof(o));

        // Even sizes only: the encoder works on pairs of pixels. A window that is resized keeps its shape inside the first size.
        var filter = $"gfxcapture={source}:max_framerate={o.Fps.ToString(CultureInfo.InvariantCulture)}:capture_cursor={(o.ShowCursor ? 1 : 0)}:resize_mode=scale_aspect:dup_frames=1,"
            + "hwdownload,format=bgra,"
            + $"scale='min({o.MaxWidth},iw)':'min({o.MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-filter_complex", filter };
        var rate = new[]
        {
            "-b:v", o.Kbps + "k", "-maxrate", Num(o.Kbps * 1.5) + "k", "-bufsize", Num(o.Kbps * 1.5) + "k",
            "-g", Math.Max(1, (int)Math.Round(o.Fps * o.KeyFrameSeconds)).ToString(CultureInfo.InvariantCulture),
        };
        if (codec == ScreenCodec.H264)
        {
            // NVIDIA's encoder: the picture is compressed on the graphics card, so the processor is left alone. Low-latency tuning,
            // no B-frames, every key frame an IDR picture that repeats the parameter sets (the file format has no header to hold
            // them), and a delimiter in front of each picture, which is how the reader finds where one picture ends. Variable
            // rate: a fixed rate would fill a still screen with padding up to the bitrate.
            args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-rc", "vbr", "-rc-lookahead", "0", "-bf", "0",
                "-profile:v", "high", "-forced-idr", "1", "-aud", "1", "-zerolatency", "1" });
            args.AddRange(rate);
            args.AddRange(new[] { "-f", "h264", "pipe:1" });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "libvpx", "-deadline", "realtime", "-cpu-used", "6", "-lag-in-frames", "0", "-auto-alt-ref", "0",
                "-error-resilient", "1", "-threads", "4" });
            args.AddRange(rate);
            args.AddRange(new[] { "-f", "ivf", "pipe:1" });
        }
        return args;
    }

    /// <summary>Read IVF (VP8) or an H.264 byte stream from standard input and write the pictures as raw BGRA to standard output.</summary>
    public static List<string> Decode(ScreenCodec codec = ScreenCodec.Vp8) => new()
    {
        // No probing: by default FFmpeg reads several seconds of input before it shows the first picture.
        // (Not "-fflags nobuffer": with it FFmpeg fails to decode frames that arrive in a burst, as a late joiner's do when its parent replays the cache.)
        "-hide_banner", "-loglevel", "error", "-flags", "low_delay", "-probesize", "32", "-analyzeduration", "0",
        "-f", codec == ScreenCodec.H264 ? "h264" : "ivf", "-i", "pipe:0", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
    };
}
