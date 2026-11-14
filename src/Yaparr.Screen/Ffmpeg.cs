using System.Globalization;

namespace Yaparr.Screen;

/// <summary>
/// How the picture is compressed. Auto uses the graphics card when there is one that can (H264 on NVIDIA's NVENC, AMD's AMF or
/// Intel's Quick Sync) and VP8 in software otherwise. Av1 is asked for on purpose: only graphics cards encode it live (software AV1 is too slow for a
/// screen at 30 or 60 frames a second), and a viewer needs a decoder for it, so Auto never picks it; where no card can, the share falls back to H.264.
/// </summary>
public enum ScreenCodec { Auto, Vp8, H264, Av1 }

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

    /// <summary>FFmpeg's name for the H.264 encoder.</summary>
    public static string Name(HardwareEncoder encoder) => Name(ScreenCodec.H264, encoder);

    /// <summary>FFmpeg's name for the encoder of this codec (H.264 or AV1).</summary>
    public static string Name(ScreenCodec codec, HardwareEncoder encoder)
    {
        string kind = codec == ScreenCodec.Av1 ? "av1" : "h264";
        return encoder switch
        {
            HardwareEncoder.Nvenc => kind + "_nvenc",
            HardwareEncoder.Amf => kind + "_amf",
            HardwareEncoder.Vaapi => kind + "_vaapi",
            _ => kind + "_qsv",
        };
    }

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
        codec is not (ScreenCodec.H264 or ScreenCodec.Av1) ? "yuv420p" : encoder switch { HardwareEncoder.Qsv => "nv12", HardwareEncoder.Vaapi => "nv12,hwupload", _ => "yuv420p" };

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

    /// <summary>
    /// Seconds between key frames. A viewer that joins or loses packets waits at most this long for one (a viewer that asks for one gets it at once, see
    /// <see cref="InProcessEncoder"/>). The environment variable YAPARR_KEY_SECONDS changes the default, to try another spacing.
    /// </summary>
    public double KeyFrameSeconds { get; init; } = EncoderTuning.KeySeconds;

    /// <summary>
    /// How much the encoder's rate buffer holds, in seconds of the bitrate (the VBV size): the smaller it is, the less the encoder may put out at once, and a key
    /// frame is made smaller (and worse) to fit. A key frame sent as a burst is what a weak link loses packets in. The environment variable YAPARR_ENCODER_BUFFER
    /// changes the default. See <see cref="EncoderTuning"/>.
    /// </summary>
    public double BufferSeconds { get; init; } = EncoderTuning.BufferSeconds;

    /// <summary>The encoder's ceiling, as a multiple of the bitrate (YAPARR_ENCODER_MAXRATE changes the default). See <see cref="EncoderTuning"/>.</summary>
    public double MaxRateMultiple { get; init; } = EncoderTuning.MaxRateMultiple;

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

    /// <summary>
    /// The in-process mode was asked for in so many words (YAPARR_INPROCESS has a value), not left to the default: then <see cref="InProcessGuard"/>, which
    /// turns the default off after programs that ended in the middle of such a share, does not stop it.
    /// </summary>
    public bool InProcessAsked { get; init; } = InProcessSettings.FromEnvironment().Asked;

    /// <summary>
    /// The pictures stay on the graphics card from the capture to the encoder (Windows, NVIDIA's and AMD's encoders: the capture scales them there, and the
    /// encoder converts them itself), so nothing is copied to memory and converted on the processor. That needs the size of the picture up front (a monitor's,
    /// or a window's as it is when sharing begins); where it cannot be had, or the card refuses, the pictures are copied to memory. On unless YAPARR_INPROCESS is
    /// "capture-cpu" (the copy to memory only, for comparing), and off after the program ended in the middle of a share twice in a row (see <see cref="InProcessGuard"/>).
    /// </summary>
    public bool CaptureOnCard { get; init; } = InProcessSettings.FromEnvironment().Mode == InProcessMode.CaptureOnCard;

    /// <summary>
    /// Lower the quality when the viewers cannot keep up, and raise it again when they can (see <see cref="QualityPolicy"/>): one preset of the share dialog at a
    /// time (see <see cref="QualityLadder"/>), and only when the encoder is in this program; otherwise what the policy would do is written down and nothing more.
    /// On unless <c>YAPARR_ADAPT</c> is 0 or off, and the share dialog's setting turns it off too.
    /// </summary>
    public bool AdaptQuality { get; init; } = !(Environment.GetEnvironmentVariable("YAPARR_ADAPT")?.Trim().ToLowerInvariant() is "0" or "off" or "no" or "false");

    /// <summary>Checks the numbers before anything is announced: the frame rate, the bitrate and the box of the picture. Throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    public void Validate()
    {
        if (Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(Fps), "Frames per second must be between 1 and 60.");
        if (Kbps is < 50 or > 20_000) throw new ArgumentOutOfRangeException(nameof(Kbps), "The bitrate must be between 50 and 20000 kbit/s.");
        if (MaxWidth < 16 || MaxHeight < 16) throw new ArgumentOutOfRangeException(nameof(MaxWidth), "The picture box is too small.");
        if (Portal == null && WindowHandle == null && Display == null) throw new ArgumentException("Choose a monitor or a window to share.");
    }

    /// <summary>The same options at another level of adaptive quality (see <see cref="QualityLadder"/>): the bitrate, the frame rate and the box of the picture.</summary>
    public ScreenOptions WithQuality(int kbps, int fps, int maxWidth, int maxHeight) => new()
    {
        Display = Display, WindowHandle = WindowHandle, Portal = Portal, Fps = fps, MaxWidth = maxWidth, MaxHeight = maxHeight, Kbps = kbps,
        KeyFrameSeconds = KeyFrameSeconds, Codec = Codec, Encoder = Encoder, ShareSound = ShareSound, SoundProgram = SoundProgram, ShowCursor = ShowCursor,
        X11Display = X11Display, CaptureOnCard = CaptureOnCard, InProcessAsked = InProcessAsked, AdaptQuality = AdaptQuality,
        BufferSeconds = BufferSeconds, MaxRateMultiple = MaxRateMultiple,
    };

    /// <summary>The same options for another monitor or window: everything else (quality, codec, sound) stays.</summary>
    public ScreenOptions Retarget(Display? display, long? windowHandle) => new()
    {
        Display = display, WindowHandle = windowHandle, Fps = Fps, MaxWidth = MaxWidth, MaxHeight = MaxHeight, Kbps = Kbps,
        KeyFrameSeconds = KeyFrameSeconds, Codec = Codec, Encoder = Encoder, ShareSound = ShareSound, SoundProgram = SoundProgram, ShowCursor = ShowCursor,
        X11Display = X11Display, CaptureOnCard = CaptureOnCard, InProcessAsked = InProcessAsked, AdaptQuality = AdaptQuality,
        BufferSeconds = BufferSeconds, MaxRateMultiple = MaxRateMultiple,
    };

    /// <summary>The same options for another stream from the portal (the desktop asked again what to share).</summary>
    public ScreenOptions Retarget(PortalScreenCast portal) => new()
    {
        Portal = portal, Fps = Fps, MaxWidth = MaxWidth, MaxHeight = MaxHeight, Kbps = Kbps, KeyFrameSeconds = KeyFrameSeconds, Codec = Codec,
        Encoder = Encoder, ShareSound = ShareSound, SoundProgram = SoundProgram, ShowCursor = ShowCursor, X11Display = X11Display,
        CaptureOnCard = CaptureOnCard, InProcessAsked = InProcessAsked, AdaptQuality = AdaptQuality,
        BufferSeconds = BufferSeconds, MaxRateMultiple = MaxRateMultiple,
    };
}

