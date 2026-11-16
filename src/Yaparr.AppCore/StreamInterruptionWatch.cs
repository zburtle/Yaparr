namespace Yaparr.AppCore;

/// <summary>What <see cref="StreamInterruptionWatch"/> says changed.</summary>
public enum InterruptionChange
{
    /// <summary>Nothing: the picture is coming, or it was already known to have stopped.</summary>
    None,
    /// <summary>The picture has not moved for too long: tell the person the stream was interrupted.</summary>
    Interrupted,
    /// <summary>A picture came after an interruption: take the notice away.</summary>
    Resumed,
    /// <summary>The notice is not needed any more for another reason (the connection was lost and has its own notice, or the stream ended).</summary>
    Cleared,
}

/// <summary>
/// Notices when the picture of a watched screen has stopped for a while although the watch is still going: the sharer restarted, a relay hiccuped, or so many
/// packets were lost that frames cannot be put together. The sharer sends pictures steadily (a screen that does not change is repeated), so a picture that
/// does not come for a couple of seconds is an interruption, not a still screen. Pure: the clock is passed in.
/// </summary>
public sealed class StreamInterruptionWatch
{
    /// <summary>How long without a new picture is an interruption.</summary>
    public const long StallMs = 2500;

    private readonly long _stallMs;
    private long _lastPictureAt, _interruptedSince;
    private bool _anyPicture, _interrupted;

    public StreamInterruptionWatch(long stallMs = StallMs) => _stallMs = stallMs;

    /// <summary>Whether the stream is known to be interrupted now.</summary>
    public bool IsInterrupted => _interrupted;

    /// <summary>A picture was drawn. Says <see cref="InterruptionChange.Resumed"/>, with how long it was stopped, when that ends an interruption.</summary>
    public (InterruptionChange Change, double Seconds) PictureShown(long nowMs)
    {
        _lastPictureAt = nowMs;
        _anyPicture = true;
        if (!_interrupted) return (InterruptionChange.None, 0);
        _interrupted = false;
        return (InterruptionChange.Resumed, (nowMs - _interruptedSince) / 1000.0);
    }

    /// <summary>
    /// Called about once a second. <paramref name="suppressed"/> is true while something else says what is wrong (the connection to the sharer is lost: that has its
    /// own notice) or nothing should be said (the stream ended). Nothing is said before the first picture: that is "waiting for the first picture".
    /// </summary>
    public (InterruptionChange Change, double Seconds) Tick(long nowMs, bool suppressed)
    {
        if (suppressed)
        {
            if (!_interrupted) return (InterruptionChange.None, 0);
            _interrupted = false;
            return (InterruptionChange.Cleared, 0);
        }
        if (_interrupted || !_anyPicture || nowMs - _lastPictureAt < _stallMs) return (InterruptionChange.None, 0);
        _interrupted = true;
        _interruptedSince = _lastPictureAt;
        return (InterruptionChange.Interrupted, (nowMs - _lastPictureAt) / 1000.0);
    }
}
