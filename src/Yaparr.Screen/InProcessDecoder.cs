using System.Diagnostics;
using FFmpeg.AutoGen;

namespace Yaparr.Screen;

/// <summary>
/// Decodes a shared screen with FFmpeg's libraries inside this program (stage 6 of #225), where <see cref="ScreenViewer"/> used to start a separate FFmpeg and pass
/// every frame in through a pipe and every picture out through another. Frames go in as the stream tree delivers them (H.264 as access units, VP8 and AV1 as the
/// frames of an IVF file, without its headers) and each picture comes out as NV12 at the size asked for: the size of the stream, or a smaller one for a preview
/// (scaled here, by the library that scales). H.264 can be decoded on the graphics card (Windows: Direct3D 12 or 11; Linux: NVIDIA's NVDEC or VA-API, see
/// <see cref="DecodeDevice"/>); the picture is
/// then copied back to memory and goes on as a software one would. Not thread safe: one thread decodes.
/// </summary>
public sealed unsafe class InProcessDecoder : IDisposable
{
    private AVCodecContext* _context;
    private AVBufferRef* _hardware;
    private AVFrame* _downloaded;
    // A preview decoded on the card through Direct3D 12 or VA-API is scaled there too (scale_d3d12, scale_vaapi), so that only the small picture is copied back: the graph that does it,
    // made for the first picture and again when the size changes. Measured with the separate FFmpeg this replaces: a fifth to an eighth of the processor time of
    // copying the whole picture back and scaling it in memory. Off for good if the libraries cannot build it (the picture is then copied back whole and scaled here).
    private AVFilterGraph* _graph;
    private AVFilterContext* _graphIn, _graphOut;
    private int _graphWidth, _graphHeight, _graphOutWidth, _graphOutHeight;
    private bool _cardScaleFailed;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _scaled;
    private SwsContext* _scaler;
    private int _scaledWidth, _scaledHeight;
    private bool _disposed;

    /// <summary>The codec this decoder reads.</summary>
    public ScreenCodec Codec { get; }

    /// <summary>The decoder that is used, like "h264" or "libdav1d", and the libraries, for a log.</summary>
    public string Description { get; private set; } = "";

    /// <summary>Whether FFmpeg's libraries are there and can decode <paramref name="codec"/> (nothing is started).</summary>
    public static bool IsAvailable(ScreenCodec codec)
    {
        if (!FfmpegLibraries.TryLoad(out _)) return false;
        return Find(codec) != null;
    }

    /// <summary>The name of the decoder used for <paramref name="codec"/> (like "libdav1d"), or null when there is none.</summary>
    public static string? DecoderName(ScreenCodec codec)
    {
        if (!FfmpegLibraries.TryLoad(out _)) return null;
        var found = Find(codec);
        return found == null ? null : Marshal(found->name);
    }

