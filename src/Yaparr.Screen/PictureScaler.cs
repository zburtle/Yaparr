using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Yaparr.Screen;

/// <summary>
/// Makes a smaller NV12 picture out of a picture of the share, for the lower stream (<see cref="LowStream"/>): from the capture's own picture (in memory,
/// or on the graphics card, which is copied back first) or from NV12 bytes (a Wayland share's). The result keeps the shape and fits the box, both sides even,
/// never larger than the source. Not thread safe: the share's loop uses it.
/// </summary>
public sealed unsafe class PictureScaler : IDisposable
{
    private SwsContext* _sws;
    private AVFrame* _download;
    private AVFrame* _scaled;
    private AVFrame* _wrapped;
    private int _scaledWidth, _scaledHeight;
    private byte[] _output = Array.Empty<byte>();

    /// <summary>The last picture made: NV12, <see cref="Width"/> x <see cref="Height"/>.</summary>
    public ReadOnlySpan<byte> Output => _output;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public PictureScaler()
    {
        if (!FfmpegLibraries.TryLoad(out var why)) throw new PlatformNotSupportedException(why);
        _download = ffmpeg.av_frame_alloc();
        _scaled = ffmpeg.av_frame_alloc();
        _wrapped = ffmpeg.av_frame_alloc();
    }

    /// <summary>The size a <paramref name="width"/> x <paramref name="height"/> picture is made into for a box of <paramref name="maxWidth"/> x <paramref name="maxHeight"/>.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight) => new PreviewSize(maxWidth, maxHeight).Fit(width, height);

    /// <summary>Scales the capture's picture (copied back from the card first when it is there).</summary>
    public void Scale(CapturedPicture picture, int maxWidth, int maxHeight)
    {
        var frame = picture.Frame;
        if (frame == null) throw new ObjectDisposedException(nameof(CapturedPicture));
        if (frame->hw_frames_ctx != null)
        {
            ffmpeg.av_frame_unref(_download);
            Check(ffmpeg.av_hwframe_transfer_data(_download, frame, 0), "copying a picture from the graphics card");
            try { Scale(_download, maxWidth, maxHeight); }
            finally { ffmpeg.av_frame_unref(_download); }
            return;
        }
        Scale(frame, maxWidth, maxHeight);
    }

    /// <summary>Scales NV12 bytes of <paramref name="width"/> x <paramref name="height"/>.</summary>
    public void Scale(ReadOnlySpan<byte> nv12, int width, int height, int maxWidth, int maxHeight)
    {
        if (nv12.Length < Nv12.Size(width, height)) throw new ArgumentException("The picture is too short.", nameof(nv12));
        fixed (byte* data = nv12)
        {
            _wrapped->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _wrapped->width = width;
            _wrapped->height = height;
            _wrapped->data[0] = data;
            _wrapped->data[1] = data + (long)width * height;
            _wrapped->linesize[0] = width;
            _wrapped->linesize[1] = width;
            try { Scale(_wrapped, maxWidth, maxHeight); }
            finally { _wrapped->data[0] = null; _wrapped->data[1] = null; }
        }
    }

    private void Scale(AVFrame* source, int maxWidth, int maxHeight)
    {
        var (width, height) = Fit(source->width, source->height, maxWidth, maxHeight);
        if (_scaledWidth != width || _scaledHeight != height)
        {
            ffmpeg.av_frame_unref(_scaled);
            _scaled->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _scaled->width = width;
            _scaled->height = height;
            Check(ffmpeg.av_frame_get_buffer(_scaled, 0), "making room for a picture");
            (_scaledWidth, _scaledHeight) = (width, height);
        }
        _sws = ffmpeg.sws_getCachedContext(_sws, source->width, source->height, (AVPixelFormat)source->format, width, height, AVPixelFormat.AV_PIX_FMT_NV12,
            2 /* SWS_BILINEAR */, null, null, null);
        if (_sws == null) throw new InvalidOperationException("The picture cannot be scaled.");
        Check(ffmpeg.av_frame_make_writable(_scaled), "preparing a picture");
        Check(ffmpeg.sws_scale_frame(_sws, _scaled, source), "scaling a picture");
        int size = Nv12.Size(width, height);
        if (_output.Length != size) _output = new byte[size];
        fixed (byte* into = _output)
        {
            Plane(_scaled->data[0], _scaled->linesize[0], into, width, height);
            Plane(_scaled->data[1], _scaled->linesize[1], into + (long)width * height, width, height / 2);
        }
        (Width, Height) = (width, height);
    }

    private static void Plane(byte* from, int stride, byte* to, int rowBytes, int rows)
    {
        if (stride == rowBytes) { Buffer.MemoryCopy(from, to, (long)rowBytes * rows, (long)rowBytes * rows); return; }
        for (int y = 0; y < rows; y++) Buffer.MemoryCopy(from + (long)y * stride, to + (long)y * rowBytes, rowBytes, rowBytes);
    }

    private static void Check(int result, string what)
    {
        if (result >= 0) return;
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi((IntPtr)buffer)}");
    }

    public void Dispose()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_download != null) { var d = _download; ffmpeg.av_frame_free(&d); _download = null; }
        if (_scaled != null) { var s = _scaled; ffmpeg.av_frame_free(&s); _scaled = null; }
        if (_wrapped != null) { var w = _wrapped; ffmpeg.av_frame_free(&w); _wrapped = null; }
    }
}
