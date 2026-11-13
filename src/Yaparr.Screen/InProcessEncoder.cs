using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen;

namespace Yaparr.Screen;

/// <summary>
/// Finds and loads the FFmpeg libraries that sit next to the app (or in the folder YAPARR_FFMPEG_LIBS names), for
/// <see cref="InProcessEncoder"/>. They are shared libraries on purpose, so that anyone can replace them (the LGPL asks for that),
/// and only libraries under the LGPL are used: a build with GPL or non-free parts is refused.
/// </summary>
public static class FfmpegLibraries
{
    private static readonly object Gate = new();
    private static bool _tried, _ok;
    private static string _why = "", _info = "";

    /// <summary>The folder with FFmpeg's libraries, or null when there is none.</summary>
    public static string? Find(string? appDirectory = null, string? explicitFolder = null, string? installDirectory = null)
    {
        explicitFolder ??= Environment.GetEnvironmentVariable("YAPARR_FFMPEG_LIBS");
        appDirectory ??= AppContext.BaseDirectory;
        // The libraries come in the same package as ffmpeg.exe (see FfmpegPackage), so the folder of the FFmpeg the app would run, bundled next to the
        // app or downloaded by it, has them too.
        string? installed = null;
        try { installed = Path.GetDirectoryName(FfmpegLocator.Find(appDirectory, pathVariable: "", explicitPath: "", installDirectory: installDirectory)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        foreach (var dir in new[] { explicitFolder, Path.Combine(appDirectory, "ffmpeg"), appDirectory, installed })
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && HasLibrary(dir)) return Path.GetFullPath(dir);
        return null;
    }

    private static bool HasLibrary(string dir)
    {
        try { return Directory.EnumerateFiles(dir, OperatingSystem.IsWindows() ? "avcodec-*.dll" : "libavcodec.so*").Any(); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Loads the libraries (once). When they cannot be used, <paramref name="why"/> says why in plain words.</summary>
    public static bool TryLoad(out string why)
    {
        lock (Gate)
        {
            if (!_tried)
            {
                _tried = true;
                (_ok, _why) = Load();
            }
            why = _why;
            return _ok;
        }
    }

    /// <summary>Where the loaded libraries are, their versions and licence, for the log; empty when none are loaded.</summary>
    public static string Describe() { lock (Gate) return _ok ? _info : ""; }

    /// <summary>Whether libraries are there and load, without saying why not.</summary>
    public static bool IsAvailable => TryLoad(out _);

    // FFmpeg's libraries need others that sit in the same folder (dav1d, libvpx, the oneVPL dispatcher, winpthreads and the compiler's runtime, see
    // Yaparr.FFmpeg). The bindings load each library by its full path, and Windows then looks for the ones that library needs in the folder of the app
    // and the system folders, not in the folder of the library, so the library fails to load. Loading them first, by their full paths, puts them in the
    // process under their names, and Windows finds them there. The runtime comes first because the others need it.
    private static void PreloadDependencies(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        var ownLibraries = new System.Text.RegularExpressions.Regex(@"^(av(codec|filter|format|device|util)|sw(scale|resample)|postproc)-\d+\.dll$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var first = new[] { "libwinpthread-1.dll", "libgcc_s_seh-1.dll", "libstdc++-6.dll" };
        var others = Directory.EnumerateFiles(dir, "*.dll").Select(Path.GetFileName).Where(n => n != null && !ownLibraries.IsMatch(n!) && !first.Contains(n!, StringComparer.OrdinalIgnoreCase));
        foreach (var name in first.Concat(others!))
        {
            var path = Path.Combine(dir, name!);
            if (File.Exists(path)) NativeLibrary.TryLoad(path, out _);
        }
    }

    private static (bool, string) Load()
    {
        var dir = Find();
        if (dir == null) return (false, "FFmpeg's libraries were not found next to the app (an 'ffmpeg' folder) or in YAPARR_FFMPEG_LIBS.");
        try
        {
            PreloadDependencies(dir);
            ffmpeg.RootPath = dir;
            DynamicallyLoadedBindings.Initialize();
            string licence = ffmpeg.avcodec_license() ?? "";
            if (!licence.StartsWith("LGPL", StringComparison.OrdinalIgnoreCase))
                return (false, $"The FFmpeg libraries in {dir} are under the \"{licence}\" licence; only LGPL ones are used.");
            static string Version(uint v) => $"{v >> 16}.{(v >> 8) & 255}.{v & 255}";
            _info = $"{dir} (avcodec {Version(ffmpeg.avcodec_version())}, avutil {Version(ffmpeg.avutil_version())}, {licence})";
            return (true, "");
        }
        catch (Exception ex) when (ex is DllNotFoundException or NotSupportedException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return (false, $"The FFmpeg libraries in {dir} could not be loaded: {ex.Message}");
        }
    }
}

/// <summary>
/// Reads the size of the pictures FFmpeg writes from what it prints when it starts (the output stream's line), so raw pictures
/// can be cut out of its output without knowing the size beforehand: a window's size, and the scaling to fit, are FFmpeg's to work out.
/// </summary>
public sealed class RawSizeParser
{
    private static readonly Regex Size = new(@"Video: .*?, (\d{2,5})x(\d{2,5})[\s,\[]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private bool _inOutput;

    /// <summary>Feed each line FFmpeg prints; true once the output picture size is known.</summary>
    public bool Feed(string line, out int width, out int height)
    {
        width = height = 0;
        if (line.StartsWith("Output #", StringComparison.Ordinal)) { _inOutput = true; return false; }
        if (!_inOutput || !line.TrimStart().StartsWith("Stream #", StringComparison.Ordinal)) return false;
        if (Size.Match(line) is not { Success: true } m) return false;
        width = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        height = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}

/// <summary>
/// H.264 on NVIDIA's encoder (or, on Linux, on an Intel or AMD card through VA-API), run inside this program through FFmpeg's libraries instead of as a separate FFmpeg: the same options
/// (see <see cref="FfmpegArgs.NvencH264Options"/>), but each picture is handed over and each packet comes straight back, so a
/// key frame can be asked for on the very next picture (<see cref="Encode"/>), which the command-line FFmpeg cannot do, and the
/// encoder can be told a new bitrate later. Pictures go in as NV12, <see cref="PictureBytes"/> bytes each.
/// </summary>
public sealed unsafe class InProcessEncoder : IDisposable
{
    private AVCodecContext* _context;
    private AVBufferRef* _hardwareDevice, _uploadFrames;
    private AVFrame* _uploaded;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private long _pts;

    public int Width { get; }
    public int Height { get; }
    private string _description = "";

    /// <summary>How the encoder was set up and which libraries are used, for the log.</summary>
    public string Describe() => _description;

    /// <summary>The size of one picture handed to <see cref="Encode"/>: NV12, so 12 bits a pixel.</summary>
    public int PictureBytes => Width * Height * 3 / 2;

    private bool _onCard;

    /// <summary>Pictures are read from the graphics card (see <see cref="InProcessCapture.OnCard"/>), not handed over as bytes.</summary>
    public bool OnCard => _onCard;

    public InProcessEncoder(int width, int height, ScreenOptions options) : this(width, height, options, null) { }

    /// <summary>The graphics card this encoder runs on: NVIDIA's, or an Intel or AMD one through VA-API (Linux; the pictures are handed over as NV12 bytes and uploaded to the card).</summary>
    public HardwareEncoder Kind { get; private set; } = HardwareEncoder.Nvenc;

    /// <summary>Whether <see cref="SetBitrate"/> takes effect while encoding. NVIDIA's encoder is reconfigured on the fly; VA-API's rate control is set when the encoder starts, so a new bitrate there needs a new encoder.</summary>
    public bool LiveBitrate => Kind == HardwareEncoder.Nvenc;

    /// <summary>An encoder for a card that takes NV12 bytes (see <see cref="Kind"/>).</summary>
    public InProcessEncoder(int width, int height, ScreenOptions options, HardwareEncoder kind) : this(width, height, options, null, kind) { }

    /// <summary>
    /// With <paramref name="capture"/> keeping its pictures on the card, the encoder takes Direct3D 11 pictures from it as they are (NVENC converts them
    /// itself); otherwise it takes NV12 bytes.
    /// </summary>
    public InProcessEncoder(int width, int height, ScreenOptions options, InProcessCapture? capture) : this(width, height, options, capture, HardwareEncoder.Nvenc) { }

    /// <summary>An encoder for a card (see <see cref="Kind"/>) that takes the pictures of <paramref name="capture"/> as they are (NV12 in memory, or on NVIDIA's card).</summary>
    public InProcessEncoder(int width, int height, ScreenOptions options, InProcessCapture? capture, HardwareEncoder kind)
    {
        if (!FfmpegLibraries.TryLoad(out var why)) throw new PlatformNotSupportedException(why);
        if (width < 16 || height < 16 || width % 2 != 0 || height % 2 != 0) throw new ArgumentException($"A picture of {width}x{height} cannot be encoded.");
        if (kind is not (HardwareEncoder.Nvenc or HardwareEncoder.Vaapi)) throw new PlatformNotSupportedException($"The encoder in this program does not do {kind} yet.");
        Width = width;
        Height = height;
        Kind = kind;
        var codec = ffmpeg.avcodec_find_encoder_by_name(kind == HardwareEncoder.Vaapi ? "h264_vaapi" : "h264_nvenc");
        if (codec == null) throw new PlatformNotSupportedException(kind == HardwareEncoder.Vaapi ? "These FFmpeg libraries have no VA-API H.264 encoder." : "These FFmpeg libraries have no NVIDIA H.264 encoder.");
        _context = ffmpeg.avcodec_alloc_context3(codec);
        try
        {
            if (kind == HardwareEncoder.Vaapi) { StartVaapi(codec, width, height, options); return; }
            _context->width = width;
            _context->height = height;
            _context->time_base = new AVRational { num = 1, den = options.Fps };
            _context->framerate = new AVRational { num = options.Fps, den = 1 };
            _onCard = capture is { OnCard: true };
            _context->pix_fmt = _onCard ? AVPixelFormat.AV_PIX_FMT_D3D11 : AVPixelFormat.AV_PIX_FMT_NV12;
            if (_onCard) _context->hw_frames_ctx = ffmpeg.av_buffer_ref(capture!.HardwareFrames);
            Kbps = options.Kbps;
            _context->bit_rate = options.Kbps * 1000L;
            _context->rc_max_rate = (long)(options.Kbps * 1500L);
            _context->rc_buffer_size = (int)(options.Kbps * 1500L);
            _context->gop_size = Math.Max(1, (int)Math.Round(options.Fps * options.KeyFrameSeconds));
            _context->max_b_frames = 0;
            foreach (var (name, value) in FfmpegArgs.NvencH264Options) SetOption(name, value);
            // Hand each packet back as soon as it is made: by default NVENC holds two pictures back, which is two pictures of delay.
            SetOption("delay", "0");
            Check(ffmpeg.avcodec_open2(_context, codec, null), "starting the NVIDIA encoder");
            _description = $"h264_nvenc {width}x{height} at {options.Fps} fps, {options.Kbps} kbit/s (at most {options.Kbps * 1.5:0}, buffer {options.Kbps * 1.5:0}), "
                           + $"a key frame every {_context->gop_size} pictures, options {string.Join(' ', FfmpegArgs.NvencH264Options.Select(o => o.Name + "=" + o.Value))} delay=0; "
                           + (_onCard ? "pictures read from the graphics card (Direct3D 11); " : "") + $"libraries {FfmpegLibraries.Describe()}";
            if (!_onCard)
            {
                _frame = ffmpeg.av_frame_alloc();
                _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                _frame->width = width;
                _frame->height = height;
                Check(ffmpeg.av_frame_get_buffer(_frame, 0), "allocating a picture");
            }
            _packet = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // VA-API: the card is opened through its render node (/dev/dri/renderD128 and so on, or the one YAPARR_VAAPI_DEVICE names), the encoder takes pictures that live
    // in the card's memory, and each NV12 picture handed over is uploaded into one first. The settings are those of the command line (HardwareEncoders, Ffmpeg.cs):
    // High profile, no B-frames, access unit delimiters, one picture in flight.
    private void StartVaapi(AVCodec* codec, int width, int height, ScreenOptions options)
    {
        string device = HardwareEncoders.VaapiDevice();
        AVBufferRef* hardware = null;
        Check(ffmpeg.av_hwdevice_ctx_create(&hardware, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, device, null, 0), $"opening the graphics card {device} for VA-API");
        _hardwareDevice = hardware;
        var frames = ffmpeg.av_hwframe_ctx_alloc(_hardwareDevice);
        if (frames == null) throw new InvalidOperationException("allocating the pictures on the graphics card failed.");
        var framesContext = (AVHWFramesContext*)frames->data;
        framesContext->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
        framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesContext->width = width;
        framesContext->height = height;
        framesContext->initial_pool_size = 8;
        int init = ffmpeg.av_hwframe_ctx_init(frames);
        if (init < 0) { var f = frames; ffmpeg.av_buffer_unref(&f); Check(init, "preparing the pictures on the graphics card"); }
        _uploadFrames = frames;

        _context->width = width;
        _context->height = height;
        _context->time_base = new AVRational { num = 1, den = options.Fps };
        _context->framerate = new AVRational { num = options.Fps, den = 1 };
        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
        _context->hw_frames_ctx = ffmpeg.av_buffer_ref(_uploadFrames);
        Kbps = options.Kbps;
        _context->bit_rate = options.Kbps * 1000L;
        _context->rc_max_rate = options.Kbps * 1500L;
        _context->rc_buffer_size = (int)(options.Kbps * 1500L);
        _context->gop_size = Math.Max(1, (int)Math.Round(options.Fps * options.KeyFrameSeconds));
        _context->max_b_frames = 0;
        _context->profile = 100;   // High
        SetOption("aud", "1");
        SetOption("async_depth", "1");
        Check(ffmpeg.avcodec_open2(_context, codec, null), "starting the VA-API encoder");
        _description = $"h264_vaapi {width}x{height} at {options.Fps} fps, {options.Kbps} kbit/s (at most {options.Kbps * 1.5:0}, buffer {options.Kbps * 1.5:0}), "
                       + $"a key frame every {_context->gop_size} pictures, options profile=high bf=0 aud=1 async_depth=1; card {device}; libraries {FfmpegLibraries.Describe()}";
        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        _frame->width = width;
        _frame->height = height;
        Check(ffmpeg.av_frame_get_buffer(_frame, 0), "allocating a picture");
        _uploaded = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    // Copies the NV12 picture in _frame to a picture on the card, which is what the encoder takes.
    private AVFrame* UploadFrame() => UploadFrom(_frame);

    // The same for any NV12 picture in memory (the capture's, as it is).
    private AVFrame* UploadFrom(AVFrame* picture)
    {
        ffmpeg.av_frame_unref(_uploaded);
        Check(ffmpeg.av_hwframe_get_buffer(_uploadFrames, _uploaded, 0), "getting a picture on the graphics card");
        Check(ffmpeg.av_hwframe_transfer_data(_uploaded, picture, 0), "copying a picture to the graphics card");
        return _uploaded;
    }

    private void SetOption(string name, string value) => Check(ffmpeg.av_opt_set(_context->priv_data, name, value, 0), $"setting the encoder option {name}");

    private static void Check(int result, string what)
    {
        if (result >= 0) return;
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi((IntPtr)buffer)}");
    }

    /// <summary>
    /// Encodes one picture. <paramref name="forceKey"/> makes it an IDR picture (with the parameter sets, as every key frame has).
    /// Each packet that comes out is handed to <paramref name="output"/> with whether it is a key frame; with no delay that is exactly one.
    /// </summary>
    public void Encode(ReadOnlySpan<byte> picture, bool forceKey, Action<byte[], bool> output)
    {
        if (_onCard) throw new InvalidOperationException("This encoder takes its pictures from the graphics card, not as bytes.");
        if (picture.Length != PictureBytes) throw new ArgumentException($"A picture is {PictureBytes} bytes, not {picture.Length}.", nameof(picture));
        if (_context == null) throw new ObjectDisposedException(nameof(InProcessEncoder));
        Check(ffmpeg.av_frame_make_writable(_frame), "preparing a picture");
        fixed (byte* source = picture)
        {
            CopyPlane(source, Width, _frame->data[0], _frame->linesize[0], Height);
            CopyPlane(source + (long)Width * Height, Width, _frame->data[1], _frame->linesize[1], Height / 2);
        }
        var toSend = Kind == HardwareEncoder.Vaapi ? UploadFrame() : _frame;
        toSend->pts = _pts++;
        toSend->pict_type = forceKey ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        Check(ffmpeg.avcodec_send_frame(_context, toSend), "encoding a picture");
        ReceivePackets(output);
    }

    /// <summary>
    /// Encodes a picture that <see cref="InProcessCapture"/> made, as it is: the encoder reads it from FFmpeg's memory, so nothing is copied here.
    /// Otherwise as <see cref="Encode(ReadOnlySpan{byte}, bool, Action{byte[], bool})"/>.
    /// </summary>
    public void Encode(CapturedPicture picture, bool forceKey, Action<byte[], bool> output)
    {
        if (_context == null) throw new ObjectDisposedException(nameof(InProcessEncoder));
        var frame = picture.Frame;
        if (frame == null) throw new ObjectDisposedException(nameof(CapturedPicture));
        var format = _onCard ? AVPixelFormat.AV_PIX_FMT_D3D11 : AVPixelFormat.AV_PIX_FMT_NV12;
        if (frame->format != (int)format || frame->width != Width || frame->height != Height)
            throw new ArgumentException($"The encoder takes {(_onCard ? "Direct3D 11" : "NV12")} pictures of {Width}x{Height}, not {frame->width}x{frame->height}.", nameof(picture));
        if (Kind == HardwareEncoder.Vaapi)
        {
            // Up to the card first (the capture's picture is not changed: it may be repeated by the share's clock), and that is what is encoded.
            var onCard = UploadFrom(frame);
            onCard->pts = _pts++;
            onCard->pict_type = forceKey ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
            Check(ffmpeg.avcodec_send_frame(_context, onCard), "encoding a picture");
            ReceivePackets(output);
            return;
        }
        frame->pts = _pts++;
        frame->pict_type = forceKey ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        Check(ffmpeg.avcodec_send_frame(_context, frame), "encoding a picture");
        ReceivePackets(output);
    }

    /// <summary>The bitrate the encoder was last given, in kbit/s.</summary>
    public int Kbps { get; private set; }

    /// <summary>
    /// Changes the bitrate while encoding, without a restart: the average, the ceiling and the buffer move together (the ceiling and the buffer are
    /// one and a half times the average, as when the encoder was started). Whether the encoder follows is what the caller has to measure.
    /// </summary>
    public void SetBitrate(int kbps)
    {
        if (_context == null) throw new ObjectDisposedException(nameof(InProcessEncoder));
        if (!LiveBitrate) throw new NotSupportedException("This encoder cannot be given a new bitrate while it runs.");
        if (kbps < 50) throw new ArgumentOutOfRangeException(nameof(kbps));
        _context->bit_rate = kbps * 1000L;
        _context->rc_max_rate = kbps * 1500L;
        _context->rc_buffer_size = (int)(kbps * 1500L);
        Kbps = kbps;
    }

    private void ReceivePackets(Action<byte[], bool> output)
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
            Check(result, "reading an encoded picture");
            try { output(new ReadOnlySpan<byte>(_packet->data, _packet->size).ToArray(), (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0); }
            finally { ffmpeg.av_packet_unref(_packet); }
        }
    }

    // One block when the rows are packed the same way on both sides (the usual case), row by row when the encoder pads them.
    private static void CopyPlane(byte* from, int rowBytes, byte* to, int toStride, int rows)
    {
        if (toStride == rowBytes) { Buffer.MemoryCopy(from, to, (long)rowBytes * rows, (long)rowBytes * rows); return; }
        for (int y = 0; y < rows; y++) Buffer.MemoryCopy(from + (long)y * rowBytes, to + (long)y * toStride, rowBytes, rowBytes);
    }

    public void Dispose()
    {
        if (_uploaded != null) { var u = _uploaded; ffmpeg.av_frame_free(&u); _uploaded = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_context != null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        if (_uploadFrames != null) { var fr = _uploadFrames; ffmpeg.av_buffer_unref(&fr); _uploadFrames = null; }
        if (_hardwareDevice != null) { var d = _hardwareDevice; ffmpeg.av_buffer_unref(&d); _hardwareDevice = null; }
    }
}
