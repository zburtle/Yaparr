namespace Yaparr.Screen;

/// <summary>
/// Decides when a share of a window has to be built again because the window changed size (issue #102). Graphics Capture makes the picture of a window at the size
/// it had when the capture began, and fits a window that later changes shape into that size with bars; so when the window has a new size, the capture and the
/// encoder are built again for it (viewers pick the new size up at the next key frame). To keep a window that is dragged larger and smaller from restarting the
/// share all the time, the new size has to hold for <see cref="SettleMs"/> first, and restarts are at least <see cref="MinGapMs"/> apart. A window that is
/// minimised has no size to follow (<c>null</c>): nothing happens until it is back, and only if it comes back at another size. Pure: the clock is passed in.
/// </summary>
public sealed class WindowSizeWatch
{
    /// <summary>How long a new size has to stay the same before it is followed.</summary>
    public const long SettleMs = 500;

    /// <summary>The least time between two restarts (and before the first one, from when the share's capture was built).</summary>
    public const long MinGapMs = 3000;

    private (int Width, int Height)? _builtFor, _seen;
    private long _seenSince, _lastRestart;

    /// <summary><paramref name="builtFor"/> is the window's size the capture was built for, or null when it was not known (a minimised window).</summary>
    public WindowSizeWatch((int Width, int Height)? builtFor, long nowMs)
    {
        _builtFor = builtFor;
        _lastRestart = nowMs;
    }

    /// <summary>
    /// Call now and then with the window's size as it is (null when it is minimised or cannot be asked). Returns true when the capture should be built again,
    /// for <paramref name="followed"/>; the watch then counts that size as the one built for.
    /// </summary>
    public bool Update((int Width, int Height)? size, long nowMs, out (int Width, int Height) followed)
    {
        followed = default;
        if (size is not { } now) { _seen = null; return false; }
        if (_builtFor == now) { _seen = null; return false; }
        if (_seen != now) { _seen = now; _seenSince = nowMs; return false; }
        if (nowMs - _seenSince < SettleMs || nowMs - _lastRestart < MinGapMs) return false;
        _builtFor = now;
        _seen = null;
        _lastRestart = nowMs;
        followed = now;
        return true;
    }
}
