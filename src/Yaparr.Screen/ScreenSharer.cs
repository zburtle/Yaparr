using System.Diagnostics;
using Yaparr.Chat;

namespace Yaparr.Screen;

/// <summary>
/// Shares the screen or one window with a room: FFmpeg captures and encodes, and every encoded frame goes out through the
/// stream tree. Windows only, because capture uses the Windows screen sources.
/// </summary>
public sealed class ScreenSharer : IDisposable
{
    private readonly MediaSource _source;
    private readonly ScreenOptions _options;
    private readonly string _ffmpeg;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Queue<string> _errors = new();
    private Process? _process;
    private Task? _run;
    private long _frames, _bytes, _keys;
    private volatile ScreenCodec _codec;
    private volatile string? _fallback;

    public ScreenSharer(MediaSource source, ScreenOptions options, string ffmpegPath)
    {
        _source = source;
        _options = options;
        _ffmpeg = ffmpegPath;
    }

    /// <summary>Announces a screen share in the room and starts sending it.</summary>
    public static ScreenSharer Start(ChatNode node, ChatRoom room, string name, ScreenOptions options, string? ffmpegPath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sharing a screen works on Windows only for now.");
        ffmpegPath ??= FfmpegLocator.Find() ?? throw new FileNotFoundException("FFmpeg was not found. Put ffmpeg.exe in an 'ffmpeg' folder next to the app, or set YAPARR_FFMPEG.");
        FfmpegArgs.Capture(options); // checks the numbers before anything is announced
        if (options.WindowHandle is { } handle && !ShareWindows.Exists(handle)) throw new InvalidOperationException("That window is not open any more.");
        var source = node.StartMedia(room, "screen", name, signed: true, upstream: false, kbps: options.Kbps);
        var sharer = new ScreenSharer(source, options, ffmpegPath);
        sharer._run = Task.Run(() => sharer.PumpAsync(sharer._cts.Token));
        return sharer;
    }

    public MediaSource Source => _source;
    public long Frames => Interlocked.Read(ref _frames);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long KeyFrames => Interlocked.Read(ref _keys);

    private long _lastFrameTick, _maxGapMs;

    /// <summary>The longest wait between two frames from FFmpeg since this was last called (milliseconds), for spotting an encoder that falls behind.</summary>
    public long TakeMaxFrameGapMs() => Interlocked.Exchange(ref _maxGapMs, 0);

    /// <summary>The codec in use: what <see cref="ScreenOptions.Codec"/> asked for, or for Auto what turned out to work.</summary>
    public ScreenCodec Codec => _codec;

    /// <summary>When the graphics card could not be used and VP8 took over: why. Otherwise null.</summary>
    public string? FallbackReason => _fallback;

    /// <summary>The sharing ended; the exception is null when it was stopped on purpose.</summary>
    public event Action<Exception?>? Ended;

    /// <summary>What FFmpeg last printed to its error output, to explain a failure.</summary>
    public string LastErrors { get { lock (_gate) return string.Join(" | ", _errors); } }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            // Auto tries the graphics card first. If FFmpeg cannot start that encoder (no NVIDIA card, an old driver, all the
            // encoder sessions in use) it stops before the first picture, and VP8 in software takes over.
            var codecs = _options.Codec == ScreenCodec.Auto ? new[] { ScreenCodec.H264, ScreenCodec.Vp8 } : new[] { _options.Codec };
            for (int i = 0; i < codecs.Length; i++)
            {
                try { await RunAsync(codecs[i], ct).ConfigureAwait(false); break; }
                catch (Exception ex) when (!ct.IsCancellationRequested && i < codecs.Length - 1 && Interlocked.Read(ref _frames) == 0 && !WindowGone)
                {
                    _fallback = ex.Message;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally { KillProcess(); }
        Ended?.Invoke(failure);
    }

    private bool WindowGone => _options.WindowHandle is { } handle && !ShareWindows.Exists(handle);

    // One run of FFmpeg with one codec: returns when stopped on purpose, throws when FFmpeg stops by itself.
    private async Task RunAsync(ScreenCodec codec, CancellationToken ct)
    {
        _codec = codec;
        lock (_gate) _errors.Clear();
        var psi = new ProcessStartInfo(_ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in FfmpegArgs.Capture(_options, codec)) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        lock (_gate) _process = process;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 }) return;
            lock (_gate) { _errors.Enqueue(e.Data); while (_errors.Count > 5) _errors.Dequeue(); }
        };
        process.BeginErrorReadLine();

        var stdout = process.StandardOutput.BaseStream;
        Func<CancellationToken, Task<byte[]?>> next;
        if (codec == ScreenCodec.H264) next = new H264Reader(stdout).ReadAsync;
        else
        {
            try { await Ivf.ReadHeaderAsync(stdout, ct).ConfigureAwait(false); }
            catch (EndOfStreamException) { throw Stopped(process); }
            next = async token => (await Ivf.ReadFrameAsync(stdout, token).ConfigureAwait(false))?.Data;
        }

        bool started = false;
        try
        {
            while (await next(ct).ConfigureAwait(false) is { } frame)
            {
                bool isKey = ScreenCodecs.IsKeyFrame(codec, frame);
                // A viewer can only start at a key frame, so nothing is sent before the first one.
                if (!started && !isKey) continue;
                started = true;
                // How long FFmpeg took to produce this frame: a long wait means capture or encoding cannot keep up.
                long tick = Environment.TickCount64;
                if (Interlocked.Exchange(ref _lastFrameTick, tick) is var before and > 0)
                {
                    long gap = tick - before, seen;
                    while (gap > (seen = Interlocked.Read(ref _maxGapMs)) && Interlocked.CompareExchange(ref _maxGapMs, gap, seen) != seen) { }
                }
                await _source.SendFrameAsync(frame, isKey, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _frames);
                Interlocked.Add(ref _bytes, frame.Length);
                if (isKey) Interlocked.Increment(ref _keys);
            }
            if (!ct.IsCancellationRequested) throw Stopped(process);
        }
        finally { KillProcess(); }
    }

    // Why FFmpeg is not producing frames any more, as plainly as it can be said.
    private Exception Stopped(Process process)
    {
        try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
        var errors = LastErrors;
        if (_options.WindowHandle is { } handle && !ShareWindows.Exists(handle))
            return new InvalidOperationException("The window you were sharing was closed.");
        if ((errors.Contains("gfxcapture", StringComparison.OrdinalIgnoreCase) && errors.Contains("No such filter", StringComparison.OrdinalIgnoreCase))
            || errors.Contains("dup_frames", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("This FFmpeg is too old for screen capture: it needs a build from late October 2026 or newer (the Graphics Capture fixes are not in FFmpeg 9.0.2 or earlier).");
        return new InvalidOperationException("FFmpeg stopped. " + errors);
    }

    private void KillProcess()
    {
        Process? p;
        lock (_gate) { p = _process; _process = null; }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        p.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        KillProcess();
        try { _run?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _source.Dispose();
        _cts.Dispose();
    }
}
