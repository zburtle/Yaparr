namespace Yaparr.Chat;

/// <summary>
/// What the viewers below one point of a stream's tree say about how it is going, folded together: how many viewers the figure covers, how many of them
/// are in trouble, the worst picture rate among them (pictures drawn a second, 0 when not known) and how many lose packets that asking again does not get back.
/// Counts only, no names, so a relay passes up one small message however many viewers sit below it.
/// </summary>
public readonly record struct MediaQuality(int Viewers, int Troubled, double WorstFps = 0, int Lossy = 0)
{
    public static MediaQuality None => default;

    /// <summary>The two together: the counts add, the worst rate is the lower of the two that are known.</summary>
    public MediaQuality Plus(MediaQuality other) => new(Viewers + other.Viewers, Troubled + other.Troubled,
        WorstFps == 0 ? other.WorstFps : other.WorstFps == 0 ? WorstFps : Math.Min(WorstFps, other.WorstFps), Lossy + other.Lossy);
}

/// <summary>
/// What the root of a stream says about the room, passed down the tree to every viewer (see <see cref="MediaView.RoomState"/>): how many viewers reported, how many
/// of them are in trouble, and whether the sharer's own link is fine (it had no trouble sending). Counts only, a few bytes, every five seconds.
/// </summary>
public readonly record struct RoomState(int Reported, int Troubled, bool OwnLinkFine);

/// <summary>
/// When a viewer may be told that it is the only one in trouble: it has had trouble itself, and the room's state says exactly one viewer is in trouble (so it is
/// this one, whose report is in the count), at least two others reported and are fine, and the sharer's own link had no trouble, all of that for fifteen seconds
/// in a row. Said only when it is certain: a small room, a room whose state has not come, or a sharer with trouble of its own say nothing. Pure: the clock is passed in.
/// </summary>
public sealed class LoneStrugglerWatch
{
    /// <summary>How long it has to hold before it is said.</summary>
    public const long ConfirmMs = 15_000;

    private long _since = -1;

    /// <summary>Whether to say it now. <paramref name="room"/> is null when no state has come (or it is old).</summary>
    public bool Update(long nowMs, ViewerQualitySample mine, RoomState? room)
    {
        bool certain = mine.Known && mine.Troubled && room is { Reported: >= 3, Troubled: 1, OwnLinkFine: true };
        if (!certain) { _since = -1; return false; }
        if (_since < 0) _since = nowMs;
        return nowMs - _since >= ConfirmMs;
    }
}

/// <summary>How one viewer judges its own picture over the last seconds (see <see cref="ViewerQualityMeter"/>); <see cref="Known"/> is false while there is nothing to judge by.</summary>
public readonly record struct ViewerQualitySample(bool Known, bool Troubled, double Fps, bool Lossy)
{
    public static ViewerQualitySample Unknown => default;
    public MediaQuality AsQuality => Known ? new MediaQuality(1, Troubled ? 1 : 0, Fps, Lossy ? 1 : 0) : MediaQuality.None;
}

/// <summary>The running totals of what a viewer has seen since it began (they only go up), which <see cref="ViewerQualityMeter"/> reads once a second.</summary>
public readonly record struct ViewerCounters(long Shown, long Lost, long Skipped, long Late, long DecoderDropped, long GaveUp);

/// <summary>
/// Judges a viewer's own picture over a window of about ten seconds, from the running totals fed to it once a second. In trouble when fewer than nine
/// in ten of the pictures that were sent were shown (lost, skipped waiting for a key frame, or too late to show count as not shown), or the decoder dropped
/// frames. It says nothing (<see cref="ViewerQualitySample.Known"/> false) until it has five seconds of pictures to go by.
/// </summary>
public sealed class ViewerQualityMeter
{
    private readonly int _windowSeconds;
    private readonly Queue<(long At, ViewerCounters Counters)> _samples = new();
    private ViewerQualitySample _sample;

    public ViewerQualityMeter(int windowSeconds = 10) => _windowSeconds = windowSeconds;

    /// <summary>The share of the pictures sent below which a viewer is in trouble.</summary>
    public const double ShownLimit = 0.90;

    public ViewerQualitySample Sample { get { lock (_samples) return _sample; } }

    /// <summary>Feed the totals; call about once a second.</summary>
    public void Update(long nowMs, ViewerCounters totals)
    {
        lock (_samples)
        {
            _samples.Enqueue((nowMs, totals));
            while (_samples.Count > 1 && nowMs - _samples.Peek().At > _windowSeconds * 1000L) _samples.Dequeue();
            var first = _samples.Peek();
            double seconds = (nowMs - first.At) / 1000.0;
            if (seconds < 5) { _sample = ViewerQualitySample.Unknown; return; }
            long shown = totals.Shown - first.Counters.Shown;
            long notShown = (totals.Lost - first.Counters.Lost) + (totals.Skipped - first.Counters.Skipped) + (totals.Late - first.Counters.Late);
            long sent = shown + notShown;
            if (sent <= 0) { _sample = ViewerQualitySample.Unknown; return; }   // nothing came, so there is nothing to judge: no information
            bool troubled = shown < sent * ShownLimit || totals.DecoderDropped - first.Counters.DecoderDropped >= 5;
            bool lossy = totals.GaveUp - first.Counters.GaveUp >= 10;
            _sample = new ViewerQualitySample(true, troubled, shown / seconds, lossy);
        }
    }
}