/// <summary>
/// The rate settings of the encoders that can be tried with environment variables: the spacing of key frames, the size of the rate buffer in seconds of the bitrate,
/// and the ceiling as a multiple of the bitrate. The defaults are the ones docs/encoder-tuning.md measured to be best: a key frame every two seconds, a buffer of half a second
/// and a ceiling of the bitrate itself (the encoders were set up with one second, one and a half seconds and one and a half times until then).
/// </summary>
public static class EncoderTuning
{
    public const double DefaultKeySeconds = 2, DefaultBufferSeconds = 0.5, DefaultMaxRateMultiple = 1.0;

    public static double KeySeconds => Read("YAPARR_KEY_SECONDS", DefaultKeySeconds, 0.25, 10);
    public static double BufferSeconds => Read("YAPARR_ENCODER_BUFFER", DefaultBufferSeconds, 0.1, 5);
    public static double MaxRateMultiple => Read("YAPARR_ENCODER_MAXRATE", DefaultMaxRateMultiple, 1.0, 4);

    private static double Read(string name, double fallback, double low, double high) => Parse(Environment.GetEnvironmentVariable(name), fallback, low, high);

    /// <summary>A number from text, or <paramref name="fallback"/> when there is none or it is outside the range that makes sense.</summary>
    internal static double Parse(string? text, double fallback, double low, double high)
        => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= low && v <= high ? v : fallback;
}

