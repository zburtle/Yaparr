using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>The stream of a share a viewer watches: the main one, or the share's lower stream.</summary>
public enum WatchedStream { Main, Low }

/// <summary>
/// Which of a share's two streams a viewer watches (issue #254). It is chosen for the viewer, never by it: the viewer moves to the lower stream when it has been
/// in trouble for <see cref="TroubleMs"/> (its own judgement, <see cref="ViewerQualityMeter"/>), and at once when it is told that it is the only one in trouble
/// (<see cref="LoneStrugglerWatch"/>: the cause is its own link). On the lower stream it tries the main one again after a quiet stretch, <see cref="FirstWaitMs"/>
/// at first and twice as long after each try that went back down within <see cref="FailedTryMs"/> (up to <see cref="LongestWaitMs"/>); a long enough stretch on
/// the main stream (<see cref="ForgetMs"/>) forgets the failed tries. Pure: the clock is passed in.
/// </summary>
public sealed class StreamChoice
{
    public const long TroubleMs = 10_000, FirstWaitMs = 60_000, LongestWaitMs = 900_000, FailedTryMs = 20_000, ForgetMs = 1_800_000;

    private long _troubledSince = -1, _quietSince = -1, _mainSince = -1, _triedMainAt = -1;
    private long _waitMs = FirstWaitMs;

    /// <summary>How long the lower stream waits, quiet, before the main one is tried again.</summary>
    public long WaitMs => _waitMs;

    /// <summary>Why the last move was made, as the viewer's notice says it ("your connection lost pictures for 10 s", and so on); null before any.</summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// The stream to watch now. <paramref name="lowAvailable"/> whether the share has a lower stream at the moment (the sharer starts it when a viewer is in
    /// trouble); <paramref name="current"/> is the one watched; <paramref name="sample"/> is the viewer's judgement of it (unknown for a few seconds after a move);
    /// <paramref name="toldAlone"/> whether it has been told that it is the only one in trouble.
    /// </summary>
    public WatchedStream Update(long nowMs, bool lowAvailable, WatchedStream current, ViewerQualitySample sample, bool toldAlone)
    {
        if (!lowAvailable) return Want(WatchedStream.Main, current, nowMs, current == WatchedStream.Low ? "the sharer stopped the lower-quality stream" : null);

        bool troubled = sample.Known && sample.Troubled;
        if (current == WatchedStream.Main)
        {
            _quietSince = -1;
            if (_mainSince < 0) _mainSince = nowMs;
            if (nowMs - _mainSince >= ForgetMs) _waitMs = FirstWaitMs;   // long enough on the main stream: the failed tries are forgotten
            if (!troubled) { _troubledSince = -1; return WatchedStream.Main; }
            if (_troubledSince < 0) _troubledSince = nowMs;
            if (!toldAlone && nowMs - _troubledSince < TroubleMs) return WatchedStream.Main;
            // Down. A try of the main stream that failed this quickly makes the next wait twice as long.
            if (_triedMainAt >= 0 && nowMs - _triedMainAt <= FailedTryMs + TroubleMs) _waitMs = Math.Min(LongestWaitMs, _waitMs * 2);
            _triedMainAt = -1;
            return Want(WatchedStream.Low, current, nowMs, toldAlone ? "your connection is the only one struggling with the full picture" : $"your connection lost pictures for {TroubleMs / 1000} seconds");
        }

        _troubledSince = -1;
        _mainSince = -1;
        if (troubled || !sample.Known) { if (troubled) _quietSince = -1; return WatchedStream.Low; }
        if (_quietSince < 0) _quietSince = nowMs;
        if (nowMs - _quietSince < _waitMs) return WatchedStream.Low;
        _triedMainAt = nowMs;
        return Want(WatchedStream.Main, current, nowMs, $"the lower-quality stream was fine for {_waitMs / 1000} seconds, so the full one is tried again");
    }

    private WatchedStream Want(WatchedStream wanted, WatchedStream current, long nowMs, string? reason)
    {
        if (wanted != current)
        {
            Reason = reason;
            _troubledSince = _quietSince = -1;
            _mainSince = wanted == WatchedStream.Main ? nowMs : -1;
        }
        return wanted;
    }
}
