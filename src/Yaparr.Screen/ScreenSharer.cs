using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Voice;

namespace Yaparr.Screen;

/// <summary>
/// Shares the screen or one window with a room: the capture and the encoder run inside this program with FFmpeg's libraries (<see cref="InProcessCapture"/>,
/// <see cref="InProcessEncoder"/>; no FFmpeg program is started), and every encoded frame goes out through the stream tree. Capture is Windows Graphics Capture on
/// Windows, x11grab on Linux under X11, and the desktop's portal through GStreamer on Wayland (see <see cref="ScreenCapture"/>).
/// </summary>
public sealed partial class ScreenSharer : IDisposable
{
    private readonly MediaSource _source;
    private volatile ScreenOptions _options;
    private volatile bool _switch;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Queue<string> _errors = new();
    private Task? _run;
    private long _frames, _bytes, _keys;
    private volatile ScreenCodec _codec;
    private HardwareEncoder? _hardware;
    private volatile string? _fallback;

    public ScreenSharer(MediaSource source, ScreenOptions options)
    {
        _source = source;
        _options = options;
        _policy = new QualityPolicy();
        // A viewer's request for a key frame is answered on the very next picture (at most one every 500 ms, see KeyFramePacer).
        _onKeyRequested = () => _pacer.Request();
        _source.KeyFrameRequested += _onKeyRequested;
    }