/// <summary>
/// FFmpeg's own words for what the capture inside this program does (<see cref="InProcessCapture"/> builds a filter graph from this text), and the options of
/// NVIDIA's encoder. Kept apart so that they can be checked without FFmpeg.
/// </summary>
public static class FfmpegArgs
{
    private static string Hex(long v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>The options of <see cref="InProcessEncoder"/> for H.264 on NVIDIA's encoder, as FFmpeg's own names and values (see <see cref="InProcessEncoder.EncoderOptions"/>).</summary>
    public static readonly (string Name, string Value)[] NvencH264Options =
    {
        ("preset", "p4"), ("tune", "ll"), ("rc", "vbr"), ("rc-lookahead", "0"), ("profile", "high"), ("forced-idr", "1"), ("aud", "1"), ("zerolatency", "1"),
    };

    /// <summary>
    /// The filter chain of a Windows capture: Windows Graphics Capture (FFmpeg's gfxcapture filter, which gets the content of browsers and games, is not spoiled by
    /// windows on top, and repeats the last picture when nothing changed with dup_frames=1), copied back to memory, scaled to fit the box (never up, the shape kept,
    /// even numbers) and made NV12. Builds before 24 October 2026 (FFmpeg 8.1.3 and 9.0.2 included) lack dup_frames and the fixes that came with it.
    /// </summary>
    public static string CaptureFilter(ScreenOptions o)
    {
        if (o.Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(o), "Frames per second must be between 1 and 60.");
        if (o.MaxWidth < 16 || o.MaxHeight < 16) throw new ArgumentOutOfRangeException(nameof(o), "The picture box is too small.");
        return Gfxcapture(o) + ",hwdownload,format=bgra,"
               + $"scale='min({o.MaxWidth},iw)':'min({o.MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=nv12";
    }

    private static string Gfxcapture(ScreenOptions o)
    {
        string source;
        if (o.WindowHandle is { } window) source = "hwnd=" + Hex(window);
        else if (o.Display is { Handle: not 0 } display) source = "hmonitor=" + Hex(display.Handle);
        else throw new ArgumentException("Choose a monitor or a window to share.", nameof(o));
        return $"gfxcapture={source}:max_framerate={o.Fps.ToString(CultureInfo.InvariantCulture)}:capture_cursor={(o.ShowCursor ? 1 : 0)}:resize_mode=scale_aspect:dup_frames=1";
    }

    /// <summary>
    /// The capture alone, with the pictures kept on the graphics card: Graphics Capture scales them to the size worked out here (the shape kept, even
    /// numbers, never above the source), and the filter graph ends there. Null when the size is not known: a window that is minimised, or no monitor size.
    /// </summary>
    public static string? CaptureFilterOnCard(ScreenOptions o)
    {
        if (o.Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(o), "Frames per second must be between 1 and 60.");
        if (o.MaxWidth < 16 || o.MaxHeight < 16) throw new ArgumentOutOfRangeException(nameof(o), "The picture box is too small.");
        (int Width, int Height) shown;
        if (o.WindowHandle is { } window)
        {
            if (ShareWindows.CaptureSize(window) is not { } size) return null;
            shown = size;
        }
        else if (o.Display is { Handle: not 0, Width: > 0, Height: > 0 } display) shown = (display.Width, display.Height);
        else return null;
        double factor = Math.Min(1.0, Math.Min((double)o.MaxWidth / shown.Width, (double)o.MaxHeight / shown.Height));
        int w = Math.Max(2, (int)(shown.Width * factor) / 2 * 2), h = Math.Max(2, (int)(shown.Height * factor) / 2 * 2);
        // The size is always given, even when it is the source's own, so that the picture is the size the encoder is told.
        return Gfxcapture(o) + $":width={w}:height={h}";
    }
}
