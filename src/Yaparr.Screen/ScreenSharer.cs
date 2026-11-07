using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Voice;

namespace Yaparr.Screen;

/// <summary>
/// Shares the screen or one window with a room: FFmpeg captures and encodes, and every encoded frame goes out through the
/// stream tree. Capture is Windows Graphics Capture on Windows and x11grab on Linux under X11 (see <see cref="ScreenCapture"/>).
/// </summary>
public sealed class ScreenSharer : IDisposable
{
    private readonly MediaSource _source;
    private volatile ScreenOptions _options;
    private volatile bool _switch;
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
        if (ScreenCapture.WhyNotSupported is { } why) throw new PlatformNotSupportedException(why);
        ffmpegPath ??= FfmpegLocator.Find() ?? throw new FileNotFoundException("FFmpeg was not found. Put ffmpeg in an 'ffmpeg' folder next to the app, install it with your system's package manager, or set YAPARR_FFMPEG.");
        FfmpegArgs.Capture(options); // checks the numbers before anything is announced
        if (options.Portal != null) { if (PortalPipeline.Missing() is { } missing) throw new PlatformNotSupportedException(missing); }
        else if (options.WindowHandle is { } handle && !ShareWindows.Exists(handle)) throw new InvalidOperationException("That window is not open any more.");
        var source = node.StartMedia(room, "screen", name, signed: true, upstream: false, kbps: options.Kbps, channel: room.CurrentVoice?.ChannelId);
        var sharer = new ScreenSharer(source, options, ffmpegPath);
        if (options.ShareSound) sharer.StartSound(node, room);
        sharer.WatchPortal(options.Portal);
        sharer._run = Task.Run(() => sharer.PumpAsync(sharer._cts.Token));
        return sharer;
    }

    // A share from the desktop's portal can be stopped from the desktop's own indicator, which ends the stream under FFmpeg.
    private volatile bool _portalGone;

    private void WatchPortal(PortalScreenCast? portal)
    {
        if (portal == null) return;
        portal.Closed += () =>
        {
            if (_options.Portal != portal) return;
            _portalGone = true;
            KillProcess();
        };
    }

    /// <summary>The kind of the stream that carries a share's sound; its name says which share it belongs to (see <see cref="SoundNameFor"/>).</summary>
    public const string SoundKind = "screen-audio";

    public static string SoundNameFor(string shareId) => "audio:" + shareId;

    private MediaSource? _soundSource;
    private StreamAudioSender? _soundSender;
    private IDisposable? _soundCapture;

    /// <summary>Whether the sound is being shared along with the picture.</summary>
    public bool SharingSound => _soundCapture != null;

    /// <summary>When sound was asked for but could not be shared: why. The picture is shared anyway.</summary>
    public string? SoundProblem { get; private set; }

    /// <summary>The sound stopped on its own while sharing (the device went away, say); the picture goes on. Raised on another thread.</summary>
    public event Action<Exception>? SoundFailed;

    // The sound is a stream of its own beside the picture, so a stalled picture never holds the sound back.
    private void StartSound(ChatNode node, ChatRoom room)
    {
        try
        {
            _soundSource = node.StartMedia(room, SoundKind, SoundNameFor(_source.Info.Id), signed: true, upstream: false, kbps: 96, channel: _source.Info.Channel);
            _soundSender = new StreamAudioSender(_soundSource);
            StartSoundCapture();
        }
        catch (Exception ex)
        {
            SoundProblem = ex.Message;
            StopSound();
        }
    }

    // A window's own program, or for a monitor everything except Yaparr, so what others say in the voice chat is not sent back to them.
    // On Linux (see PulseAppCapture) a window of an X11 session is its own program (all its streams, found by process and by the
    // window's class names); a monitor is every program except Yaparr. On Wayland the portal does not say which program a window
    // belongs to, so the sound is every program except Yaparr unless one was picked by name (ScreenOptions.SoundProgram).
    private void StartSoundCapture()
    {
        if (OperatingSystem.IsLinux())
        {
            var linux = _options;
            var linuxSender = _soundSender ?? throw new InvalidOperationException("The sound is not being shared.");
            Action<float[], int> push = (samples, count) => linuxSender.Push(samples.AsSpan(0, count));
            Action<Exception> failed = ex =>
            {
                SoundProblem = ex.Message;
                SoundFailed?.Invoke(ex);
            };
            if (linux.Portal == null && linux.WindowHandle is { } window)
            {
                int owner = ShareWindows.ProcessId(window);
                if (owner == 0) throw new InvalidOperationException("That window is not open any more.");
                _soundCapture = PulseAppCapture.Start(owner, push, failed, ShareWindows.Classes(window));
            }
            else if (linux.Portal != null && !string.IsNullOrWhiteSpace(linux.SoundProgram))
                _soundCapture = PulseAppCapture.StartProgram(linux.SoundProgram, push, failed);
            else _soundCapture = PulseAppCapture.StartAllExceptThisProgram(push, failed);
            return;
        }
        if (!ProcessLoopback.IsSupported) throw new PlatformNotSupportedException("Sharing sound needs Windows 10 version 2004 or newer.");
        var options = _options;
        int pid = options.WindowHandle is { } handle ? ShareWindows.ProcessId(handle) : Environment.ProcessId;
        if (pid == 0) throw new InvalidOperationException("That window is not open any more.");
        var sender = _soundSender ?? throw new InvalidOperationException("The sound is not being shared.");
        _soundCapture = ProcessLoopback.Start(pid, options.WindowHandle == null, (samples, count) => sender.Push(samples.AsSpan(0, count)), ex =>
        {
            SoundProblem = ex.Message;
            SoundFailed?.Invoke(ex);
        });
    }

    /// <summary>
    /// Shares another monitor or window from now on, in the same stream: watchers keep watching and see the new picture at the
    /// next key frame (the sound follows too). Throws, and changes nothing, if the new window is not open.
    /// </summary>
    public void Change(ScreenOptions next)
    {
        FfmpegArgs.Capture(next);
        if (next.Portal != null) { if (PortalPipeline.Missing() is { } missing) throw new PlatformNotSupportedException(missing); }
        else if (next.WindowHandle is { } handle && !ShareWindows.Exists(handle)) throw new InvalidOperationException("That window is not open any more.");
        var previous = _options.Portal;
        _options = next;
        _portalGone = false;
        WatchPortal(next.Portal);
        _switch = true;
        Interlocked.Exchange(ref _lastFrameTick, 0);
        KillProcess();   // the pump starts FFmpeg again for the new target
        if (previous != null && previous != next.Portal) previous.Dispose();
        if (_soundSender != null)
        {
            var old = _soundCapture; _soundCapture = null;
            old?.Dispose();
            try { StartSoundCapture(); SoundProblem = null; }
            catch (Exception ex) { SoundProblem = ex.Message; }
        }
        Changed?.Invoke();
    }

    /// <summary>The monitor or window being shared was changed with <see cref="Change"/>.</summary>
    public event Action? Changed;

    /// <summary>Whether a monitor or a window is being shared, and which.</summary>
    public ScreenOptions Options => _options;

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

    private long _lastFrameTick, _maxGapMs, _longestGapMs;

    /// <summary>Like <see cref="TakeMaxFrameGapMs"/> but with a counter of its own, so the log can ask once a minute without taking it from the sharing panel.</summary>
    public long TakeLongestGapMs() => Interlocked.Exchange(ref _longestGapMs, 0);

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
            // With GpuScale, an NVIDIA attempt first tries scaling on the graphics card, and the processor path if FFmpeg cannot start that.
            do
            {
                _switch = false;
                long before = Interlocked.Read(ref _frames);
                var attempts = new List<(ScreenCodec Codec, HardwareEncoder? Encoder, bool GpuScale)>();
                foreach (var (codec, encoder) in Plan(_options))
                {
                    if (_options.GpuScale && codec == ScreenCodec.H264 && encoder == HardwareEncoder.Nvenc) attempts.Add((codec, encoder, true));
                    attempts.Add((codec, encoder, false));
                }
                var reasons = new List<string>();
                for (int i = 0; i < attempts.Count; i++)
                {
                    var (attempt, card, onCard) = attempts[i];
                    try { await RunAsync(attempt, card, onCard, ct).ConfigureAwait(false); break; }
                    catch (Exception) when (_switch) { break; }   // stopped on purpose to share something else
                    catch (Exception ex) when (!ct.IsCancellationRequested && i < attempts.Count - 1 && Interlocked.Read(ref _frames) == before && !WindowGone)
                    {
                        string? who = card is { } known ? HardwareEncoders.Maker(known) + (onCard ? " (scaling on the card)" : "") : null;
                        reasons.Add(who != null ? $"{who}: {ex.Message}" : ex.Message);
                        _fallback = string.Join("; ", reasons);
                    }
                }
            } while (_switch && !ct.IsCancellationRequested);
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

    private bool WindowGone => _portalGone || (_options.WindowHandle is { } handle && !ShareWindows.Exists(handle));

    // One run of FFmpeg with one codec: returns when stopped on purpose, throws when FFmpeg stops by itself.
    private async Task RunAsync(ScreenCodec codec, HardwareEncoder? encoder, bool gpuScale, CancellationToken ct)
    {
        _codec = codec;
        _hardware = encoder;
        lock (_gate) _errors.Clear();
        var portal = _options.Portal;
        var psi = new ProcessStartInfo(_ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = portal != null, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in FfmpegArgs.Capture(_options, codec, encoder ?? HardwareEncoder.Nvenc, gpuScale)) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        lock (_gate) _process = process;
        if (portal != null) StartPortalSource(portal, process);
        if (_switch) KillProcess();   // asked to share something else while this one was starting
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
        long startedAt = Environment.TickCount64;
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
                    while (gap > (seen = Interlocked.Read(ref _longestGapMs)) && Interlocked.CompareExchange(ref _longestGapMs, gap, seen) != seen) { }
                }
                await _source.SendFrameAsync(frame, isKey, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _frames);
                Interlocked.Add(ref _bytes, frame.Length);
                if (isKey) Interlocked.Increment(ref _keys);
            }
            if (!ct.IsCancellationRequested && !_switch)
            {
                // x11grab cannot follow a window that changes size: it stops, and FFmpeg starts again at the new size (viewers pick the
                // new size up at the next key frame, as when another window is chosen). Only if pictures were flowing for a while and
                // the window is still open; otherwise this is a real failure and ends the share.
                if (OperatingSystem.IsLinux() && started && _options.Portal == null && _options.WindowHandle is { } window
                    && Environment.TickCount64 - startedAt > 1000 && ShareWindows.Exists(window))
                {
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    _switch = true;
                    return;
                }
                throw Stopped(process);
            }
        }
        finally { KillProcess(); }
    }

    // The picture of a Wayland share: GStreamer reads the portal's PipeWire stream (it inherits the connection) and its raw
    // pictures are copied into FFmpeg's standard input, which ends when GStreamer does.
    private Process? _gstreamer;

    private void StartPortalSource(PortalScreenCast portal, Process ffmpeg)
    {
        int fd = PortalPipeline.AllowInherit(portal.PipeWireFd);
        var psi = new ProcessStartInfo("gst-launch-1.0") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in PortalPipeline.GstArguments(portal.Stream, fd, _options)) psi.ArgumentList.Add(a);
        var gst = Process.Start(psi) ?? throw new InvalidOperationException("GStreamer did not start.");
        lock (_gate) _gstreamer = gst;
        gst.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 }) return;
            lock (_gate) { _errors.Enqueue("GStreamer: " + e.Data); while (_errors.Count > 5) _errors.Dequeue(); }
        };
        gst.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {
            try { await gst.StandardOutput.BaseStream.CopyToAsync(ffmpeg.StandardInput.BaseStream).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            finally { try { ffmpeg.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } }
        });
    }

    // Why FFmpeg is not producing frames any more, as plainly as it can be said.
    private Exception Stopped(Process process)
    {
        try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
        var errors = LastErrors;
        if (_portalGone) return new InvalidOperationException("The screen share was stopped from the desktop.");
        if (_options.WindowHandle is { } handle && !ShareWindows.Exists(handle))
            return new InvalidOperationException("The window you were sharing was closed.");
        if ((errors.Contains("gfxcapture", StringComparison.OrdinalIgnoreCase) && errors.Contains("No such filter", StringComparison.OrdinalIgnoreCase))
            || errors.Contains("dup_frames", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("This FFmpeg is too old for screen capture: it needs a build from late October 2026 or newer (the Graphics Capture fixes are not in FFmpeg 9.0.2 or earlier).");
        return new InvalidOperationException("FFmpeg stopped. " + errors);
    }

    private void KillProcess()
    {
        Process? p, gst;
        lock (_gate) { p = _process; _process = null; gst = _gstreamer; _gstreamer = null; }
        foreach (var each in new[] { gst, p })
        {
            if (each == null) continue;
            try { if (!each.HasExited) each.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            each.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        KillProcess();
        try { _run?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        StopSound();
        _options.Portal?.Dispose();
        _source.Dispose();
        _cts.Dispose();
    }
}
