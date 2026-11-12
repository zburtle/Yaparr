using Yaparr.Chat;

namespace Yaparr.Screen;

// Stage 3 of #225, in shadow mode: every second the room's reports and the sharer's own link go through QualityPolicy, and what it would do is written
// down and nothing else happens (docs/adaptive-quality.md). The first release that has it is for finding out how often it would act.
public sealed partial class ScreenSharer
{
    private readonly QualityPolicy _policy = new();
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

    internal void StartQualityWatchForTest() => StartQualityWatch();

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
            _qualityNote = $"shadow mode, nothing is changed: {(room.Viewers == 0 ? "no viewer has reported" : $"{room.Viewers} viewers reported, {room.Troubled} in trouble, worst {room.WorstFps:0} fps")}; "
                           + (_policy.Step == 0 ? "would be at the quality asked for" : $"would be at step {_policy.Step} of {QualityLadder.Steps - 1}: {level}");
            if (decision.Changed)
            {
                Interlocked.Increment(ref _shadowChanges);
                string what = decision.Step == 0 ? "go back to the quality asked for" : $"{(decision.Step > _lastShadowStep ? "lower" : "raise")} to {level} (step {decision.Step} of {QualityLadder.Steps - 1})";
                QualityDecided?.Invoke($"adaptive quality (shadow mode, nothing is changed): would {what}: {decision.Why}");
            }
            else if (decision.AtFloor)
                QualityDecided?.Invoke($"adaptive quality (shadow mode, nothing is changed): {decision.Why}");
            _lastShadowStep = decision.Step;
        }
        catch (Exception) { }   // a figure that cannot be worked out this second is left for the next
    }

    private int _lastShadowStep;
}