    /// <summary>
    /// Announces a screen share in the room and starts sending it, to the voice channel this device is in or, when it is not in one, the
    /// channel another of your linked devices is in (<see cref="ChatRoom.ScreenShareChannel"/>).
    /// </summary>
    public static ScreenSharer Start(ChatNode node, ChatRoom room, string name, ScreenOptions options)
    {
        if (ScreenCapture.WhyNotSupported is { } why) throw new PlatformNotSupportedException(why);
        if (!FfmpegLibraries.TryLoad(out var noLibraries)) throw new FileNotFoundException("FFmpeg's libraries are needed to share a screen. " + noLibraries);
        options.Validate();   // the numbers, before anything is announced
        if (options.Portal != null) { if (PortalPipeline.Missing() is { } missing) throw new PlatformNotSupportedException(missing); }
        else if (options.WindowHandle is { } handle && !ShareWindows.Exists(handle)) throw new InvalidOperationException("That window is not open any more.");
        ProcessPower.KeepResponsive();
        var source = node.StartMedia(room, "screen", name, signed: true, upstream: false, kbps: options.Kbps, channel: room.ScreenShareChannel);
        var sharer = new ScreenSharer(source, options);
        if (options.ShareSound) sharer.StartSound(node, room);
        if (LowStream.Enabled) sharer.StartLowStream(node, room);   // made ready; the stream itself starts only when a viewer needs it
        sharer.WatchPortal(options.Portal);
        sharer.StartSending();
        sharer._run = Task.Run(() => sharer.PumpAsync(sharer._cts.Token));
        sharer.StartQualityWatch();
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

    private LowStream? _low;

    /// <summary>The share's second, lower stream (see <see cref="LowStream"/>), or null when shares offer none (YAPARR_LOW_STREAM=0) or it could not be started.</summary>
    public LowStream? Low => _low;

    private void StartLowStream(ChatNode node, ChatRoom room)
    {
        try { _low = new LowStream(node, room, _source, RunOptions, () => (_codec, _hardware)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { _low = null; }
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
        next.Validate();
        if (next.Portal != null) { if (PortalPipeline.Missing() is { } missing) throw new PlatformNotSupportedException(missing); }
        else if (next.WindowHandle is { } handle && !ShareWindows.Exists(handle)) throw new InvalidOperationException("That window is not open any more.");
        var previous = _options.Portal;
        _options = next;
        _portalGone = false;
        WatchPortal(next.Portal);
        _switch = true;
        Interlocked.Exchange(ref _lastFrameTick, 0);
        KillProcess();   // the run ends (GStreamer is stopped; the capture here sees the switch) and the pump starts again for the new target
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

    /// <summary>
    /// Each frame just before it is sent to the room (raised on the thread that sends, so a handler has to be quick), for a preview of what
    /// viewers get: give them to a <see cref="ScreenViewer"/> through a <see cref="LocalFrames"/>. Nothing is done with them when nobody listens.
    /// </summary>
    public event Action<byte[], bool>? FrameEncoded;
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

    /// <summary>
    /// An encoder is running now: <see cref="Codec"/>, <see cref="Encoder"/> and <see cref="InProcess"/> say which, and <see cref="FallbackReason"/> why
    /// an earlier choice was not used. Raised again if another one has to take over. On another thread, so a handler has to be quick.
    /// </summary>
    public event Action? EncoderStarted;

    private volatile string _lastArguments = "";
    // When the share began; the figures in the report are averages over that, not over the time since the capture was last started (a new quality level does that).
    private readonly DateTime _shareStartedAt = DateTime.UtcNow;

    /// <summary>
    /// A plain description of this share for a bug report: what is being shared and how, how many pictures were sent and how fast, and what
    /// the programs behind it last said. A viewer that sees one picture and no more shows up as few frames and a long gap here.
    /// </summary>
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        var seconds = (DateTime.UtcNow - _shareStartedAt).TotalSeconds;
        text.AppendLine($"  Sharing: {(_options.Portal != null ? (_options.Portal.IsWindow ? "a window, chosen in the desktop's picker" : "a screen, chosen in the desktop's picker") : _options.WindowHandle != null ? "a window" : "a monitor")}, {_options.Fps} fps, {_options.Kbps} kbit/s, up to {_options.MaxWidth}x{_options.MaxHeight}, a key frame every {_options.KeyFrameSeconds:0.#} s");
        text.AppendLine($"  Encoder: {ScreenCodecs.Describe(Codec, Encoder)}, in this program{(FallbackReason is { } why ? $" (not used: {why})" : "")}");
        text.AppendLine($"  Sent: {Frames} frames, {KeyFrames} of them key frames ({KeyFramesForced} made because a viewer asked), {Bytes / 1024} KB in {seconds:0} s ({(seconds > 1 ? Frames / seconds : 0):0.0} frames per second on average)");
        var figures = Figures();
        text.AppendLine($"  Key frames: {figures.DescribeKeys()}");
        if (figures.Encodes > 0) text.AppendLine($"  Encoder in this program: {figures.DescribeEncoding(FrameBudgetMs)}; picture {(PictureSize is { } size ? $"{size.Width}x{size.Height}" : "unknown")}");
        text.AppendLine(_options.Portal != null ? $"  Capture: GStreamer reads the desktop's stream, started {figures.Restarts + 1} times"
                                                : $"  Capture: in this program, {(CaptureOnCard ? "pictures kept on the graphics card" : "pictures copied to memory")}, so its processor time is in this program's own; started again {figures.Restarts} times");
        text.AppendLine($"  How the encoder was set up: {(EncoderDetails.Length > 0 ? EncoderDetails : "not started yet")}");
        text.AppendLine($"  Longest wait for a frame from the encoder since this was last asked: {Interlocked.Read(ref _maxGapMs)} ms; running: {(IsRunning ? "yes" : "no")}");
        text.AppendLine($"  Adaptive quality: {QualityNote ?? "not started"}");
        text.AppendLine($"  Key frames are sent at {Yaparr.Chat.ChatNode.FramePaceMultiple:0.##} times the bitrate{(ProcessPower.KeepResponsive() is { } power ? "; " + power : "")}");
        text.AppendLine($"  Sound: {(SharingSound ? "shared" : SoundProblem ?? "not shared")}");
        text.AppendLine($"  Lower stream: {(_low?.Describe() ?? (LowStream.Enabled ? "could not be started" : "not offered (YAPARR_LOW_STREAM=0)"))}");
        if (_options.Portal != null)
        {
            bool gst; lock (_gate) gst = _gstreamer is { HasExited: false };
            text.AppendLine($"  GStreamer is {(gst ? "running" : "not running")}; the portal stream is {_options.Portal.Stream.Width}x{_options.Portal.Stream.Height}");
        }
        text.AppendLine($"  Capture: {_lastArguments}");
        text.AppendLine($"  The capture and GStreamer last said: {(LastErrors.Length > 0 ? LastErrors : "nothing")}");
        return text.ToString();
    }

    /// <summary>Whether the share is still going (not ended or stopped).</summary>
    public bool IsRunning => _run is { IsCompleted: false };

    /// <summary>What the capture (or GStreamer) last said, to explain a failure.</summary>
    public string LastErrors { get { lock (_gate) return string.Join(" | ", _errors); } }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            // Auto tries the graphics cards first, one after the other. If an encoder cannot start (no such card, an old driver, all the encoder sessions in use)
            // the next one is tried, VP8 in software last. For each, the pictures are kept on the graphics card first where the encoder can read them there
            // (NVIDIA's and AMD's on Windows), then copied to memory.
            do
            {
                _switch = false;
                long before = Interlocked.Read(ref _frames);
                var attempts = new List<(ScreenCodec Codec, HardwareEncoder? Encoder, bool OnCard)>();
                foreach (var (codec, encoder) in Plan(_options))
                {
                    if (WantsCaptureOnCard(_options, codec, encoder)) attempts.Add((codec, encoder, true));
                    attempts.Add((codec, encoder, false));
                }
                var reasons = new List<string>();
                for (int i = 0; i < attempts.Count; i++)
                {
                    var (attempt, card, onCard) = attempts[i];
                    try { await (_options.Portal != null ? RunPortalAsync(attempt, card, ct) : RunCaptureAsync(onCard, attempt, card, ct)).ConfigureAwait(false); break; }
                    catch (Exception) when (_switch) { break; }   // stopped on purpose to share something else
                    catch (Exception ex) when (!ct.IsCancellationRequested && i < attempts.Count - 1 && Interlocked.Read(ref _frames) == before && !WindowGone)
                    {
                        string who = card is { } known ? (attempt == ScreenCodec.Av1 ? "AV1 " : "") + HardwareEncoders.Maker(known) + (onCard ? " (pictures kept on the card)" : "") : "VP8";
                        reasons.Add($"{who}: {ex.Message}");
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
        var cards = options.Encoder is { } only ? new[] { only } : HardwareEncoders.InOrder.ToArray();
        // AV1 is only tried when it was asked for, on every card in turn, and then what Auto would have done, so a computer without a card that
        // does AV1 still shares (H.264, or VP8 in software at last).
        if (options.Codec == ScreenCodec.Av1)
            foreach (var card in cards) plan.Add((ScreenCodec.Av1, card));
        if (options.Codec is not ScreenCodec.Vp8)
            foreach (var card in cards) plan.Add((ScreenCodec.H264, card));
        if (options.Codec != ScreenCodec.H264) plan.Add((ScreenCodec.Vp8, null));
        return plan;
    }

    private bool WindowGone => _portalGone || (_options.WindowHandle is { } handle && !ShareWindows.Exists(handle));

    // ---- sending, on a thread of its own (see FrameSender) ------------------------------------------------------------------------------

    private FrameSender? _sender;

    private FrameSender Sender => _sender ??= new FrameSender(DeliverAsync, _stats, () => _pacer.Request(), () => TimeSpan.FromMilliseconds(FrameBudgetMs).Ticks);

    /// <summary>How far behind sending may fall before what is queued is thrown away and a key frame is asked for.</summary>
    public static TimeSpan MaxSendBacklog => FrameSender.MaxBacklog;

    /// <summary>Frames waiting to be sent.</summary>
    public int QueuedFrames => _sender?.Queued ?? 0;

    internal void StartSending() => Sender.Start(_cts.Token);

    // The encoding loop's side: never waits.
    internal void Enqueue(byte[] frame, bool isKey) => Sender.Enqueue(frame, isKey);

    // Sends one encoded frame to the room and keeps the figures: the gaps between frames (a long gap means capture, encoding or sending cannot keep
    // up), and the counts.
    private async Task DeliverAsync(byte[] frame, bool isKey, CancellationToken ct)
    {
        long tick = Environment.TickCount64;
        if (Interlocked.Exchange(ref _lastFrameTick, tick) is var before and > 0)
        {
            long gap = tick - before, seen;
            while (gap > (seen = Interlocked.Read(ref _maxGapMs)) && Interlocked.CompareExchange(ref _maxGapMs, gap, seen) != seen) { }
            while (gap > (seen = Interlocked.Read(ref _longestGapMs)) && Interlocked.CompareExchange(ref _longestGapMs, gap, seen) != seen) { }
        }
        long answered = isKey ? _pacer.KeySent(tick) : -1;
        _stats.Frame(frame.Length, isKey);
        FrameEncoded?.Invoke(frame, isKey);
        await _source.SendFrameAsync(frame, isKey, ct).ConfigureAwait(false);
        if (answered >= 0)
        {
            _stats.Answered(answered);
            KeyFrameAnswered?.Invoke(answered, frame.Length);
        }
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, frame.Length);
        if (isKey) Interlocked.Increment(ref _keys);
    }

    private readonly ShareStats _stats = new();
    private int _runs;
    private volatile string _encoderDetails = "";
    private (int Width, int Height)? _picture;

    /// <summary>What the share has counted about its encoder and key frames, in totals since it began (see <see cref="ShareStats"/>).</summary>
    public ShareFigures Figures() => _stats.Figures(_pacer, Math.Max(0, Volatile.Read(ref _runs) - 1));

    /// <summary>The largest values since this reader (0 for the sharing panel, 1 for the log) last asked.</summary>
    public ShareMaxima TakeMaxima(int reader) => _stats.TakeMaxima(reader);

    /// <summary>The time there is for each picture: the time between two, in milliseconds.</summary>
    public double FrameBudgetMs => 1000.0 / Math.Max(1, RunFps);

    /// <summary>The size of the pictures the encoder is given (the capture says it as it starts); null before.</summary>
    public (int Width, int Height)? PictureSize => _picture;

    /// <summary>How the current encoder was set up, for the log: the libraries and options in use.</summary>
    public string EncoderDetails => _encoderDetails;

    /// <summary>
    /// A key frame that a viewer asked for went out: how many milliseconds after the request arrived, and how big it was. On another thread, so a handler has to be quick.
    /// </summary>
    public event Action<long, int>? KeyFrameAnswered;

    /// <summary>How many key frames were made on the very next picture because a viewer asked for one.</summary>
    public long KeyFramesForced => _pacer.Forced;

    private volatile bool _captureOnCard;

    /// <summary>With the capture in this program: the pictures stay on the graphics card all the way to the encoder, and are never copied to memory.</summary>
    public bool CaptureOnCard => _captureOnCard;

    private volatile InProcessCapture? _captureHere;

    /// <summary>With the capture in this program: how many pictures it has handed over, and how many times it was asked and had none yet (0 and 0 otherwise).</summary>
    public (long Pictures, long EmptyPolls) CaptureCounts => _captureHere is { } c ? (c.Pictures, c.EmptyPolls) : (0, 0);
    private readonly KeyFramePacer _pacer = new();
    private readonly Action _onKeyRequested;

    /// <summary>
    /// Whether to try keeping the pictures on the graphics card for this encoder first: Windows Graphics Capture, an encoder that reads Direct3D 11 pictures (NVIDIA's,
    /// AMD's), asked for (see <see cref="ScreenOptions.CaptureOnCard"/>), and not turned off by <see cref="InProcessGuard"/> after the program ended in the middle of a share.
    /// </summary>
    private static bool WantsCaptureOnCard(ScreenOptions options, ScreenCodec codec, HardwareEncoder? encoder)
        => options.CaptureOnCard && options.Portal == null && options.X11Display == null && InProcessEncoder.ReadsFromCard(codec, encoder)
           && (options.InProcessAsked || InProcessGuard.Allowed);

    // Capture here as well: FFmpeg's capture filter runs as a filter graph in this program (see InProcessCapture), and its pictures go to the encoder as
    // they are. The capture hands over a picture when the screen changes, so the share's own clock decides when a picture is encoded, and the last
    // one is repeated when there is none: the encoder gets an even 60 a second instead of the bursts of copies the command-line FFmpeg makes.
    private async Task RunCaptureAsync(bool onCard, ScreenCodec codec, HardwareEncoder? kind, CancellationToken ct)
    {
        _codec = codec;
        _hardware = codec == ScreenCodec.Vp8 ? null : kind;
        _captureOnCard = onCard;
        _picture = null;
        Interlocked.Increment(ref _runs);
        lock (_gate) _errors.Clear();
        // The program ending in the middle of this (native code crashing) is what InProcessGuard is for: the mark is taken away when the share ends here.
        InProcessGuard.Begin();
        try { await RunCaptureCoreAsync(onCard, codec, _hardware, ct).ConfigureAwait(false); }
        finally { InProcessGuard.End(); }
    }

    private async Task RunCaptureCoreAsync(bool onCard, ScreenCodec codec, HardwareEncoder? kind, CancellationToken ct)
    {
        // At a lower level of adaptive quality the capture and the encoder are built again for the size and frame rate of that level.
        var run = RunOptions();
        Volatile.Write(ref _runStartedMs, Environment.TickCount64);
        InProcessCapture capture;
        try { capture = new InProcessCapture(run, onCard); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { throw CaptureFailed(ex.Message); }
        using var disposeCapture = capture;
        _captureHere = capture;
        _lastArguments = "in this program: " + capture.Graph;
        try { await capture.WaitForFirstAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { throw CaptureFailed(capture.EndedBecause ?? ex.Message); }
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(capture.Width, capture.Height, run, capture, codec, kind); }
        catch (Exception) { throw; }   // no such encoder to use, or it will not read pictures from the card: the next way is tried
        using var disposeEncoder = encoder;
        _picture = (capture.Width, capture.Height);
        _shape = new RunShape(run.Fps, run.MaxWidth, run.MaxHeight);
        _liveBitrate = encoder.LiveBitrate;
        _canRestart = true;
        Volatile.Write(ref _pendingKbps, 0);
        _encoderLive = true;
        _encoderDetails = $"in this program: {encoder.Describe()}; capture: in this program{(onCard ? ", pictures kept on the graphics card" : "")}, {capture.Graph}";
        EncoderStarted?.Invoke();

        long frameTicks = Math.Max(1, Stopwatch.Frequency / Math.Max(1, run.Fps));
        long due = Stopwatch.GetTimestamp(), lastArrival = 0, startedAtMs = Environment.TickCount64;
        bool started = false;
        var ready = new List<(byte[] Frame, bool Key)>(2);
        void Collect(byte[] frame, bool key) => ready.Add((frame, key));
        while (!ct.IsCancellationRequested && !_levelRestart && !_switch)
        {
            long wait = due - Stopwatch.GetTimestamp();
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), ct).ConfigureAwait(false);
            // A late tick does not make a burst afterwards: the clock skips ahead instead of catching up.
            due = Math.Max(due + frameTicks, Stopwatch.GetTimestamp() - frameTicks);
            using var picture = capture.TakeNewest(out bool isNew, out long arrivedAt);
            if (picture == null) { if (capture.EndedBecause != null) break; continue; }
            if (isNew)
            {
                // How long the capture took over this picture, as the stand-in for "waiting for the capture" (long gaps are stalls, tiny ones bursts).
                if (lastArrival != 0) _stats.Waited(Stopwatch.GetElapsedTime(lastArrival, arrivedAt).Ticks, TimeSpan.FromMilliseconds(FrameBudgetMs).Ticks);
                lastArrival = arrivedAt;
            }
            else
            {
                if (capture.EndedBecause != null) break;
                _stats.Repeated();
            }
            bool force = _pacer.ShouldForce(Environment.TickCount64);
            ready.Clear();
            ApplyPendingBitrate(encoder);
            long encodeStart = Stopwatch.GetTimestamp();
            encoder.Encode(picture, force, Collect);
            _low?.Offer(picture);   // the lower stream, when someone watches it
            var took = Stopwatch.GetElapsedTime(encodeStart);
            _stats.Encoded(took.Ticks, took.TotalMilliseconds > FrameBudgetMs);
            foreach (var (frame, key) in ready)
            {
                bool isKey = key && ScreenCodecs.IsKeyFrame(codec, frame);
                if (!started && !isKey) continue;
                started = true;
                Enqueue(frame, isKey);
            }
        }
        _captureHere = null;
        _encoderLive = false;
        _canRestart = false;
        // Adaptive quality asked for another size or frame rate: this run ends and the next one is built for it (see PumpAsync).
        if (_levelRestart) { _levelRestart = false; _switch = true; }
        // x11grab cannot follow a window that changes size: it stops, and the capture starts again at the new size (viewers pick the new size up at the next key frame).
        // Only if pictures were flowing for a while and the window is still open; otherwise this is a real failure and ends the share.
        if (!ct.IsCancellationRequested && !_switch && OperatingSystem.IsLinux() && started && run.WindowHandle is { } window
            && Environment.TickCount64 - startedAtMs > 1000 && ShareWindows.Exists(window))
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            _switch = true;
            return;
        }
        if (!ct.IsCancellationRequested && !_switch) throw CaptureFailed(capture.EndedBecause ?? "The capture stopped.");
    }

    // Why the capture in this program is not giving pictures, as plainly as it can be said.
    private Exception CaptureFailed(string said)
    {
        if (_portalGone) return new InvalidOperationException("The screen share was stopped from the desktop.");
        if (_options.WindowHandle is { } handle && !ShareWindows.Exists(handle))
            return new InvalidOperationException("The window you were sharing was closed.");
        if ((said.Contains("gfxcapture", StringComparison.OrdinalIgnoreCase) && said.Contains("No such filter", StringComparison.OrdinalIgnoreCase))
            || said.Contains("dup_frames", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("These FFmpeg libraries are too old for screen capture: they need a build from late October 2026 or newer (the Graphics Capture fixes are not in FFmpeg 9.0.2 or earlier).");
        return new InvalidOperationException("The capture in this program stopped. " + said);
    }

    // The picture of a Wayland share: GStreamer reads the portal's PipeWire stream (it inherits the connection).
    private Process? _gstreamer;

    // GStreamer's pictures, paced to the frame rate, go to <paramref name="target"/>: the pipe the encoder in this program reads (NV12).
    private void StartPortalSource(PortalScreenCast portal, ScreenOptions options, Stream target, bool nv12, Action done)
    {
        int fd = PortalPipeline.AllowInherit(portal.PipeWireFd);
        var psi = new ProcessStartInfo("gst-launch-1.0") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in PortalPipeline.GstArguments(portal.Stream, fd, options, nv12)) psi.ArgumentList.Add(a);
        var gst = Process.Start(psi) ?? throw new InvalidOperationException("GStreamer did not start.");
        lock (_gate) _gstreamer = gst;
        gst.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 }) return;
            lock (_gate) { _errors.Enqueue("GStreamer: " + e.Data); while (_errors.Count > 5) _errors.Dequeue(); }
        };
        gst.BeginErrorReadLine();
        _ = Task.Run(() => PaceAsync(gst.StandardOutput.BaseStream, target, PortalPipeline.FrameSize(portal.Stream, options), options.Fps, done));
    }

