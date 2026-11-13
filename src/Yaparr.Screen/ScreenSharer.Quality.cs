using Yaparr.Chat;

namespace Yaparr.Screen;

// Stage 3 of #225, in shadow mode: every second the room's reports and the sharer's own link go through QualityPolicy, and what it would do is written
// down and nothing else happens (docs/adaptive-quality.md). The first release that has it is for finding out how often it would act.
public sealed partial class ScreenSharer
{
    /// <summary>How many steps of the ladder are acted on so far: the bitrate ones (1 and 2). The others are only written down.</summary>
    internal const int ActedSteps = 2;

    private readonly QualityPolicy _policy;
    // The encoder of this program is running (and takes a new bitrate between pictures); the request waits here for the thread that encodes.
    private volatile bool _encoderLive;
    private int _pendingKbps;
    private volatile int _appliedStep;
    private readonly Queue<(long At, long Bytes, long Resent, long Dropped)> _linkWindow = new();
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
    internal int PendingKbpsForTest => Volatile.Read(ref _pendingKbps);
    internal void SetEncoderLiveForTest(bool live) => _encoderLive = live;

    /// <summary>Whether the policy's decisions are carried out: the setting is on and the encoder is in this program (else they are only written down).</summary>
    public bool ActingOnQuality => _options.AdaptQuality && _options.InProcessEncoder && _encoderLive;

    /// <summary>The step the share is at now, as applied to the encoder (0 is the quality that was asked for).</summary>
    public int AppliedStep => _appliedStep;

    /// <summary>What the share is doing about its viewers, in words, or null while it is at the quality asked for: "Quality lowered to 2.7 Mbit/s: 3 of 4 viewers lost frames for 10 s".</summary>
    public string? AdaptiveLine => _appliedStep == 0 ? null : $"Quality lowered to {Describe(LevelOf(_appliedStep).Kbps)}: {_appliedWhy}";
    private volatile string _appliedWhy = "";

    private static string Describe(int kbps) => kbps >= 1000 ? $"{kbps / 1000.0:0.#} Mbit/s" : $"{kbps} kbit/s";

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
    private QualityLevel LevelOf(int step)
    {
        var (width, height) = _picture ?? (_options.MaxWidth, _options.MaxHeight);
        return QualityLadder.For(step, _options.Kbps, _options.Fps, width, height);
    }

    internal void QualityTick()
    {
        lock (_policy)
        try
        {
            long now = Environment.TickCount64;
            var status = _source.Status;
            var room = _source.Room;
            // The sharer's own link over the last ten seconds: the share of what it sent that it sent again (packets, counted from the bytes), and what it dropped.
            _linkWindow.Enqueue((now, Interlocked.Read(ref _bytes), status.Retransmitted, status.Dropped));
            while (_linkWindow.Count > 1 && now - _linkWindow.Peek().At > 10_000) _linkWindow.Dequeue();
            var first = _linkWindow.Peek();
            double packets = Math.Max(1, (Interlocked.Read(ref _bytes) - first.Bytes) / (double)Math.Max(200, _source.MaxPayload));
            var own = new OwnLink((status.Retransmitted - first.Resent) / packets, status.Dropped - first.Dropped, status.Children.Count);
            (_room, _ownLink) = (room, own);

            var decision = _policy.Tick(now, new RoomQuality(room.Viewers, room.Troubled, room.WorstFps, room.Lossy), own, _source.Viewers.Count);
            string level = LevelOf(_policy.Step).Describe();
            bool acting = ActingOnQuality;
            _qualityNote = $"{(acting ? "acting on the first two steps (the bitrate)" : "shadow mode, nothing is changed")}: {(room.Viewers == 0 ? "no viewer has reported" : $"{room.Viewers} viewers reported, {room.Troubled} in trouble, worst {room.WorstFps:0} fps")}; "
                           + (_policy.Step == 0 ? $"{(acting ? "at" : "would be at")} the quality asked for" : $"{(acting ? "at" : "would be at")} step {_policy.Step} of {QualityLadder.Steps - 1}: {level}");
            if (decision.Changed)
            {
                Interlocked.Increment(ref _shadowChanges);
                bool lowering = decision.Step > _lastShadowStep;
                if (acting && decision.Step <= ActedSteps)
                {
                    int kbps = LevelOf(decision.Step).Kbps;
                    Volatile.Write(ref _pendingKbps, kbps);
                    _source.EffectiveKbps = kbps;
                    _appliedStep = decision.Step;
                    _appliedWhy = decision.Why ?? "";
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
