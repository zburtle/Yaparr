namespace Yaparr.Screen;

/// <summary>
/// Decides when a key frame that a viewer asked for is really made. Requests come from the stream tree at most once a second per
/// node, but a sharer with many viewers can still get a steady trickle of them, and every forced key frame is a burst, so one is
/// forced only when none went out in the last <c>minGapMs</c>. A request that arrives too soon is kept, not dropped, and is answered
/// when the gap has passed. A key frame that goes out anyway (the regular one, or the one that answered) satisfies what is waiting.
/// It also counts what became of the requests and how long they waited, for the log.
/// </summary>
public sealed class KeyFramePacer
{
    private readonly long _minGapMs;
    private int _wanted;
    private long _lastKeyAt = long.MinValue / 2;
    private long _pendingSince;          // when the oldest unanswered request arrived (0: none)
    private bool _forcing, _heldBackCounted;
    private long _requested, _forced, _deferred, _satisfied;

    public KeyFramePacer(long minGapMs = 500) => _minGapMs = minGapMs;

    /// <summary>A viewer needs a key frame. Safe to call from any thread. <paramref name="nowMs"/> is a millisecond clock (the tick count when not given).</summary>
    public void Request(long nowMs = -1)
    {
        if (nowMs < 0) nowMs = Environment.TickCount64;
        Interlocked.Increment(ref _requested);
        Interlocked.CompareExchange(ref _pendingSince, Math.Max(1, nowMs), 0);
        Volatile.Write(ref _wanted, 1);
    }

    /// <summary>Whether the next picture should be made a key frame, at <paramref name="nowMs"/> (a millisecond clock).</summary>
    public bool ShouldForce(long nowMs)
    {
        if (Volatile.Read(ref _wanted) == 0) return false;
        if (nowMs - Volatile.Read(ref _lastKeyAt) < _minGapMs)
        {
            if (!_heldBackCounted) { _heldBackCounted = true; Interlocked.Increment(ref _deferred); }
            return false;
        }
        Volatile.Write(ref _wanted, 0);
        _heldBackCounted = false;
        _forcing = true;
        Interlocked.Increment(ref _forced);
        return true;
    }

    /// <summary>
    /// A key frame was sent (forced or not). Returns how long a viewer's request waited for it in milliseconds, or -1 when no request was waiting.
    /// A regular key frame that arrives while a request waits is the answer, and counts as such.
    /// </summary>
    public long KeySent(long nowMs)
    {
        Volatile.Write(ref _lastKeyAt, nowMs);
        bool waiting = Interlocked.Exchange(ref _wanted, 0) == 1;
        bool forced = _forcing;
        _forcing = false;
        _heldBackCounted = false;
        long since = Interlocked.Exchange(ref _pendingSince, 0);
        if (waiting && !forced) Interlocked.Increment(ref _satisfied);
        return (waiting || forced) && since > 0 ? Math.Max(0, nowMs - since) : -1;
    }

    /// <summary>How many times a viewer asked.</summary>
    public long Requested => Interlocked.Read(ref _requested);

    /// <summary>How many key frames were forced because a viewer asked.</summary>
    public long Forced => Interlocked.Read(ref _forced);

    /// <summary>How many requests came too soon after a key frame and had to wait.</summary>
    public long Deferred => Interlocked.Read(ref _deferred);

    /// <summary>How many requests were answered by a regular key frame that happened to come first.</summary>
    public long Satisfied => Interlocked.Read(ref _satisfied);
}
