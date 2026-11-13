using Yaparr.Chat;

namespace Yaparr.Screen;

// Stage 3 of #225, in shadow mode: every second the room's reports and the sharer's own link go through QualityPolicy, and what it would do is written
// down and nothing else happens (docs/adaptive-quality.md). The first release that has it is for finding out how often it would act.
public sealed partial class ScreenSharer
{
    /// <summary>How many steps of the ladder there are below the quality asked for; all of them are acted on when the share can (see <see cref="ActingOnQuality"/>).</summary>
    internal const int ActedSteps = QualityLadder.Steps - 1;

    private readonly QualityPolicy _policy;
    // The encoder of this program is running (and takes a new bitrate between pictures); the request waits here for the thread that encodes.
    private volatile bool _encoderLive;
    private int _pendingKbps;
    private volatile int _appliedStep;
    // What the capture and the encoder of this run were built for (the frame rate and the box of the picture), and whether the next level needs another run.
    private sealed record RunShape(int Fps, int Width, int Height);
    private volatile RunShape? _shape;
    private volatile bool _levelRestart;
    private bool _restartableForTest;

    /// <summary>The frame rate this share runs at now: the one asked for, or the one of the level it was lowered to.</summary>
    public int RunFps => _shape?.Fps ?? _options.Fps;

    // The options for a run of the capture: the ones asked for, or at a lower level of adaptive quality those of the level.
    internal ScreenOptions RunOptions()
    {
        int step = _appliedStep;
        if (step == 0) return _options;
        var l = LevelOf(step);
        return _options.WithQuality(l.Kbps, l.Fps, l.Width, l.Height);
    }
    private readonly Queue<(long At, long Bytes, long Resent, long Dropped, long Frames, long Encodes, long OverBudget)> _linkWindow = new();
    private long _runStartedMs;
    private bool _senderWasBehind;

    /// <summary>The share of the pictures a second this computer was set to send that it sent over the last ten seconds, or 1 when that is not known (a run that began a moment ago).</summary>
    public double SenderRate { get; private set; } = 1;
    private Timer? _qualityTimer;
    private MediaQuality _room;
    private OwnLink _ownLink;
    private long _shadowChanges;
    private volatile string? _qualityNote;

    /// <summary>
    /// Raised when the policy would change the quality (in words, with the reason): "adaptive quality (shadow mode, nothing is changed): would lower to
    /// 720p60 at 2.7 Mbit/s (step 5 of 5): 3 of 4 viewers lost frames for 10 s". On another thread, so a handler has to be quick.
    /// </summary>
    public event Action<string>? QualityDecided;

    /// <summary>The step the policy is at: 0 is the quality that was asked for. In shadow mode only a figure; nothing is changed by it.</summary>
    public int ShadowStep => _policy.Step;

    /// <summary>How many times the policy has said it would change the quality.</summary>
    public long ShadowChanges => Interlocked.Read(ref _shadowChanges);

    /// <summary>What the viewers said last (see <see cref="MediaSource.Room"/>) and the sharer's own link over the last ten seconds.</summary>
    public (MediaQuality Room, OwnLink Own) QualityInputs => (_room, _ownLink);

    /// <summary>The state of it in one line, for the report and the log: the room, and where the policy is.</summary>
    public string? QualityNote => _qualityNote;

    internal void StartQualityWatchForTest() { _encoderLive = true; StartQualityWatch(); }
    internal bool RestartPendingForTest => _levelRestart;
    internal void SetRestartableForTest(bool restartable) { _restartableForTest = restartable; _shape = new RunShape(_options.Fps, _options.MaxWidth, _options.MaxHeight); }
    internal void SetAppliedStepForTest(int step) => _appliedStep = step;
    internal int PendingKbpsForTest => Volatile.Read(ref _pendingKbps);
    internal void SetEncoderLiveForTest(bool live) => _encoderLive = live;
    internal void SetRunStartedForTest(long tickMs) => Volatile.Write(ref _runStartedMs, tickMs);
    internal void AddFramesForTest(long frames) => Interlocked.Add(ref _frames, frames);

    /// <summary>Whether the policy's decisions are carried out: the setting is on and the encoder is in this program (else they are only written down).</summary>
    public bool ActingOnQuality => _options.AdaptQuality && _options.InProcessEncoder && _encoderLive;

    /// <summary>The step the share is at now, as applied to the encoder (0 is the quality that was asked for).</summary>
    public int AppliedStep => _appliedStep;

    /// <summary>What the share is doing about its viewers, in words, or null while it is at the quality asked for: "Quality lowered to 2.7 Mbit/s: 3 of 4 viewers lost frames for 10 s".</summary>
    public string? AdaptiveLine => _appliedStep == 0 ? null : $"Quality lowered to {LevelOf(_appliedStep).Describe()}: {_appliedWhy}";
    private volatile string _appliedWhy = "";

    // Puts a level into effect. A level with another frame rate or size needs the capture and the encoder built again (a second or so without pictures, then a
    // key frame); one with the same only needs the encoder told the bitrate. Where the capture is not here, only the bitrate can follow.
    private void ApplyStep(int step, string why)
    {
        var next = LevelOf(step);
        bool restart = (_captureHere != null || _restartableForTest) && _shape is { } shape && (shape.Fps != next.Fps || shape.Width != next.Width || shape.Height != next.Height);
        _appliedStep = step;
        _appliedWhy = why;
        _source.EffectiveKbps = next.Kbps;
        if (restart) { Volatile.Write(ref _pendingKbps, 0); _levelRestart = true; }
        else Volatile.Write(ref _pendingKbps, next.Kbps);
    }

