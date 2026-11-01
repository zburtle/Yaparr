using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Voice;

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
    private HardwareEncoder? _hardware;
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
        var source = node.StartMedia(room, "screen", name, signed: true, upstream: false, kbps: options.Kbps, channel: room.CurrentVoice?.ChannelId);
        var sharer = new ScreenSharer(source, options, ffmpegPath);
        if (options.ShareSound) sharer.StartSound(node, room);
        sharer._run = Task.Run(() => sharer.PumpAsync(sharer._cts.Token));
        return sharer;
    }

    /// <summary>The kind of the stream that carries a share's sound; its name says which share it belongs to (see <see cref="SoundNameFor"/>).</summary>
    public const string SoundKind = "screen-audio";

    public static string SoundNameFor(string shareId) => "audio:" + shareId;

    private MediaSource? _soundSource;
    private StreamAudioSender? _soundSender;
    private ProcessLoopback? _soundCapture;

    /// <summary>Whether the sound is being shared along with the picture.</summary>
    public bool SharingSound => _soundCapture != null;

    /// <summary>When sound was asked for but could not be shared: why. The picture is shared anyway.</summary>
    public string? SoundProblem { get; private set; }

    // The sound is a stream of its own beside the picture, so a stalled picture never holds the sound back.
    private void StartSound(ChatNode node, ChatRoom room)
    {
        try
        {
            if (!ProcessLoopback.IsSupported) throw new PlatformNotSupportedException("Sharing sound needs Windows 10 version 2004 or newer.");
            // A window's own program, or for a monitor everything except Yaparr, so what others say in the voice chat is not sent back to them.
            int pid = _options.WindowHandle is { } handle ? ShareWindows.ProcessId(handle) : Environment.ProcessId;
            bool exclude = _options.WindowHandle == null;
            if (pid == 0) throw new InvalidOperationException("That window is not open any more.");
            _soundSource = node.StartMedia(room, SoundKind, SoundNameFor(_source.Info.Id), signed: true, upstream: false, kbps: 96, channel: _source.Info.Channel);
            _soundSender = new StreamAudioSender(_soundSource);
            var sender = _soundSender;
            _soundCapture = ProcessLoopback.Start(pid, exclude, (samples, count) => sender.Push(samples.AsSpan(0, count)));
        }
        catch (Exception ex)
        {
            SoundProblem = ex.Message;
            StopSound();
        }
    }

    private void StopSound()
    {
        var capture = _soundCapture; _soundCapture = null;
        var sender = _soundSender; _soundSender = null;
        var source = _soundSource; _soundSource = null;
        capture?.Dispose();
        sender?.Dispose();
        source?.Dispose();
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

    /// <summary>The graphics card encoder in use; null for VP8 in software.</summary>
    public HardwareEncoder? Encoder => _hardware;

    /// <summary>When the graphics card could not be used and VP8 took over: why, for each encoder that was tried. Otherwise null.</summary>
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
            // Auto tries the graphics cards first, one after the other. If FFmpeg cannot start an encoder (no such card, an old
            // driver, all the encoder sessions in use) it stops before the first picture, and the next one is tried, VP8 in
            // software last.
            var plan = Plan(_options);
            var reasons = new List<string>();
            for (int i = 0; i < plan.Count; i++)
            {
                try { await RunAsync(plan[i].Codec, plan[i].Encoder, ct).ConfigureAwait(false); break; }
                catch (Exception ex) when (!ct.IsCancellationRequested && i < plan.Count - 1 && Interlocked.Read(ref _frames) == 0 && !WindowGone)
                {
                    reasons.Add(plan[i].Encoder is { } card ? $"{HardwareEncoders.Maker(card)}: {ex.Message}" : ex.Message);
                    _fallback = string.Join("; ", reasons);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally { KillProcess(); }
        Ended?.Invoke(failure);
    }

    /// <summary>What is tried, in order, for these options.</summary>
    public static List<(ScreenCodec Codec, HardwareEncoder? Encoder)> Plan(ScreenOptions options)
    {
        var plan = new List<(ScreenCodec, HardwareEncoder?)>();
        if (options.Codec != ScreenCodec.Vp8)
            foreach (var card in options.Encoder is { } only ? new[] { only } : HardwareEncoders.InOrder) plan.Add((ScreenCodec.H264, card));
        if (options.Codec != ScreenCodec.H264) plan.Add((ScreenCodec.Vp8, null));
        return plan;
    }

    private bool WindowGone => _options.WindowHandle is { } handle && !ShareWindows.Exists(handle);

    // One run of FFmpeg with one codec: returns when stopped on purpose, throws when FFmpeg stops by itself.
    private async Task RunAsync(ScreenCodec codec, HardwareEncoder? encoder, CancellationToken ct)
    {
        _codec = codec;
        _hardware = encoder;
        lock (_gate) _errors.Clear();
        var psi = new ProcessStartInfo(_ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in FfmpegArgs.Capture(_options, codec, encoder ?? HardwareEncoder.Nvenc)) psi.ArgumentList.Add(a);
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
        if (codec == ScreenCodec.H264) next = new H264Reader(stdout) { RepeatParameterSets = true }.ReadAsync;
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
        StopSound();
        _source.Dispose();
        _cts.Dispose();
    }
}
