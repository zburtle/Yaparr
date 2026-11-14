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

    /// <summary>
    /// The folder with FFmpeg's libraries, or null when there is none: the one YAPARR_FFMPEG_LIBS names, an "ffmpeg" folder next to the app (where the Windows
    /// installer puts them), the app's own folder, then the newest package the app downloaded (<see cref="FfmpegInstaller"/>).
    /// </summary>
    public static string? Find(string? appDirectory = null, string? explicitFolder = null, string? installDirectory = null)
    {
        explicitFolder ??= Environment.GetEnvironmentVariable("YAPARR_FFMPEG_LIBS");
        appDirectory ??= AppContext.BaseDirectory;
        foreach (var dir in new[] { explicitFolder, Path.Combine(appDirectory, "ffmpeg"), appDirectory })
            if (!string.IsNullOrWhiteSpace(dir) && FfmpegInstaller.HasLibraries(dir)) return Path.GetFullPath(dir);
        return FfmpegInstaller.FindAny(installDirectory) is { } installed ? Path.GetFullPath(installed) : null;
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

    /// <summary>
    /// Forgets that loading failed, so that the next <see cref="TryLoad"/> looks again (after the libraries have been downloaded). Libraries that did load stay
    /// loaded: a process cannot load a second set.
    /// </summary>
    public static void LookAgain()
    {
        lock (Gate) if (_tried && !_ok) _tried = false;
    }

    /// <summary>Where the loaded libraries are, their versions and licence, for the log; empty when none are loaded.</summary>
    public static string Describe() { lock (Gate) return _ok ? _info : ""; }

    /// <summary>The encoders of the ones Yaparr uses that the loaded libraries have (FFmpeg's names), for the diagnostics.</summary>
    public static IReadOnlyList<string> Encoders() => Have(new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_vaapi", "av1_nvenc", "av1_amf", "av1_qsv", "av1_vaapi", "libvpx" }, encoder: true);

    /// <summary>The decoders of the ones Yaparr uses that the loaded libraries have.</summary>
    public static IReadOnlyList<string> Decoders() => Have(new[] { "h264", "vp8", "libdav1d" }, encoder: false);

    private static unsafe IReadOnlyList<string> Have(string[] names, bool encoder)
    {
        if (!TryLoad(out _)) return Array.Empty<string>();
        return names.Where(n => encoder ? ffmpeg.avcodec_find_encoder_by_name(n) != null : ffmpeg.avcodec_find_decoder_by_name(n) != null).ToList();
    }

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
/// The encoder of a share, run inside this program through FFmpeg's libraries: H.264 or AV1 on a graphics card (NVIDIA's NVENC, AMD's AMF, Intel's Quick Sync, or on
/// Linux an Intel or AMD card through VA-API) or VP8 in software (libvpx). Each picture is handed over and each packet comes straight back, so a key frame can be made on
/// the very next picture (<see cref="Encode(ReadOnlySpan{byte}, bool, Action{byte[], bool})"/>), and NVIDIA's encoder can be told a new bitrate while it runs. The options
/// are the ones each encoder was given on FFmpeg's command line before (<see cref="EncoderOptions"/>). Pictures go in as NV12, <see cref="PictureBytes"/> bytes each, or
/// as the capture's own pictures (<see cref="Encode(CapturedPicture, bool, Action{byte[], bool})"/>), which NVENC and AMF read from the graphics card as they are.
/// </summary>
public sealed unsafe class InProcessEncoder : IDisposable
{
    private AVCodecContext* _context;
    private double _bufferSeconds = EncoderTuning.DefaultBufferSeconds, _maxRateMultiple = EncoderTuning.DefaultMaxRateMultiple;
    private AVBufferRef* _hardwareDevice, _uploadFrames;
    private AVFrame* _uploaded;
    private AVFrame* _frame;         // what goes to the encoder from memory: NV12, or planar 4:2:0 for VP8
    private AVFrame* _nv12;          // VP8: the NV12 picture handed over, before it is made planar
    private SwsContext* _planar;     // VP8: NV12 to planar 4:2:0
    private AVPacket* _packet;
    private AVBSFContext* _filter;   // AV1: a temporal delimiter in front of every picture, as the IVF writer of the command line put there
    private long _pts;

    public int Width { get; }
    public int Height { get; }
    private string _description = "";

    /// <summary>How the encoder was set up and which libraries are used, for the log.</summary>
    public string Describe() => _description;

    /// <summary>The size of one picture handed to <see cref="Encode(ReadOnlySpan{byte}, bool, Action{byte[], bool})"/>: NV12, so 12 bits a pixel.</summary>
    public int PictureBytes => Width * Height * 3 / 2;

    private bool _onCard;

    /// <summary>Pictures are read from the graphics card (see <see cref="InProcessCapture.OnCard"/>), not handed over as bytes.</summary>
    public bool OnCard => _onCard;

    /// <summary>The codec made: H.264, AV1 or VP8.</summary>
    public ScreenCodec Codec { get; }

    /// <summary>The graphics card this encoder runs on; null for VP8, which the processor encodes.</summary>
    public HardwareEncoder? Card { get; }

    /// <summary>The graphics card this encoder runs on (NVIDIA's for VP8, which has none: see <see cref="Card"/>).</summary>
    public HardwareEncoder Kind => Card ?? HardwareEncoder.Nvenc;

    /// <summary>
    /// Whether <see cref="SetBitrate"/> takes effect while encoding. NVIDIA's encoder is reconfigured on the fly; the others' rate control is set when they start,
    /// so a new bitrate there needs a new encoder.
    /// </summary>
    public bool LiveBitrate => Card == HardwareEncoder.Nvenc;

    /// <summary>Whether the capture can keep its pictures on the graphics card for this encoder (Windows: NVENC and AMF read Direct3D 11 pictures as they are).</summary>
    public static bool ReadsFromCard(ScreenCodec codec, HardwareEncoder? card) => OperatingSystem.IsWindows() && codec != ScreenCodec.Vp8 && card is HardwareEncoder.Nvenc or HardwareEncoder.Amf;

    public InProcessEncoder(int width, int height, ScreenOptions options) : this(width, height, options, null) { }

    /// <summary>H.264 on a card that takes NV12 bytes (see <see cref="Card"/>).</summary>
    public InProcessEncoder(int width, int height, ScreenOptions options, HardwareEncoder kind) : this(width, height, options, null, ScreenCodec.H264, kind) { }

    /// <summary>H.264 on NVIDIA's encoder, taking the pictures of <paramref name="capture"/> as they are (from the card when it keeps them there).</summary>
    public InProcessEncoder(int width, int height, ScreenOptions options, InProcessCapture? capture) : this(width, height, options, capture, ScreenCodec.H264, HardwareEncoder.Nvenc) { }

    /// <summary>H.264 on <paramref name="kind"/>, taking the pictures of <paramref name="capture"/> as they are.</summary>
    public InProcessEncoder(int width, int height, ScreenOptions options, InProcessCapture? capture, HardwareEncoder kind) : this(width, height, options, capture, ScreenCodec.H264, kind) { }

    /// <summary>
    /// An encoder for <paramref name="codec"/> on <paramref name="card"/> (ignored for VP8, which the processor encodes). With <paramref name="capture"/> keeping its
    /// pictures on the card, the encoder takes them from there (NVENC and AMF only, see <see cref="ReadsFromCard"/>); otherwise it takes NV12 in memory. Throws
    /// <see cref="PlatformNotSupportedException"/> when the libraries have no such encoder, and <see cref="InvalidOperationException"/> when it will not start (no such card, a
    /// driver that refuses, every session in use): the share then tries the next one.
    /// </summary>
    public InProcessEncoder(int width, int height, ScreenOptions options, InProcessCapture? capture, ScreenCodec codec, HardwareEncoder? card)
    {
        if (!FfmpegLibraries.TryLoad(out var why)) throw new PlatformNotSupportedException(why);
        if (width < 16 || height < 16 || width % 2 != 0 || height % 2 != 0) throw new ArgumentException($"A picture of {width}x{height} cannot be encoded.");
        if (codec == ScreenCodec.Auto) throw new ArgumentException("Say which codec to encode.", nameof(codec));
        Width = width;
        Height = height;
        Codec = codec;
        Card = codec == ScreenCodec.Vp8 ? null : card ?? HardwareEncoder.Nvenc;
        if (Card == HardwareEncoder.Vaapi && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("VA-API is for Linux.");
        if (Card is HardwareEncoder.Amf or HardwareEncoder.Qsv && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException($"{HardwareEncoders.Maker(Card.Value)}'s encoder is driven this way on Windows only.");
        string name = Card is { } c ? HardwareEncoders.Name(codec, c) : "libvpx";
        var encoder = ffmpeg.avcodec_find_encoder_by_name(name);
        if (encoder == null) throw new PlatformNotSupportedException($"These FFmpeg libraries have no {name} encoder.");
        _context = ffmpeg.avcodec_alloc_context3(encoder);
        try
        {
            _context->width = width;
            _context->height = height;
            _context->time_base = new AVRational { num = 1, den = options.Fps };
            _context->framerate = new AVRational { num = options.Fps, den = 1 };
            _onCard = capture is { OnCard: true } && ReadsFromCard(codec, Card);
            string cardNote = "";
            if (_onCard)
            {
                _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                _context->hw_frames_ctx = ffmpeg.av_buffer_ref(capture!.HardwareFrames);
            }
            else if (Card == HardwareEncoder.Vaapi) cardNote = "; card " + OpenVaapi(width, height);
            else _context->pix_fmt = codec == ScreenCodec.Vp8 ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_NV12;
            Kbps = options.Kbps;
            _context->bit_rate = options.Kbps * 1000L;
            (_bufferSeconds, _maxRateMultiple) = (options.BufferSeconds, options.MaxRateMultiple);
            _context->rc_max_rate = (long)(options.Kbps * 1000.0 * _maxRateMultiple);
            _context->rc_buffer_size = (int)(options.Kbps * 1000.0 * _bufferSeconds);
            _context->gop_size = Math.Max(1, (int)Math.Round(options.Fps * options.KeyFrameSeconds));
            _context->max_b_frames = 0;
            var settings = EncoderOptions(codec, Card);
            foreach (var (option, value) in settings) SetOption(option, value);
            Check(ffmpeg.avcodec_open2(_context, encoder, null), $"starting the encoder {name}");
            if (codec == ScreenCodec.Av1) StartTemporalDelimiters();
            _description = $"{name} {width}x{height} at {options.Fps} fps, {options.Kbps} kbit/s (at most {options.Kbps * _maxRateMultiple:0}, buffer {options.Kbps * _bufferSeconds:0}), "
                           + $"a key frame every {_context->gop_size} pictures, options {string.Join(' ', settings.Select(o => o.Name + "=" + o.Value))}"
                           + (_onCard ? "; pictures read from the graphics card (Direct3D 11)" : "") + cardNote + $"; libraries {FfmpegLibraries.Describe()}";
            if (!_onCard)
            {
                _frame = Picture(codec == ScreenCodec.Vp8 ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_NV12, width, height);
                if (codec == ScreenCodec.Vp8)
                {
                    _nv12 = Picture(AVPixelFormat.AV_PIX_FMT_NV12, width, height);
                    _planar = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_NV12, width, height, AVPixelFormat.AV_PIX_FMT_YUV420P, 0x10 /* SWS_POINT */, null, null, null);
                    if (_planar == null) throw new InvalidOperationException("converting pictures for VP8 could not be prepared.");
                }
            }
            if (Card == HardwareEncoder.Vaapi) _uploaded = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The options of each encoder, by FFmpeg's own names: what the command line gave it (<c>-preset p4</c> and so on). Low latency, no B-frames, every key frame an IDR
    /// picture that repeats the parameter sets, and for H.264 a delimiter in front of each picture. Variable rate: a fixed rate would fill a still screen with padding.
    /// NVIDIA's encoder also hands each packet back as soon as it is made (by default it holds two pictures back).
    /// </summary>
    public static (string Name, string Value)[] EncoderOptions(ScreenCodec codec, HardwareEncoder? card) => (codec, card) switch
    {
        (ScreenCodec.Vp8, _) or (_, null) => new[] { ("deadline", "realtime"), ("cpu-used", "6"), ("lag-in-frames", "0"), ("auto-alt-ref", "0"), ("error-resilient", "1"), ("threads", "4") },
        (ScreenCodec.H264, HardwareEncoder.Nvenc) => FfmpegArgs.NvencH264Options.Append(("delay", "0")).ToArray(),
        (ScreenCodec.H264, HardwareEncoder.Amf) => new[] { ("usage", "ultralowlatency"), ("quality", "speed"), ("rc", "vbr_latency"), ("profile", "high"), ("forced_idr", "1"), ("aud", "1"), ("async_depth", "1") },
        (ScreenCodec.H264, HardwareEncoder.Qsv) => new[] { ("preset", "veryfast"), ("scenario", "displayremoting"), ("async_depth", "1"), ("profile", "high"), ("forced_idr", "1"), ("aud", "1") },
        (ScreenCodec.H264, HardwareEncoder.Vaapi) => new[] { ("profile", "high"), ("aud", "1"), ("async_depth", "1") },
        (ScreenCodec.Av1, HardwareEncoder.Nvenc) => new[] { ("preset", "p4"), ("tune", "ll"), ("rc", "vbr"), ("rc-lookahead", "0"), ("forced-idr", "1"), ("zerolatency", "1"), ("delay", "0") },
        (ScreenCodec.Av1, HardwareEncoder.Amf) => new[] { ("usage", "ultralowlatency"), ("quality", "speed"), ("rc", "vbr_latency"), ("async_depth", "1"), ("forced_idr", "1"), ("header_insertion_mode", "frame") },
        (ScreenCodec.Av1, HardwareEncoder.Qsv) => new[] { ("preset", "veryfast"), ("async_depth", "1"), ("forced_idr", "1") },
        (ScreenCodec.Av1, HardwareEncoder.Vaapi) => new[] { ("profile", "main") },
        _ => throw new ArgumentException($"There is no encoder for {codec}."),
    };

    private static AVFrame* Picture(AVPixelFormat format, int width, int height)
    {
        var frame = ffmpeg.av_frame_alloc();
        frame->format = (int)format;
        frame->width = width;
        frame->height = height;
        int got = ffmpeg.av_frame_get_buffer(frame, 0);
        if (got < 0) { ffmpeg.av_frame_free(&frame); Check(got, "allocating a picture"); }
        return frame;
    }

    // VA-API: the card is opened through its render node (/dev/dri/renderD128 and so on, or the one YAPARR_VAAPI_DEVICE names), the encoder takes pictures that live in
    // the card's memory, and each NV12 picture handed over is uploaded into one first.
    private string OpenVaapi(int width, int height)
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
        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
        _context->hw_frames_ctx = ffmpeg.av_buffer_ref(_uploadFrames);
        return device;
    }

    // AV1 as the command line wrote it to IVF: the IVF writer put av1_metadata with td=insert in front, so every unit starts with a temporal delimiter (which is how the
    // viewer and Av1.IsKeyFrame find where a picture starts). The same filter, on each packet.
    private void StartTemporalDelimiters()
    {
        var bsf = ffmpeg.av_bsf_get_by_name("av1_metadata");
        if (bsf == null) throw new PlatformNotSupportedException("These FFmpeg libraries have no av1_metadata filter.");
        AVBSFContext* filter = null;
        Check(ffmpeg.av_bsf_alloc(bsf, &filter), "preparing the AV1 filter");
        _filter = filter;
        Check(ffmpeg.avcodec_parameters_from_context(_filter->par_in, _context), "preparing the AV1 filter");
        _filter->time_base_in = _context->time_base;
        Check(ffmpeg.av_opt_set(_filter->priv_data, "td", "insert", 0), "preparing the AV1 filter");
        Check(ffmpeg.av_bsf_init(_filter), "starting the AV1 filter");
    }

    // Copies an NV12 picture in memory to a picture on the card, which is what the VA-API encoder takes.
    private AVFrame* UploadFrom(AVFrame* picture)
    {
        ffmpeg.av_frame_unref(_uploaded);
        Check(ffmpeg.av_hwframe_get_buffer(_uploadFrames, _uploaded, 0), "getting a picture on the graphics card");
        Check(ffmpeg.av_hwframe_transfer_data(_uploaded, picture, 0), "copying a picture to the graphics card");
        return _uploaded;
    }

    // As FFmpeg's command line sets an option: the encoder's own first (preset, rc...), then the general ones (profile, threads).
    private void SetOption(string name, string value) => Check(ffmpeg.av_opt_set(_context, name, value, ffmpeg.AV_OPT_SEARCH_CHILDREN), $"setting the encoder option {name}");

    private static void Check(int result, string what)
    {
        if (result >= 0) return;
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi((IntPtr)buffer)}");
    }

    /// <summary>
    /// Encodes one picture. <paramref name="forceKey"/> makes it a key frame (an IDR picture, with the parameter sets, as every key frame has).
    /// Each packet that comes out is handed to <paramref name="output"/> with whether it is a key frame; with no delay that is exactly one.
    /// </summary>
    public void Encode(ReadOnlySpan<byte> picture, bool forceKey, Action<byte[], bool> output)
    {
        if (_onCard) throw new InvalidOperationException("This encoder takes its pictures from the graphics card, not as bytes.");
        if (picture.Length != PictureBytes) throw new ArgumentException($"A picture is {PictureBytes} bytes, not {picture.Length}.", nameof(picture));
        if (_context == null) throw new ObjectDisposedException(nameof(InProcessEncoder));
        var into = Codec == ScreenCodec.Vp8 ? _nv12 : _frame;
        Check(ffmpeg.av_frame_make_writable(into), "preparing a picture");
        fixed (byte* source = picture)
        {
            CopyPlane(source, Width, into->data[0], into->linesize[0], Height);
            CopyPlane(source + (long)Width * Height, Width, into->data[1], into->linesize[1], Height / 2);
        }
        Send(into, forceKey, output);
    }

    /// <summary>
    /// Encodes a picture that <see cref="InProcessCapture"/> made, as it is: the encoder reads it from FFmpeg's memory (or the card's), so nothing is copied here
    /// (VA-API uploads it, VP8 makes it planar). Otherwise as <see cref="Encode(ReadOnlySpan{byte}, bool, Action{byte[], bool})"/>.
    /// </summary>
    public void Encode(CapturedPicture picture, bool forceKey, Action<byte[], bool> output)
    {
        if (_context == null) throw new ObjectDisposedException(nameof(InProcessEncoder));
        var frame = picture.Frame;
        if (frame == null) throw new ObjectDisposedException(nameof(CapturedPicture));
        var format = _onCard ? AVPixelFormat.AV_PIX_FMT_D3D11 : AVPixelFormat.AV_PIX_FMT_NV12;
        if (frame->format != (int)format || frame->width != Width || frame->height != Height)
            throw new ArgumentException($"The encoder takes {(_onCard ? "Direct3D 11" : "NV12")} pictures of {Width}x{Height}, not {frame->width}x{frame->height}.", nameof(picture));
        Send(frame, forceKey, output);
    }

    // An NV12 picture (or the card's) to the encoder: uploaded first for VA-API, made planar first for VP8. The capture's picture itself is not changed (the share's clock
    // may hand the same one over again), except its time stamp and kind, which are set on whatever is sent.
    private void Send(AVFrame* picture, bool forceKey, Action<byte[], bool> output)
    {
        AVFrame* toSend = picture;
        if (Codec == ScreenCodec.Vp8)
        {
            Check(ffmpeg.av_frame_make_writable(_frame), "preparing a picture");
            Check(ffmpeg.sws_scale_frame(_planar, _frame, picture), "converting a picture for VP8");
            toSend = _frame;
        }
        else if (Card == HardwareEncoder.Vaapi) toSend = UploadFrom(picture);
        toSend->pts = _pts++;
        toSend->pict_type = forceKey ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        Check(ffmpeg.avcodec_send_frame(_context, toSend), "encoding a picture");
        ReceivePackets(output);
    }

    /// <summary>The bitrate the encoder was last given, in kbit/s.</summary>
    public int Kbps { get; private set; }

    /// <summary>
    /// Changes the bitrate while encoding, without a restart (NVIDIA's encoder only, see <see cref="LiveBitrate"/>): the average, the ceiling and the buffer move
    /// together, in the proportions the encoder was started with. Whether the encoder follows is what the caller has to measure.
    /// </summary>
    public void SetBitrate(int kbps)
    {
        if (_context == null) throw new ObjectDisposedException(nameof(InProcessEncoder));
        if (!LiveBitrate) throw new NotSupportedException("This encoder cannot be given a new bitrate while it runs.");
        if (kbps < 50) throw new ArgumentOutOfRangeException(nameof(kbps));
        _context->bit_rate = kbps * 1000L;
        _context->rc_max_rate = (long)(kbps * 1000.0 * _maxRateMultiple);
        _context->rc_buffer_size = (int)(kbps * 1000.0 * _bufferSeconds);
        Kbps = kbps;
    }

    private void ReceivePackets(Action<byte[], bool> output)
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
            Check(result, "reading an encoded picture");
            if (_filter == null)
            {
                try { output(new ReadOnlySpan<byte>(_packet->data, _packet->size).ToArray(), (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0); }
                finally { ffmpeg.av_packet_unref(_packet); }
                continue;
            }
            Check(ffmpeg.av_bsf_send_packet(_filter, _packet), "putting a delimiter in front of an AV1 picture");   // takes the packet's data
            while (true)
            {
                int got = ffmpeg.av_bsf_receive_packet(_filter, _packet);
                if (got == ffmpeg.AVERROR(ffmpeg.EAGAIN) || got == ffmpeg.AVERROR_EOF) break;
                Check(got, "putting a delimiter in front of an AV1 picture");
                try { output(new ReadOnlySpan<byte>(_packet->data, _packet->size).ToArray(), (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0); }
                finally { ffmpeg.av_packet_unref(_packet); }
            }
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
        if (_filter != null) { var b = _filter; ffmpeg.av_bsf_free(&b); _filter = null; }
        if (_planar != null) { ffmpeg.sws_freeContext(_planar); _planar = null; }
        if (_nv12 != null) { var n = _nv12; ffmpeg.av_frame_free(&n); _nv12 = null; }
        if (_uploaded != null) { var u = _uploaded; ffmpeg.av_frame_free(&u); _uploaded = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_context != null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        if (_uploadFrames != null) { var fr = _uploadFrames; ffmpeg.av_buffer_unref(&fr); _uploadFrames = null; }
        if (_hardwareDevice != null) { var d = _hardwareDevice; ffmpeg.av_buffer_unref(&d); _hardwareDevice = null; }
    }

    /// <summary>
    /// Whether <paramref name="codec"/> can be encoded on <paramref name="card"/> here: a tiny encoder is started and given a few black pictures, and must hand back a key
    /// frame. The one reliable way to know a card (and driver, and these libraries) can do it (see <see cref="Av1Support"/>). <paramref name="why"/> says why not.
    /// </summary>
    public static bool Probe(ScreenCodec codec, HardwareEncoder card, out string why)
    {
        why = "";
        try
        {
            // Big enough for every maker's minimum picture size (some AV1 encoders refuse the sizes a test usually uses).
            using var encoder = new InProcessEncoder(640, 360, new ScreenOptions { Fps = 10, Kbps = 500, KeyFrameSeconds = 1 }, null, codec, card);
            var picture = new byte[encoder.PictureBytes];
            Array.Fill(picture, (byte)16, 0, 640 * 360);
            Array.Fill(picture, (byte)128, 640 * 360, picture.Length - 640 * 360);
            bool key = false;
            int packets = 0;
            for (int n = 0; n < 3; n++) encoder.Encode(picture, n == 0, (frame, isKey) => { packets++; key |= isKey && ScreenCodecs.IsKeyFrame(codec, frame); });
            if (key) return true;
            why = packets == 0 ? "the encoder gave nothing back" : "the encoder gave no key frame a viewer can start from";
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException or NotSupportedException)
        {
            why = ex.Message;
            return false;
        }
    }
}
