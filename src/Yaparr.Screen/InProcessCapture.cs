using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Yaparr.Screen;

/// <summary>
/// Windows Graphics Capture, or on Linux an X11 screen (FFmpeg's <c>x11grab</c> device), inside this program, through FFmpeg's libraries instead of a separate FFmpeg that writes raw pictures to a pipe: the same
/// filter chain (<see cref="FfmpegArgs.CaptureFilter"/>: capture, copy back to memory, scale to fit, NV12), run as a filter graph whose output is pulled
/// here, so a picture goes from the capture to the encoder without a child process, a pipe, or a copy in between. (On X11 the pictures are read from the
/// device, put into a graph that scales and converts them the same way, and pulled from it: x11grab hands over a picture at the frame rate whether or not the
/// screen changed, and there is nothing on a graphics card to keep them on.)
/// <para>
/// The capture hands over pictures when the screen changes (about 50 a second when something moves, none when nothing does), so a thread pulls them
/// and keeps the newest; <see cref="TakeNewest"/> gives the share the picture to encode at each tick of its own clock, which repeats the last one when
/// there is no new one. That makes the pictures reach the encoder at an even pace (the command-line FFmpeg tops the stream up with copies that arrive
/// in bursts, which showed as 60 to 90% of the pictures arriving straight after another).
/// </para>
/// </summary>
public sealed unsafe class InProcessCapture : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);

    private readonly object _gate = new();
    private AVFilterGraph* _graph;
    private AVFilterContext* _sink;
    private AVFrame* _newest;          // the newest picture, one reference held here
    private long _newestSeq, _taken;   // which picture that is, and which the share last took
    private long _newestAt;            // Stopwatch ticks when it arrived
    private readonly Thread _thread;
    private volatile bool _stop;
    private string? _ended;            // why no more pictures are coming: null while they are
    private bool _timerRaised;
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _pictures, _emptyPolls;
    private AVBufferRef* _hardwareFrames;   // with pictures on the card: what the encoder needs to read them
    private const int BuffersrcKeepRef = 8;   // AV_BUFFERSRC_FLAG_KEEP_REF
    private AVFormatContext* _x11;          // on X11: the screen grabber that the pictures are read from
    private AVFilterContext* _x11Source;    // and where they go into the graph
    private AVPixelFormat _x11Format;
    private int _x11Width, _x11Height;

    /// <summary>The size of the pictures (known once the first has come; see <see cref="WaitForFirstAsync"/>).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The filter chain, as FFmpeg's own text, for the log.</summary>
    public string Graph { get; }

    /// <summary>Pictures the capture has handed over so far.</summary>
    public long Pictures => Interlocked.Read(ref _pictures);

    /// <summary>How many times the capture was asked and had nothing yet (each costs a short sleep).</summary>
    public long EmptyPolls => Interlocked.Read(ref _emptyPolls);

    /// <summary>The size of one picture handed to the encoder: NV12, so 12 bits a pixel.</summary>
    public int PictureBytes => Width * Height * 3 / 2;

    /// <summary>The pictures stay on the graphics card (Direct3D 11 textures) and the encoder reads them there.</summary>
    public bool OnCard { get; }

    /// <summary>With the pictures on the card: the frames context to give the encoder (a reference is held here; the encoder takes its own).</summary>
    internal AVBufferRef* HardwareFrames => _hardwareFrames;

    public InProcessCapture(ScreenOptions options, bool onCard = false)
    {
        OnCard = onCard;
        bool x11 = OperatingSystem.IsLinux() && options.X11Display is { Length: > 0 };
        if (!x11 && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Capture in this program is for Windows Graphics Capture and, on Linux, for an X11 screen.");
        if (x11 && onCard) throw new InvalidOperationException("An X11 screen is read from memory: there is no graphics card to keep its pictures on.");
        if (!FfmpegLibraries.TryLoad(out var why)) throw new PlatformNotSupportedException(why);
        Graph = x11 ? X11GraphText(options)
                    : (onCard ? FfmpegArgs.CaptureFilterOnCard(options) : FfmpegArgs.CaptureFilter(options))
                      ?? throw new InvalidOperationException("The size of the picture is not known, so it cannot be scaled on the graphics card.");
        _graph = ffmpeg.avfilter_graph_alloc();
        try
        {
            AVFilterContext* sink = null;
            Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _graph), "creating the end of the capture graph");
            _sink = sink;
            if (x11) BuildX11(options, sink);
            else
            {
                var inputs = ffmpeg.avfilter_inout_alloc();
                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = sink;
                inputs->pad_idx = 0;
                inputs->next = null;
                AVFilterInOut* outputs = null;
                // Where FFmpeg lacks the capture filter or the options of the October 2026 fixes it says so here; the message is kept as it is, because the share
                // looks for those words to tell the person that the libraries are too old.
                int parsed = ffmpeg.avfilter_graph_parse_ptr(_graph, Graph, &inputs, &outputs, null);
                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
                Check(parsed, "building the capture graph");
                Check(ffmpeg.avfilter_graph_config(_graph, null), "starting the capture");
                if (onCard)
                {
                    var frames = ffmpeg.av_buffersink_get_hw_frames_ctx(_sink);
                    if (frames == null) throw new InvalidOperationException("The capture does not keep its pictures on the graphics card.");
                    _hardwareFrames = ffmpeg.av_buffer_ref(frames);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        // Without this the wait for the next picture is a sleep of up to 15 ms, which is nearly a whole picture at 60 a second (Windows).
        if (OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0) _timerRaised = true;
        _thread = new Thread(x11 ? PullX11 : Pull) { IsBackground = true, Name = "Screen capture" };
        _thread.Start();
    }

    // ---- X11

    private static string X11Input(ScreenOptions o)
        => o.WindowHandle != null ? o.X11Display! : $"{o.X11Display}+{(o.Display?.X ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)},{(o.Display?.Y ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    // The filter chain after the grabber, the same text the command line uses (FfmpegArgs.CaptureX11): scale to fit the box (never up, the shape kept, even numbers), NV12.
    private static string X11Scale(ScreenOptions o) => $"scale='min({o.MaxWidth},iw)':'min({o.MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=nv12";

    private static string X11GraphText(ScreenOptions o)
    {
        if (o.WindowHandle == null && o.Display == null) throw new ArgumentException("Choose a monitor or a window to share.", nameof(o));
        string window = o.WindowHandle is { } w ? $" -window_id 0x{w.ToString("X", System.Globalization.CultureInfo.InvariantCulture)}" : o.Display is { Width: > 0, Height: > 0 } d ? $" -video_size {d.Width}x{d.Height}" : "";
        return $"x11grab -framerate {o.Fps} -draw_mouse {(o.ShowCursor ? 1 : 0)}{window} -i {X11Input(o)}  ->  {X11Scale(o)}";
    }

    // Opens the screen grabber with the options of the command line, and makes the graph its pictures go through.
    private void BuildX11(ScreenOptions o, AVFilterContext* sink)
    {
        ffmpeg.avdevice_register_all();
        var format = ffmpeg.av_find_input_format("x11grab");
        if (format == null) throw new PlatformNotSupportedException("These FFmpeg libraries cannot copy an X11 screen (no x11grab).");
        AVDictionary* options = null;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        ffmpeg.av_dict_set(&options, "framerate", o.Fps.ToString(c), 0);
        ffmpeg.av_dict_set(&options, "draw_mouse", o.ShowCursor ? "1" : "0", 0);
        if (o.WindowHandle is { } window) ffmpeg.av_dict_set(&options, "window_id", "0x" + window.ToString("X", c), 0);   // the window keeps the size it had when sharing began
        else if (o.Display is { Width: > 0, Height: > 0 } shown) ffmpeg.av_dict_set(&options, "video_size", $"{shown.Width}x{shown.Height}", 0);
        AVFormatContext* context = null;
        int opened = ffmpeg.avformat_open_input(&context, X11Input(o), format, &options);
        ffmpeg.av_dict_free(&options);
        Check(opened, "opening the X11 screen");
        _x11 = context;
        if (context->nb_streams < 1) throw new InvalidOperationException("The X11 screen gave no picture.");
        var parameters = context->streams[0]->codecpar;
        (_x11Width, _x11Height, _x11Format) = (parameters->width, parameters->height, (AVPixelFormat)parameters->format);
        var timeBase = context->streams[0]->time_base;
        string args = $"video_size={_x11Width}x{_x11Height}:pix_fmt={(int)_x11Format}:time_base={timeBase.num}/{timeBase.den}:pixel_aspect=1/1";
        AVFilterContext* source = null;
        Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("buffer"), "in", args, null, _graph), "creating the start of the capture graph");
        _x11Source = source;
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
        int parsed = ffmpeg.avfilter_graph_parse_ptr(_graph, X11Scale(o), &inputs, &outputs, null);
        ffmpeg.avfilter_inout_free(&inputs);
        ffmpeg.avfilter_inout_free(&outputs);
        Check(parsed, "building the capture graph");
        Check(ffmpeg.avfilter_graph_config(_graph, null), "starting the capture");
    }

    // The thread of an X11 share: reads a picture from the grabber (which waits for the next frame time), puts it through the graph and keeps the newest.
    private void PullX11()
    {
        var packet = ffmpeg.av_packet_alloc();
        var raw = ffmpeg.av_frame_alloc();
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            int rowBytes = ffmpeg.av_image_get_linesize(_x11Format, _x11Width, 0);
            while (!_stop)
            {
                int result = ffmpeg.av_read_frame(_x11, packet);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Interlocked.Increment(ref _emptyPolls); Thread.Sleep(1); continue; }
                if (result < 0) { Finish("The capture ended (the window was closed or changed size, or the screen is no longer there)."); return; }
                if (packet->stream_index != 0 || packet->size < rowBytes * _x11Height) { ffmpeg.av_packet_unref(packet); continue; }
                // The grabber's bytes are one picture of plain pixels: wrapped in a frame that owns nothing, and copied by the graph's source as it takes it in.
                raw->format = (int)_x11Format;
                raw->width = _x11Width;
                raw->height = _x11Height;
                raw->data[0] = packet->data;
                raw->linesize[0] = rowBytes;
                raw->pts = packet->pts;
                int added = ffmpeg.av_buffersrc_add_frame_flags(_x11Source, raw, BuffersrcKeepRef);
                raw->data[0] = null;
                ffmpeg.av_frame_unref(raw);
                ffmpeg.av_packet_unref(packet);
                if (added < 0) { Finish("The capture stopped: " + Error(added)); return; }
                while (!_stop)
                {
                    result = ffmpeg.av_buffersink_get_frame(_sink, frame);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN)) break;
                    if (result < 0) { Finish("The capture stopped: " + Error(result)); return; }
                    if (frame->format != (int)AVPixelFormat.AV_PIX_FMT_NV12) { ffmpeg.av_frame_unref(frame); Finish("The capture gave pictures that are not NV12."); return; }
                    long at = Stopwatch.GetTimestamp();
                    lock (_gate)
                    {
                        if (_newest != null) { var old = _newest; ffmpeg.av_frame_free(&old); }
                        _newest = ffmpeg.av_frame_clone(frame);
                        _newestSeq++;
                        _newestAt = at;
                        if (Width == 0) { Width = frame->width; Height = frame->height; }
                    }
                    ffmpeg.av_frame_unref(frame);
                    Interlocked.Increment(ref _pictures);
                    _first.TrySetResult();
                }
            }
        }
        finally
        {
            var p = packet; ffmpeg.av_packet_free(&p);
            var r = raw; ffmpeg.av_frame_free(&r);
            var f = frame; ffmpeg.av_frame_free(&f);
        }
    }

    private static string Error(int result)
    {
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? "error " + result;
    }

    private static void Check(int result, string what)
    {
        if (result >= 0) return;
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi((IntPtr)buffer)}");
    }

    /// <summary>Waits for the first picture, which says what size the pictures are.</summary>
    public Task WaitForFirstAsync(TimeSpan limit, CancellationToken ct) => _first.Task.WaitAsync(limit, ct);

    // The thread that asks the graph for pictures. The capture answers "try again" while there is nothing new, so this waits a moment and asks again.
    private void Pull()
    {
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            while (!_stop)
            {
                int result = ffmpeg.av_buffersink_get_frame(_sink, frame);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Interlocked.Increment(ref _emptyPolls); Thread.Sleep(1); continue; }
                if (result == ffmpeg.AVERROR_EOF) { Finish("The capture ended (the window was closed, or the screen is no longer there)."); return; }
                if (result < 0)
                {
                    byte* buffer = stackalloc byte[256];
                    ffmpeg.av_strerror(result, buffer, 256);
                    Finish("The capture stopped: " + Marshal.PtrToStringAnsi((IntPtr)buffer));
                    return;
                }
                var expected = OnCard ? AVPixelFormat.AV_PIX_FMT_D3D11 : AVPixelFormat.AV_PIX_FMT_NV12;
                if (frame->format != (int)expected) { ffmpeg.av_frame_unref(frame); Finish($"The capture gave pictures that are not {(OnCard ? "on the graphics card" : "NV12")}."); return; }
                long at = Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    if (_newest != null) { var old = _newest; ffmpeg.av_frame_free(&old); }
                    _newest = ffmpeg.av_frame_clone(frame);
                    _newestSeq++;
                    _newestAt = at;
                    if (Width == 0) { Width = frame->width; Height = frame->height; }
                }
                ffmpeg.av_frame_unref(frame);
                Interlocked.Increment(ref _pictures);
                _first.TrySetResult();
            }
        }
        finally { var f = frame; ffmpeg.av_frame_free(&f); }
    }

    private void Finish(string why)
    {
        lock (_gate) _ended = why;
        _first.TrySetException(new InvalidOperationException(why));
    }

    /// <summary>Why no more pictures are coming, or null while they are.</summary>
    public string? EndedBecause { get { lock (_gate) return _ended; } }

    /// <summary>
    /// A reference to the newest picture, or null when the capture has none yet. <paramref name="isNew"/>: it has not been taken before (otherwise it is the
    /// last one again), and <paramref name="arrivedAt"/> is when it came (Stopwatch ticks). Dispose it after use; the picture itself is not copied.
    /// </summary>
    public CapturedPicture? TakeNewest(out bool isNew, out long arrivedAt)
    {
        lock (_gate)
        {
            isNew = _newest != null && _newestSeq != _taken;
            _taken = _newestSeq;
            arrivedAt = _newestAt;
            return _newest == null ? null : new CapturedPicture(ffmpeg.av_frame_clone(_newest));
        }
    }

    public void Dispose()
    {
        _stop = true;
        // The thread is inside a call that waits for the next picture (with the capture's dup_frames that is never long). The graph is only freed
        // once the thread has left it: freeing it under a thread that is still using it would crash the program, and a graph left behind costs memory only.
        bool threadGone = _thread == null || !_thread.IsAlive || (!ReferenceEquals(Thread.CurrentThread, _thread) && _thread.Join(5000));
        lock (_gate)
        {
            if (_newest != null) { var f = _newest; ffmpeg.av_frame_free(&f); _newest = null; }
        }
        if (_graph != null && threadGone) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; }
        if (_x11 != null && threadGone) { var x = _x11; ffmpeg.avformat_close_input(&x); _x11 = null; }
        if (_hardwareFrames != null && threadGone) { var h = _hardwareFrames; ffmpeg.av_buffer_unref(&h); _hardwareFrames = null; }
        if (_timerRaised) { _timerRaised = false; timeEndPeriod(1); }
        _first.TrySetCanceled();
    }
}

/// <summary>One picture from <see cref="InProcessCapture"/>, as NV12 in FFmpeg's own memory: a reference, not a copy. Dispose it when it has been encoded.</summary>
public sealed unsafe class CapturedPicture : IDisposable
{
    internal AVFrame* Frame { get; private set; }

    internal CapturedPicture(AVFrame* frame) => Frame = frame;

    public int Width => Frame->width;
    public int Height => Frame->height;

    public void Dispose()
    {
        if (Frame == null) return;
        var f = Frame;
        ffmpeg.av_frame_free(&f);
        Frame = null;
    }
}
