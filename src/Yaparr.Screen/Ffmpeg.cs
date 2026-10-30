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

public enum ScreenCodec { Vp8 }

/// <summary>What to capture and how hard to compress it.</summary>
public sealed class ScreenOptions
{
    /// <summary>Null for a monitor or the whole desktop, otherwise the exact title of a window. <see cref="WindowHandle"/> is better: titles are not unique.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>A window to share, from <see cref="ShareWindows.List"/>. Takes the place of the monitor.</summary>
    public long? WindowHandle { get; init; }

    /// <summary>One monitor (see <see cref="Displays.List"/>) instead of the whole desktop, which spans every monitor. Ignored when sharing a window.</summary>
    public Display? Display { get; init; }

    public int Fps { get; init; } = 10;

    /// <summary>The picture is scaled down to fit this box, never up; the shape is kept.</summary>
    public int MaxWidth { get; init; } = 1280;
    public int MaxHeight { get; init; } = 720;

    public int Kbps { get; init; } = 800;

    /// <summary>Seconds between key frames. A viewer that joins or loses packets waits at most this long for one.</summary>
    public double KeyFrameSeconds { get; init; } = 2;

    public ScreenCodec Codec { get; init; } = ScreenCodec.Vp8;

    /// <summary>Whether to draw the mouse pointer into the picture.</summary>
    public bool ShowCursor { get; init; } = true;
}

/// <summary>The command lines for FFmpeg. Kept apart from running it so that they can be checked without FFmpeg.</summary>
public static class FfmpegArgs
{
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Capture the screen or a window and write VP8 in an IVF container to standard output.</summary>
    public static List<string> Capture(ScreenOptions o)
    {
        if (o.Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(o), "Frames per second must be between 1 and 60.");
        if (o.Kbps is < 50 or > 20_000) throw new ArgumentOutOfRangeException(nameof(o), "The bitrate must be between 50 and 20000 kbit/s.");
        if (o.MaxWidth < 16 || o.MaxHeight < 16) throw new ArgumentOutOfRangeException(nameof(o), "The picture box is too small.");

        // Even sizes only: the encoder works on pairs of pixels.
        var scale = $"scale='min({o.MaxWidth},iw)':'min({o.MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2";
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "gdigrab", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture),
            "-draw_mouse", o.ShowCursor ? "1" : "0",
        };
        bool wholeScreen = o.WindowHandle == null && o.WindowTitle is not { Length: > 0 };
        if (wholeScreen && o.Display is { } d)
        {
            args.AddRange(new[] { "-offset_x", d.X.ToString(CultureInfo.InvariantCulture), "-offset_y", d.Y.ToString(CultureInfo.InvariantCulture),
                "-video_size", $"{d.Width}x{d.Height}" });
        }
        args.AddRange(new[]
        {
            "-i", o.WindowHandle is { } h ? "hwnd=0x" + h.ToString("X", CultureInfo.InvariantCulture) : o.WindowTitle is { Length: > 0 } t ? "title=" + t : "desktop",
            "-vf", scale + ",format=yuv420p",
            "-c:v", "libvpx", "-deadline", "realtime", "-cpu-used", "6", "-lag-in-frames", "0", "-auto-alt-ref", "0",
            "-error-resilient", "1", "-threads", "4",
            "-b:v", o.Kbps + "k", "-maxrate", Num(o.Kbps * 1.5) + "k", "-bufsize", Num(o.Kbps * 1.5) + "k",
            "-g", Math.Max(1, (int)Math.Round(o.Fps * o.KeyFrameSeconds)).ToString(CultureInfo.InvariantCulture),
            "-f", "ivf", "pipe:1",
        });
        return args;
    }

    /// <summary>Read IVF from standard input and write the pictures as raw BGRA to standard output.</summary>
    public static List<string> Decode() => new()
    {
        // No probing: by default FFmpeg reads several seconds of input before it shows the first picture.
        "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer", "-flags", "low_delay", "-probesize", "32", "-analyzeduration", "0",
        "-f", "ivf", "-i", "pipe:0", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
    };
}