    // Wayland, encoded in this program, with no FFmpeg at all: GStreamer reads the desktop's stream, scales it and hands over NV12 pictures (paced by PaceAsync into a
    // pipe), and the encoder here takes each one as it is. A new level of adaptive quality ends the run, and the next starts GStreamer again at the new size and rate.
    private async Task RunPortalAsync(ScreenCodec codec, HardwareEncoder? kind, CancellationToken ct)
    {
        _codec = codec;
        _hardware = codec == ScreenCodec.Vp8 ? null : kind;
        _captureOnCard = false;
        _picture = null;
        Interlocked.Increment(ref _runs);
        lock (_gate) _errors.Clear();
        InProcessGuard.Begin();
        try { await RunPortalCoreAsync(codec, _hardware, ct).ConfigureAwait(false); }
        finally { InProcessGuard.End(); }
    }

    private async Task RunPortalCoreAsync(ScreenCodec codec, HardwareEncoder? kind, CancellationToken ct)
    {
        var run = RunOptions();
        Volatile.Write(ref _runStartedMs, Environment.TickCount64);
        var portal = run.Portal ?? throw new InvalidOperationException("There is no stream from the desktop to share.");
        var size = PortalPipeline.FrameSize(portal.Stream, run);
        _lastArguments = "gst-launch-1.0 " + string.Join(' ', PortalPipeline.GstArguments(portal.Stream, 0, run, nv12: true)) + "  (encoded in this program)";

        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(size.Width, size.Height, run, null, codec, kind); }
        catch (Exception) { throw; }   // no such encoder to use: the next way of encoding is tried
        using var disposeEncoder = encoder;
        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var reader = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, pipe.ClientSafePipeHandle);
        _picture = size;
        _shape = new RunShape(run.Fps, run.MaxWidth, run.MaxHeight);
        _liveBitrate = encoder.LiveBitrate;
        _canRestart = true;
        Volatile.Write(ref _pendingKbps, 0);
        _encoderLive = true;
        _encoderDetails = $"in this program: {encoder.Describe()}; capture: {_lastArguments}";
        EncoderStarted?.Invoke();
        if (_switch) return;
        StartPortalSource(portal, run, pipe, nv12: true, () => { try { pipe.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });

        var buffer = new byte[encoder.PictureBytes];
        bool started = false;
        var ready = new List<(byte[] Frame, bool Key)>(2);
        void Collect(byte[] frame, bool key) => ready.Add((frame, key));
        try
        {
            while (!_levelRestart && !_switch)
            {
                long waitStart = Stopwatch.GetTimestamp();
                try { await reader.ReadExactlyAsync(buffer, ct).ConfigureAwait(false); }
                catch (EndOfStreamException) { break; }
                _stats.Waited(Stopwatch.GetElapsedTime(waitStart).Ticks, TimeSpan.FromMilliseconds(FrameBudgetMs).Ticks);
                bool force = _pacer.ShouldForce(Environment.TickCount64);
                ready.Clear();
                ApplyPendingBitrate(encoder);
                long encodeStart = Stopwatch.GetTimestamp();
                encoder.Encode(buffer, force, Collect);
                _low?.Offer(buffer, size.Width, size.Height);
                var took = Stopwatch.GetElapsedTime(encodeStart);
                _stats.Encoded(took.Ticks, took.TotalMilliseconds > FrameBudgetMs);
                foreach (var (frame, key) in ready)
                {
                    bool isKey = key && ScreenCodecs.IsKeyFrame(codec, frame);
                    if (!started && !isKey) continue;
                    started = true;
                    Enqueue(frame, isKey);
                }
            }
            if (_levelRestart) { _levelRestart = false; _switch = true; }
            if (!ct.IsCancellationRequested && !_switch)
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }   // GStreamer's last words
                throw _portalGone ? new InvalidOperationException("The screen share was stopped from the desktop.")
                                  : new InvalidOperationException("GStreamer stopped. " + LastErrors);
            }
        }
        finally { _encoderLive = false; _canRestart = false; KillProcess(); }
    }

    /// <summary>
    /// Hands the encoder exactly <paramref name="fps"/> pictures a second whatever GStreamer does. GStreamer writes raw pictures of a known size; the newest
    /// one is kept and written at each tick (a still screen gives the same picture again), and the ones in between are dropped. A desktop whose
    /// stream makes GStreamer hand over a burst (many minutes of copies of one picture in a few seconds were seen from a KDE desktop) therefore cannot
    /// make the encoder send many times the bitrate it was asked for, which is what left viewers with the first picture and nothing after it.
    /// </summary>
    public static async Task PaceAsync(Stream raw, Stream encoder, (int Width, int Height) size, int fps, Action done)
    {
        int bytes = size.Width * size.Height * 3 / 2;   // one picture of planar 4:2:0, which is what GStreamer is asked to write
        var gate = new object();
        var free = new Stack<byte[]>();      // pictures nobody holds, so a picture a frame is not a new array each time
        byte[]? newest = null;
        bool ended = false;

        var reader = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    byte[] buffer;
                    lock (gate) buffer = free.Count > 0 ? free.Pop() : new byte[bytes];
                    await raw.ReadExactlyAsync(buffer.AsMemory(0, bytes)).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (newest != null) free.Push(newest);   // never sent: dropped for a newer one
                        newest = buffer;
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException or InvalidOperationException) { }
            finally { lock (gate) ended = true; }
        });

        try
        {
            var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, fps));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var due = TimeSpan.Zero;
            byte[]? sending = null;
            while (true)
            {
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(1)) await Task.Delay(wait).ConfigureAwait(false);
                due = Max(due + interval, clock.Elapsed - interval);   // a late tick does not make a burst afterwards
                bool finished;
                lock (gate)
                {
                    finished = ended;
                    if (newest != null)
                    {
                        if (sending != null) free.Push(sending);
                        sending = newest;
                        newest = null;
                    }
                }
                if (sending != null)
                {
                    await encoder.WriteAsync(sending.AsMemory(0, bytes)).ConfigureAwait(false);
                    await encoder.FlushAsync().ConfigureAwait(false);
                }
                if (finished) break;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { done(); }
        await reader.ConfigureAwait(false);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // Stops GStreamer (a Wayland share); the capture in this program stops by itself when the run sees the switch or the end.
    private void KillProcess()
    {
        Process? gst;
        lock (_gate) { gst = _gstreamer; _gstreamer = null; }
        if (gst == null) return;
        try { if (!gst.HasExited) gst.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        gst.Dispose();
    }

    public void Dispose()
    {
        StopQualityWatch();
        _cts.Cancel();
        KillProcess();
        _sender?.Complete();
        try { _run?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _sender?.Wait(TimeSpan.FromSeconds(2));
        StopSound();
        _low?.Dispose();
        _options.Portal?.Dispose();
        _source.KeyFrameRequested -= _onKeyRequested;
        _source.Dispose();
        _cts.Dispose();
    }
}