    private static AVCodec* Find(ScreenCodec codec)
    {
        switch (codec)
        {
            case ScreenCodec.H264: return ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            case ScreenCodec.Vp8: return ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_VP8);
            case ScreenCodec.Av1:
                // dav1d is far faster than the reference decoder, and the library may or may not have it.
                var dav1d = ffmpeg.avcodec_find_decoder_by_name("libdav1d");
                return dav1d != null ? dav1d : ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AV1);
            default: return null;
        }
    }

    /// <summary>Where the decoding is done.</summary>
    public DecodeDevice Device { get; }

    /// <summary>
    /// A decoder for <paramref name="codec"/>, on <paramref name="device"/>. The graphics card decodes H.264 only (the others are decoded by the processor, whatever is asked);
    /// a card that cannot be opened throws <see cref="PlatformNotSupportedException"/>, and the caller tries the next choice down.
    /// </summary>
    public InProcessDecoder(ScreenCodec codec, DecodeDevice device = DecodeDevice.Processor)
    {
        if (!FfmpegLibraries.TryLoad(out var why)) throw new PlatformNotSupportedException(why);
        Codec = codec;
        Device = codec == ScreenCodec.H264 && Fits(device) ? device : DecodeDevice.Processor;
        var decoder = Find(codec);
        if (decoder == null) throw new PlatformNotSupportedException($"These FFmpeg libraries have no {codec} decoder.");
        _context = ffmpeg.avcodec_alloc_context3(decoder);
        try
        {
            // The picture is wanted as soon as it is decoded: no frames in flight (a frame thread adds a picture of delay per thread), only the slices of a picture
            // spread over the processors, and the decoder told that nothing is held back for reordering.
            _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _context->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            _context->thread_type = ffmpeg.FF_THREAD_SLICE;
            _context->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            if (Device != DecodeDevice.Processor)
            {
                // With a device given to the decoder, FFmpeg's own choice of format picks the card's (the hardware formats come first) when the card can decode the stream.
                var type = Device switch
                {
                    DecodeDevice.D3d12 => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA,
                    DecodeDevice.Cuda => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
                    DecodeDevice.Vaapi => AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
                    _ => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                };
                AVBufferRef* hardware = null;
                // VA-API is opened on a render node (the one the encoder uses too, see HardwareEncoders.VaapiDevice); the others on the first card.
                int opened = ffmpeg.av_hwdevice_ctx_create(&hardware, type, Device == DecodeDevice.Vaapi ? HardwareEncoders.VaapiDevice() : null, null, 0);
                if (opened < 0) throw new PlatformNotSupportedException($"the graphics card could not be opened for {Name(Device)}: {Message(opened)}");
                _hardware = hardware;
                _context->hw_device_ctx = ffmpeg.av_buffer_ref(_hardware);
                _context->thread_count = 1;   // the card decodes; threads only add work
                _downloaded = ffmpeg.av_frame_alloc();
            }
            Check(ffmpeg.avcodec_open2(_context, decoder, null), "starting the decoder");
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _scaled = ffmpeg.av_frame_alloc();
            Description = Device == DecodeDevice.Processor
                ? $"{Marshal(decoder->name)} (slice threads {_context->thread_count}); libraries {FfmpegLibraries.Describe()}"
                : $"{Marshal(decoder->name)} on the card through {Name(Device)}; libraries {FfmpegLibraries.Describe()}";
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>How a device is named in a log.</summary>
    public static string Name(DecodeDevice device) => device switch
    {
        DecodeDevice.D3d12 => "Direct3D 12", DecodeDevice.D3d11 => "Direct3D 11", DecodeDevice.Cuda => "NVIDIA's decoder (NVDEC)", DecodeDevice.Vaapi => "VA-API", _ => "the processor",
    };

    /// <summary>Whether <paramref name="device"/> is one of this system's: Direct3D on Windows, NVDEC and VA-API on Linux, the processor everywhere.</summary>
    public static bool Fits(DecodeDevice device) => device switch
    {
        DecodeDevice.D3d11 or DecodeDevice.D3d12 => OperatingSystem.IsWindows(),
        DecodeDevice.Cuda or DecodeDevice.Vaapi => OperatingSystem.IsLinux(),
        _ => true,
    };

    /// <summary>The next choice down when <paramref name="device"/> does not work: Direct3D 12, then 11; NVDEC, then VA-API; then the processor.</summary>
    public static DecodeDevice NextDown(DecodeDevice device) => device switch
    {
        DecodeDevice.D3d12 => DecodeDevice.D3d11,
        DecodeDevice.Cuda => DecodeDevice.Vaapi,
        _ => DecodeDevice.Processor,
    };

    /// <summary>The first card to try on this system: Direct3D 12 or 11 on Windows (as asked), NVIDIA's decoder on Linux (VA-API after it), or the processor elsewhere.</summary>
    public static DecodeDevice FirstCard(bool preferD3d12) =>
        OperatingSystem.IsWindows() ? (preferD3d12 ? DecodeDevice.D3d12 : DecodeDevice.D3d11) : OperatingSystem.IsLinux() ? DecodeDevice.Cuda : DecodeDevice.Processor;

    /// <summary>Whether a picture has come out of the card (rather than from the processor, which the decoder falls back to by itself for a stream the card cannot take).</summary>
    public bool LastOnCard { get; private set; }

    private static string Marshal(byte* text) => System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)text) ?? "";

    /// <summary>
    /// Decodes one frame. Every picture that comes out (one, or none while the decoder has not got a whole picture) is handed to <paramref name="output"/> as NV12 of
    /// the size given with its width and height; the memory is only good until the call returns. <paramref name="maxWidth"/> and <paramref name="maxHeight"/> are 0 for the
    /// size of the stream, or the box a smaller picture has to fit (the aspect ratio is kept, both sides come out even).
    /// </summary>
    /// <returns>Whether the frame decoded (false when the decoder refused it: damaged, or not a key frame it can start from).</returns>
    public bool Decode(ReadOnlySpan<byte> data, int maxWidth, int maxHeight, PictureOut output)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InProcessDecoder));
        if (data.Length == 0) return false;
        Check(ffmpeg.av_new_packet(_packet, data.Length), "making room for a frame");
        try
        {
            data.CopyTo(new Span<byte>(_packet->data, data.Length));
            int sent = ffmpeg.avcodec_send_packet(_context, _packet);
            if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN)) { LastError = Message(sent); return false; }
        }
        finally { ffmpeg.av_packet_unref(_packet); }
        bool any = false;
        while (true)
        {
            int result = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) break;
            if (result < 0) { LastError = Message(result); return any; }
            try { Convert(maxWidth, maxHeight, output); any = true; }
            finally { ffmpeg.av_frame_unref(_frame); }
        }
        return true;
    }

    /// <summary>What FFmpeg last said when it refused a frame.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Receives a decoded picture: NV12, its width and height.</summary>
    public delegate void PictureOut(ReadOnlySpan<byte> nv12, int width, int height);

    private void Convert(int maxWidth, int maxHeight, PictureOut output)
    {
        var frame = _frame;
        LastOnCard = frame->hw_frames_ctx != null;
        if (LastOnCard && Device is DecodeDevice.D3d12 or DecodeDevice.Vaapi && maxWidth > 0 && maxHeight > 0 && !_cardScaleFailed && ScaleOnCard(maxWidth, maxHeight, output)) return;
        if (LastOnCard)
        {
            // Out of the card's memory, as NV12, and on as a software picture. A preview is then scaled by the processor: a small cost next to the decoding the card saves.
            ffmpeg.av_frame_unref(_downloaded);
            Check(ffmpeg.av_hwframe_transfer_data(_downloaded, frame, 0), "copying a picture from the graphics card");
            frame = _downloaded;
        }
        try { Convert(frame, maxWidth, maxHeight, output); }
        finally { if (LastOnCard) ffmpeg.av_frame_unref(_downloaded); }
    }

    /// <summary>Whether the last preview picture was scaled on the card (only the small picture copied back).</summary>
    public bool ScaledOnCard { get; private set; }

    // The picture through the card's scaling (scale_d3d12, scale_vaapi) and hwdownload: false (and never tried again) when the graph cannot be made or fails.
    private bool ScaleOnCard(int maxWidth, int maxHeight, PictureOut output)
    {
        int width = _frame->width, height = _frame->height;
        var (outWidth, outHeight) = new PreviewSize(maxWidth, maxHeight).Fit(width, height);
        try
        {
            if (_graph == null || _graphWidth != width || _graphHeight != height || _graphOutWidth != outWidth || _graphOutHeight != outHeight) BuildGraph(width, height, outWidth, outHeight);
            Check(ffmpeg.av_buffersrc_add_frame_flags(_graphIn, _frame, BuffersrcKeepRef), "scaling a picture on the graphics card");
            var small = _downloaded;
            ffmpeg.av_frame_unref(small);
            int got = ffmpeg.av_buffersink_get_frame(_graphOut, small);
            if (got == ffmpeg.AVERROR(ffmpeg.EAGAIN)) return true;   // nothing yet (does not happen with this graph; nothing to show either way)
            Check(got, "reading a picture scaled on the graphics card");
            try
            {
                if (small->format != (int)AVPixelFormat.AV_PIX_FMT_NV12) throw new InvalidOperationException("the card's scaling gave a picture that is not NV12");
                Convert(small, 0, 0, output);
            }
            finally { ffmpeg.av_frame_unref(small); }
            ScaledOnCard = true;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _cardScaleFailed = true;
            ScaledOnCard = false;
            FreeGraph();
            LastError = "scaling on the card did not work, the picture is copied back whole: " + ex.Message;
            return false;
        }
    }

    private const int BuffersrcKeepRef = 8;   // AV_BUFFERSRC_FLAG_KEEP_REF: the decoder's picture stays the decoder's

    private void BuildGraph(int width, int height, int outWidth, int outHeight)
    {
        FreeGraph();
        string scaler = Device == DecodeDevice.Vaapi ? "scale_vaapi" : "scale_d3d12";
        var scale = ffmpeg.avfilter_get_by_name(scaler);
        if (scale == null) throw new InvalidOperationException("these FFmpeg libraries have no " + scaler);
        _graph = ffmpeg.avfilter_graph_alloc();
        var source = ffmpeg.avfilter_graph_alloc_filter(_graph, ffmpeg.avfilter_get_by_name("buffer"), "in");
        if (source == null) throw new InvalidOperationException("the start of the scaling graph could not be made");
        // The source takes the decoder's pictures as they are, in the card's memory: their pool (hw_frames_ctx) has to be given before the filter starts.
        var parameters = ffmpeg.av_buffersrc_parameters_alloc();
        parameters->format = _frame->format;
        parameters->width = width;
        parameters->height = height;
        parameters->time_base = new AVRational { num = 1, den = 30 };
        parameters->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
        parameters->hw_frames_ctx = _frame->hw_frames_ctx;
        int set = ffmpeg.av_buffersrc_parameters_set(source, parameters);
        ffmpeg.av_free(parameters);
        Check(set, "giving the scaling graph the decoder's pictures");
        Check(ffmpeg.avfilter_init_str(source, null), "starting the scaling graph");
        AVFilterContext* sink = null;
        Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _graph), "making the end of the scaling graph");
        var inputs = ffmpeg.avfilter_inout_alloc();
        var outputs = ffmpeg.avfilter_inout_alloc();
        outputs->name = ffmpeg.av_strdup("in");
        outputs->filter_ctx = source;
        outputs->pad_idx = 0;
        outputs->next = null;
        inputs->name = ffmpeg.av_strdup("out");
        inputs->filter_ctx = sink;
        inputs->pad_idx = 0;
        inputs->next = null;
        int parsed = ffmpeg.avfilter_graph_parse_ptr(_graph, $"{scaler}=w={outWidth}:h={outHeight}:format=nv12,hwdownload,format=nv12", &inputs, &outputs, null);
        ffmpeg.avfilter_inout_free(&inputs);
        ffmpeg.avfilter_inout_free(&outputs);
        Check(parsed, "building the scaling graph");
        Check(ffmpeg.avfilter_graph_config(_graph, null), "starting the scaling graph");
        _graphIn = source;
        _graphOut = sink;
        (_graphWidth, _graphHeight, _graphOutWidth, _graphOutHeight) = (width, height, outWidth, outHeight);
    }

    private void FreeGraph()
    {
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; }
        _graphIn = _graphOut = null;
    }

    private void Convert(AVFrame* frame, int maxWidth, int maxHeight, PictureOut output)
    {
        int width = frame->width, height = frame->height;
        var (outWidth, outHeight) = maxWidth > 0 && maxHeight > 0 ? new PreviewSize(maxWidth, maxHeight).Fit(width, height) : (width & ~1, height & ~1);
        if (outWidth < 2 || outHeight < 2) return;
        var source = (AVPixelFormat)frame->format;
        // What is already NV12 at the size wanted goes out as it is; anything else (the usual 4:2:0 planes of a software decoder) is converted, and scaled, by swscale.
        if (source == AVPixelFormat.AV_PIX_FMT_NV12 && outWidth == width && outHeight == height) { Hand(frame, outWidth, outHeight, output); return; }
        if (_scaledWidth != outWidth || _scaledHeight != outHeight)
        {
            ffmpeg.av_frame_unref(_scaled);
            _scaled->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _scaled->width = outWidth;
            _scaled->height = outHeight;
            Check(ffmpeg.av_frame_get_buffer(_scaled, 0), "making room for a picture");
            _scaledWidth = outWidth; _scaledHeight = outHeight;
        }
        _scaler = ffmpeg.sws_getCachedContext(_scaler, width, height, source, outWidth, outHeight, AVPixelFormat.AV_PIX_FMT_NV12,
            outWidth == width && outHeight == height ? SwsPoint : SwsFastBilinear, null, null, null);
        if (_scaler == null) throw new InvalidOperationException($"FFmpeg cannot convert {source} to NV12.");
        Check(ffmpeg.av_frame_make_writable(_scaled), "preparing a picture");
        Check(ffmpeg.sws_scale_frame(_scaler, _scaled, frame), "converting a picture");
        Hand(_scaled, outWidth, outHeight, output);
    }

    private const int SwsFastBilinear = 1, SwsPoint = 0x10;   // swscale's flags (the bindings do not name them)

    private byte[] _packed = Array.Empty<byte>();

    // The rows of a decoded picture can be padded; the picture is handed on packed, as the viewer's colour conversion reads it.
    private void Hand(AVFrame* frame, int width, int height, PictureOut output)
    {
        int size = Nv12.Size(width, height);
        if (_packed.Length != size) _packed = new byte[size];
        fixed (byte* packed = _packed)
        {
            Plane(frame->data[0], frame->linesize[0], packed, width, height);
            Plane(frame->data[1], frame->linesize[1], packed + (long)width * height, width, height / 2);
        }
        output(_packed, width, height);
    }

    private static void Plane(byte* from, int stride, byte* to, int rowBytes, int rows)
    {
        if (stride == rowBytes) { Buffer.MemoryCopy(from, to, (long)rowBytes * rows, (long)rowBytes * rows); return; }
        for (int y = 0; y < rows; y++) Buffer.MemoryCopy(from + (long)y * stride, to + (long)y * rowBytes, rowBytes, rowBytes);
    }

    private static string Message(int error)
    {
        var buffer = stackalloc byte[256];
        ffmpeg.av_strerror(error, buffer, 256);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"error {error}";
    }

    private static void Check(int result, string doing)
    {
        if (result < 0) throw new InvalidOperationException($"FFmpeg failed {doing}: {Message(result)}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FreeGraph();
        if (_scaler != null) { ffmpeg.sws_freeContext(_scaler); _scaler = null; }
        if (_scaled != null) { var s = _scaled; ffmpeg.av_frame_free(&s); _scaled = null; }
        if (_downloaded != null) { var d = _downloaded; ffmpeg.av_frame_free(&d); _downloaded = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_context != null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        if (_hardware != null) { var h = _hardware; ffmpeg.av_buffer_unref(&h); _hardware = null; }
    }
}

/// <summary>Where <see cref="InProcessDecoder"/> decodes: the processor, or the graphics card (H.264): through Direct3D 12 or 11 on Windows, NVIDIA's NVDEC (through CUDA) or VA-API on Linux.</summary>
public enum DecodeDevice { Processor, D3d11, D3d12, Cuda, Vaapi }
