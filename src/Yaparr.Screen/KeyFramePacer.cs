namespace Yaparr.Screen;

/// <summary>
/// Decides when a key frame that a viewer asked for is really made. Requests come from the stream tree at most once a second per
/// node, but a sharer with many viewers can still get a steady trickle of them, and every forced key frame is a burst, so one is
/// forced only when none went out in the last <c>minGapMs</c>. A request that arrives too soon is kept, not dropped, and is answered
/// when the gap has passed. A key frame that goes out anyway (the regular one, or the one that answered) satisfies what is waiting.
/// </summary>
public sealed class KeyFramePacer
{
    private readonly long _minGapMs;
    private int _wanted;
    private long _lastKeyAt = long.MinValue / 2;
    private long _forced;

    public KeyFramePacer(long minGapMs = 500) => _minGapMs = minGapMs;

    /// <summary>A viewer needs a key frame. Safe to call from any thread.</summary>
    public void Request() => Volatile.Write(ref _wanted, 1);

    /// <summary>Whether the next picture should be made a key frame, at <paramref name="nowMs"/> (a millisecond clock).</summary>
    public bool ShouldForce(long nowMs)
    {
        if (Volatile.Read(ref _wanted) == 0) return false;
        if (nowMs - Volatile.Read(ref _lastKeyAt) < _minGapMs) return false;
        Volatile.Write(ref _wanted, 0);
        Interlocked.Increment(ref _forced);
        return true;
    }

    /// <summary>A key frame was sent (forced or not).</summary>
    public void KeySent(long nowMs)
    {
        Volatile.Write(ref _lastKeyAt, nowMs);
        Volatile.Write(ref _wanted, 0);
    }

    /// <summary>How many key frames were forced because a viewer asked.</summary>
    public long Forced => Interlocked.Read(ref _forced);
}
