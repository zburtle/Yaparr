using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Yaparr.Screen;

/// <summary>
/// Windows Graphics Capture inside this program, through FFmpeg's libraries instead of a separate FFmpeg that writes raw pictures to a pipe: the same
/// filter chain (<see cref="FfmpegArgs.CaptureFilter"/>: capture, copy back to memory, scale to fit, NV12), run as a filter graph whose output is pulled
/// here, so a picture goes from the capture to the encoder without a child process, a pipe, or a copy in between.
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

    public InProcessCapture(ScreenOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Capture in this program is only for Windows Graphics Capture so far.");
        if (!FfmpegLibraries.TryLoad(out var why)) throw new PlatformNotSupportedException(why);
        Graph = FfmpegArgs.CaptureFilter(options, raw: true);
        _graph = ffmpeg.avfilter_graph_alloc();
        try
        {
            AVFilterContext* sink = null;
            Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _graph), "creating the end of the capture graph");
            _sink = sink;
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
        }
        catch
        {
            Dispose();
            throw;
        }
        // Without this the wait for the next picture is a sleep of up to 15 ms, which is nearly a whole picture at 60 a second.
        if (timeBeginPeriod(1) == 0) _timerRaised = true;
        _thread = new Thread(Pull) { IsBackground = true, Name = "Screen capture" };
        _thread.Start();
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
