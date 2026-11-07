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
/// How the picture is compressed. Auto uses the graphics card when there is one that can (H264 on NVIDIA's NVENC, AMD's AMF or
/// Intel's Quick Sync) and VP8 in software otherwise.
/// </summary>
public enum ScreenCodec { Auto, Vp8, H264 }

/// <summary>The graphics card encoders FFmpeg can drive for H.264.</summary>
public enum HardwareEncoder { Nvenc, Amf, Qsv, Vaapi }

public static class HardwareEncoders
{
    /// <summary>
    /// The order Auto tries them in: the first one that starts is used. A computer with an NVIDIA card and Intel graphics uses the
    /// NVIDIA card. On Linux Intel and AMD graphics are both driven through VA-API; AMF and Quick Sync are the Windows ways.
    /// </summary>
    public static IReadOnlyList<HardwareEncoder> InOrder { get; } = OperatingSystem.IsLinux()
        ? new[] { HardwareEncoder.Nvenc, HardwareEncoder.Vaapi }
        : new[] { HardwareEncoder.Nvenc, HardwareEncoder.Amf, HardwareEncoder.Qsv };

    /// <summary>FFmpeg's name for the encoder.</summary>
    public static string Name(HardwareEncoder encoder) => encoder switch
    {
        HardwareEncoder.Nvenc => "h264_nvenc",
        HardwareEncoder.Amf => "h264_amf",
        HardwareEncoder.Vaapi => "h264_vaapi",
        _ => "h264_qsv",
    };

    /// <summary>Whose graphics hardware it is, for messages.</summary>
    public static string Maker(HardwareEncoder encoder) => encoder switch
    {
        HardwareEncoder.Nvenc => "NVIDIA",
        HardwareEncoder.Amf => "AMD",
        HardwareEncoder.Vaapi => "Intel or AMD",
        _ => "Intel",
    };