    internal void ApplyStepForTest(int step, string why) => ApplyStep(step, why);

    // Called on the thread that encodes, between two pictures.
    private void ApplyPendingBitrate(InProcessEncoder encoder)
    {
        int kbps = Interlocked.Exchange(ref _pendingKbps, 0);
        if (kbps <= 0 || kbps == encoder.Kbps) return;
        try { encoder.SetBitrate(kbps); }
        catch (Exception) { }   // an encoder that refuses keeps what it had
    }

    private void StartQualityWatch() => _qualityTimer = new Timer(_ => QualityTick(), null, 1000, 1000);

    private void StopQualityWatch()
    {
        var timer = _qualityTimer;
        _qualityTimer = null;
        timer?.Dispose();
    }

    // The level a step stands for, for the picture this share has (before the first picture, the largest the options allow).
    private QualityLevel LevelOf(int step) => QualityLadder.For(step, _options.Kbps, _options.Fps, _options.MaxWidth, _options.MaxHeight);

    internal void QualityTick()
    {
        lock (_policy)
        try
        {
            long now = Environment.TickCount64;
            var status = _source.Status;
            var room = _source.Room;
            // The sharer's own link over the last ten seconds: the share of what it sent that it sent again (packets, counted from the bytes), and what it dropped.
            var shareFigures = Figures();
            _linkWindow.Enqueue((now, Interlocked.Read(ref _bytes), status.Retransmitted, status.Dropped, Interlocked.Read(ref _frames), shareFigures.Encodes, shareFigures.OverBudget));
            while (_linkWindow.Count > 1 && now - _linkWindow.Peek().At > 10_000) _linkWindow.Dequeue();
            var first = _linkWindow.Peek();
            double packets = Math.Max(1, (Interlocked.Read(ref _bytes) - first.Bytes) / (double)Math.Max(200, _source.MaxPayload));
            // The sender's own rate over the window, once the run has had ten seconds to settle: below nine in ten of what it was set to, with the encoder keeping up
            // (fewer than one picture in fifty over its time), the capture or the clock is late, and no quality step will change that.
            double windowSeconds = (now - first.At) / 1000.0;
            bool settled = Volatile.Read(ref _runStartedMs) is > 0 and var startedAt && first.At - startedAt >= 0 && windowSeconds >= 8;
            double rate = settled ? (Interlocked.Read(ref _frames) - first.Frames) / windowSeconds / Math.Max(1, RunFps) : 1;
            long encodedInWindow = shareFigures.Encodes - first.Encodes, overInWindow = shareFigures.OverBudget - first.OverBudget;
            bool behind = settled && rate < 0.9 && overInWindow * 50 <= Math.Max(1, encodedInWindow);
            SenderRate = rate;
            var own = new OwnLink((status.Retransmitted - first.Resent) / packets, status.Dropped - first.Dropped, status.Children.Count, behind);
            (_room, _ownLink) = (room, own);

            var decision = _policy.Tick(now, new RoomQuality(room.Viewers, room.Troubled, room.WorstFps, room.Lossy), own, _source.Viewers.Count);
            string level = LevelOf(_policy.Step).Describe();
            bool acting = ActingOnQuality;
            if (decision.SenderBehind != _senderWasBehind)
            {
                _senderWasBehind = decision.SenderBehind;
                QualityDecided?.Invoke(decision.SenderBehind
                    ? $"adaptive quality: this computer is sending {SenderRate * RunFps:0} of {RunFps} pictures a second although the encoder keeps up, so what the viewers lose is not for the quality to fix; nothing is changed"
                    : $"adaptive quality: this computer is sending its pictures a second again ({SenderRate * RunFps:0} of {RunFps})");
            }
            _qualityNote = (decision.SenderBehind ? $"this computer is sending only {SenderRate * RunFps:0} of {RunFps} pictures a second, so the quality is held; " : "")
                           + $"{(acting ? "acting on the presets" : "shadow mode, nothing is changed")}: {(room.Viewers == 0 ? "no viewer has reported" : $"{room.Viewers} viewers reported, {room.Troubled} in trouble, worst {room.WorstFps:0} fps")}; "
                           + (_policy.Step == 0 ? $"{(acting ? "at" : "would be at")} the quality asked for" : $"{(acting ? "at" : "would be at")} step {_policy.Step} of {QualityLadder.Steps - 1}: {level}");
            if (decision.Changed)
            {
                Interlocked.Increment(ref _shadowChanges);
                bool lowering = decision.Step > _lastShadowStep;
                if (acting && decision.Step <= ActedSteps)
                {
                    ApplyStep(decision.Step, decision.Why ?? "");
                    string done = decision.Step == 0 ? "back to the quality asked for" : $"{(lowering ? "lowered" : "raised")} to {level} (step {decision.Step} of {QualityLadder.Steps - 1})";
                    QualityDecided?.Invoke($"adaptive quality: {done}: {decision.Why}");
                }
                else
                {
                    string what = decision.Step == 0 ? "go back to the quality asked for" : $"{(lowering ? "lower" : "raise")} to {level} (step {decision.Step} of {QualityLadder.Steps - 1})";
                    QualityDecided?.Invoke($"adaptive quality ({(acting ? "this step is not acted on yet" : "shadow mode")}, nothing is changed): would {what}: {decision.Why}");
                }
            }
            else if (decision.AtFloor)
                QualityDecided?.Invoke($"adaptive quality (shadow mode, nothing is changed): {decision.Why}");
            _lastShadowStep = decision.Step;
        }
        catch (Exception) { }   // a figure that cannot be worked out this second is left for the next
    }

    private int _lastShadowStep;
}