    /// <summary>
    /// The render device VA-API encodes on: the path in YAPARR_VAAPI_DEVICE, else the first /dev/dri/renderD* there is, else the
    /// usual first one.
    /// </summary>
    public static string VaapiDevice()
    {
        if (Environment.GetEnvironmentVariable("YAPARR_VAAPI_DEVICE") is { Length: > 0 } chosen) return chosen;
        try
        {
            if (Directory.Exists("/dev/dri") && Directory.GetFiles("/dev/dri", "renderD*").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault() is { } found) return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "/dev/dri/renderD128";
    }

    /// <summary>The pixel format (and for VA-API the upload to the card) that ends the filter chain for this encoder.</summary>
    internal static string PixelFormat(ScreenCodec codec, HardwareEncoder encoder) =>
        codec != ScreenCodec.H264 ? "yuv420p" : encoder switch { HardwareEncoder.Qsv => "nv12", HardwareEncoder.Vaapi => "nv12,hwupload", _ => "yuv420p" };

    public static bool TryParse(string? text, out HardwareEncoder encoder)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "nvenc" or "nvidia": encoder = HardwareEncoder.Nvenc; return true;
            case "amf" or "amd": encoder = HardwareEncoder.Amf; return true;
            case "qsv" or "intel": encoder = HardwareEncoder.Qsv; return true;
            case "vaapi" or "va-api": encoder = HardwareEncoder.Vaapi; return true;
            default: encoder = default; return false;
        }
    }
}

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

    /// <summary>Use only this graphics card encoder for H.264. Null tries them in turn (<see cref="HardwareEncoders.InOrder"/>).</summary>
    public HardwareEncoder? Encoder { get; init; }

    /// <summary>
    /// Whether to share the sound too: a shared window's own sound, or for a monitor everything but Yaparr (so the voice chat
    /// does not go round). Needs Windows 10 version 2004 or newer; without it the picture is shared alone.
    /// </summary>
    public bool ShareSound { get; init; }

    /// <summary>
    /// Linux, Wayland: the program (by its name) whose sound is shared. Wayland does not say which program a shared window belongs to,
    /// so without this the sound is everything except Yaparr's own.
    /// </summary>
    public string? SoundProgram { get; init; }

    /// <summary>
    /// Scale and convert the picture on the graphics card rather than the processor, so the frames never come down to system
    /// memory. Only for H.264 on NVIDIA's encoder so far. The sharer falls back to the processor path if FFmpeg cannot start it.
    /// </summary>
    public bool GpuScale { get; init; }

    /// <summary>Whether to draw the mouse pointer into the picture.</summary>
    public bool ShowCursor { get; init; } = true;

    /// <summary>
    /// On Wayland: the screen or window the desktop's portal handed over (see <see cref="PortalScreenCast"/>), which is what is
    /// shared instead of a monitor or window of this list. The sharer owns it from the moment sharing starts and ends it with the share.
    /// </summary>
    public PortalScreenCast? Portal { get; init; }

    /// <summary>
    /// The X11 display to copy (like ":0"), which makes the capture FFmpeg's x11grab instead of Windows Graphics Capture. On Linux
    /// it is the DISPLAY variable; elsewhere null.
    /// </summary>
    public string? X11Display { get; init; } = OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("DISPLAY") : null;

    /// <summary>The same options for another monitor or window: everything else (quality, codec, sound) stays.</summary>
    public ScreenOptions Retarget(Display? display, long? windowHandle) => new()
    {
        Display = display, WindowHandle = windowHandle, Fps = Fps, MaxWidth = MaxWidth, MaxHeight = MaxHeight, Kbps = Kbps,
        KeyFrameSeconds = KeyFrameSeconds, Codec = Codec, Encoder = Encoder, ShareSound = ShareSound, SoundProgram = SoundProgram, GpuScale = GpuScale, ShowCursor = ShowCursor,
        X11Display = X11Display,
    };

    /// <summary>The same options for another stream from the portal (the desktop asked again what to share).</summary>
    public ScreenOptions Retarget(PortalScreenCast portal) => new()
    {
        Portal = portal, Fps = Fps, MaxWidth = MaxWidth, MaxHeight = MaxHeight, Kbps = Kbps, KeyFrameSeconds = KeyFrameSeconds, Codec = Codec,
        Encoder = Encoder, ShareSound = ShareSound, SoundProgram = SoundProgram, GpuScale = false, ShowCursor = ShowCursor, X11Display = X11Display,
    };
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
    public static List<string> Capture(ScreenOptions o) => Capture(o, o.Codec, o.Encoder ?? HardwareEncoder.Nvenc);

    /// <summary>
    /// The same for one codec. <see cref="ScreenCodec.H264"/> writes an Annex B stream (read with <see cref="H264Reader"/>),
    /// anything else VP8 in IVF; <see cref="ScreenCodec.Auto"/> is for the sharer to resolve and here means VP8, which needs no
    /// particular hardware. For H.264 the encoder says which graphics card makes it.
    /// </summary>
    public static List<string> Capture(ScreenOptions o, ScreenCodec codec, HardwareEncoder encoder = HardwareEncoder.Nvenc, bool gpuScale = false)
    {
        if (o.Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(o), "Frames per second must be between 1 and 60.");
        if (o.Kbps is < 50 or > 20_000) throw new ArgumentOutOfRangeException(nameof(o), "The bitrate must be between 50 and 20000 kbit/s.");
        if (o.MaxWidth < 16 || o.MaxHeight < 16) throw new ArgumentOutOfRangeException(nameof(o), "The picture box is too small.");

        if (o.Portal is { } portal) return CapturePortal(o, portal.Stream, codec, encoder);

        // On X11 the input is x11grab, which polls the screen at the frame rate (so a still screen still gives a steady stream) and
        // delivers plain pixels; there is nothing on a graphics card to keep them on.
        if (o.X11Display is { Length: > 0 } x11) return CaptureX11(o, x11, codec, encoder);

        string source;
        if (o.WindowHandle is { } window) source = "hwnd=" + Hex(window);
        else if (o.Display is { Handle: not 0 } display) source = "hmonitor=" + Hex(display.Handle);
        else throw new ArgumentException("Choose a monitor or a window to share.", nameof(o));

        // Even sizes only: the encoder works on pairs of pixels. A window that is resized keeps its shape inside the first size.
        var capture = $"gfxcapture={source}:max_framerate={o.Fps.ToString(CultureInfo.InvariantCulture)}:capture_cursor={(o.ShowCursor ? 1 : 0)}:resize_mode=scale_aspect:dup_frames=1";
        string filter;
        if (gpuScale && codec == ScreenCodec.H264 && encoder == HardwareEncoder.Nvenc && o.WindowHandle == null && o.Display is { Handle: not 0, Width: > 0, Height: > 0 } shown)
        {
            // The frames stay on the graphics card: gfxcapture scales them to the size asked for there (the filter after it,
            // scale_d3d11, cannot take its frames) and NVENC converts them itself. Measured in issue #100. The shape is kept
            // by working the size out here, in even numbers and never above the source, since gfxcapture would add bars
            // instead. Only for a monitor, whose size is known; a window is resized while shared, so it takes the processor path.
            double factor = Math.Min(1.0, Math.Min((double)o.MaxWidth / shown.Width, (double)o.MaxHeight / shown.Height));
            int w = Math.Max(2, (int)(shown.Width * factor) / 2 * 2), h = Math.Max(2, (int)(shown.Height * factor) / 2 * 2);
            filter = w == shown.Width && h == shown.Height ? capture : capture + $":width={w}:height={h}";
        }
        else
        {
            filter = capture + ","
                + "hwdownload,format=bgra,"
                // Intel's encoder takes NV12 and not planar YUV; the others take either.
                + $"scale='min({o.MaxWidth},iw)':'min({o.MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format={HardwareEncoders.PixelFormat(codec, encoder)}";
        }
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-filter_complex", filter };
        return AddEncoder(args, o, codec, encoder);
    }

    /// <summary>
    /// FFmpeg's side of a Wayland share: raw planar pictures at the size <see cref="PortalPipeline.FrameSize"/> says come in on
    /// standard input (from GStreamer, see <see cref="PortalPipeline"/>), and are encoded like any other capture.
    /// </summary>
    public static List<string> CapturePortal(ScreenOptions o, PortalStream stream, ScreenCodec codec, HardwareEncoder encoder = HardwareEncoder.Nvenc)
    {
        if (o.Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(o), "Frames per second must be between 1 and 60.");
        if (o.Kbps is < 50 or > 20_000) throw new ArgumentOutOfRangeException(nameof(o), "The bitrate must be between 50 and 20000 kbit/s.");
        if (o.MaxWidth < 16 || o.MaxHeight < 16) throw new ArgumentOutOfRangeException(nameof(o), "The picture box is too small.");
        var (width, height) = PortalPipeline.FrameSize(stream, o);
        // No -nostdin: the pictures arrive on standard input.
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (codec == ScreenCodec.H264 && encoder == HardwareEncoder.Vaapi) args.AddRange(new[] { "-vaapi_device", HardwareEncoders.VaapiDevice() });
        args.AddRange(new[] { "-f", "rawvideo", "-pixel_format", "yuv420p", "-video_size", $"{width}x{height}", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0" });
        args.AddRange(new[] { "-vf", "format=" + HardwareEncoders.PixelFormat(codec, encoder) });
        return AddEncoder(args, o, codec, encoder);
    }

    // The monitor is copied with x11grab: its rectangle (when xrandr gave one) is picked out of the display with the offset in the
    // input name, then scaled down and converted like the Windows path. A window is grabbed by its id.
    private static List<string> CaptureX11(ScreenOptions o, string x11, ScreenCodec codec, HardwareEncoder encoder)
    {
        if (o.WindowHandle == null && o.Display == null) throw new ArgumentException("Choose a monitor or a window to share.", nameof(o));
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        // VA-API encodes on a render device that is named up front; the frames are uploaded to it at the end of the filter chain.
        if (codec == ScreenCodec.H264 && encoder == HardwareEncoder.Vaapi) args.AddRange(new[] { "-vaapi_device", HardwareEncoders.VaapiDevice() });
        args.AddRange(new[] { "-f", "x11grab", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-draw_mouse", o.ShowCursor ? "1" : "0" });
        if (o.WindowHandle is { } window)
            args.AddRange(new[] { "-window_id", Hex(window), "-i", x11 });   // the window keeps the size it had when sharing began
        else
        {
            var shown = o.Display!;
            if (shown.Width > 0 && shown.Height > 0) args.AddRange(new[] { "-video_size", $"{shown.Width}x{shown.Height}" });
            args.AddRange(new[] { "-i", $"{x11}+{shown.X.ToString(CultureInfo.InvariantCulture)},{shown.Y.ToString(CultureInfo.InvariantCulture)}" });
        }
        args.AddRange(new[] { "-vf",
            $"scale='min({o.MaxWidth},iw)':'min({o.MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format={HardwareEncoders.PixelFormat(codec, encoder)}" });
        return AddEncoder(args, o, codec, encoder);
    }

    // What the two capture paths share: the codec, its options and the rate, writing to standard output.
    private static List<string> AddEncoder(List<string> args, ScreenOptions o, ScreenCodec codec, HardwareEncoder encoder)
    {
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
            // AMD's and Intel's encoders get the same treatment with their own option names: low latency, no B-frames, IDR key
            // frames, a delimiter in front of each picture. (The options are checked against the real encoders in the integration tests.)
            args.AddRange(encoder switch
            {
                HardwareEncoder.Amf => new[] { "-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "speed", "-rc", "vbr_latency", "-bf", "0",
                    "-profile:v", "high", "-forced_idr", "1", "-aud", "1", "-async_depth", "1" },
                HardwareEncoder.Vaapi => new[] { "-c:v", "h264_vaapi", "-profile:v", "high", "-bf", "0", "-aud", "1", "-async_depth", "1" },
                HardwareEncoder.Qsv => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-scenario", "displayremoting", "-async_depth", "1", "-bf", "0",
                    "-profile:v", "high", "-forced_idr", "1", "-aud", "1" },
                _ => new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-rc", "vbr", "-rc-lookahead", "0", "-bf", "0",
                    "-profile:v", "high", "-forced-idr", "1", "-aud", "1", "-zerolatency", "1" },
            });
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

    /// <summary>
    /// The viewer's decoder: the same input, but the pictures come out as raw NV12 (see <see cref="Nv12"/>), which is much cheaper
    /// for FFmpeg to produce than BGRA. With <paramref name="gpu"/> (H.264 only) the decoding runs on the graphics card through
    /// Direct3D 11, which any maker's card supports, and only the finished picture is copied back. FFmpeg stops with an error if
    /// the card or driver cannot, and the viewer then starts again without it.
    /// </summary>
    public static List<string> DecodeForViewer(ScreenCodec codec, bool gpu)
    {
        gpu &= codec == ScreenCodec.H264;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-flags", "low_delay", "-probesize", "32", "-analyzeduration", "0" };
        if (gpu) args.AddRange(new[] { "-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11" });
        args.AddRange(new[] { "-f", codec == ScreenCodec.H264 ? "h264" : "ivf", "-i", "pipe:0" });
        if (gpu) args.AddRange(new[] { "-vf", "hwdownload,format=nv12" });
        args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "nv12", "pipe:1" });
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
