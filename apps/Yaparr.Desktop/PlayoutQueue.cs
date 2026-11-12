using System.Collections.Concurrent;

namespace Yaparr.Desktop;

/// <summary>
/// How fast pictures arrive, on average, which is the pace they are drawn at. Over the internet pictures come unevenly (a few close together,
/// then a gap, and after a stall at the sender a whole burst), so the pace is the number of pictures that arrived in the last second divided by
/// the time they took, not an average of the gaps between them: a gap cannot be shorter than nothing, so every short gap that is lifted to some
/// minimum makes the average too long, and the more bunched the pictures come the slower the clock runs. At 60 pictures a second that is how
/// a viewer came to draw 49 of the 60 that arrived, and at 120 a second about half.
/// </summary>
public sealed class PlayoutClock
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private const int MinPictures = 8;
    private const double MinIntervalMs = 4, MaxIntervalMs = 200;
    private readonly Queue<long> _arrivals = new();
    private double _intervalMs = 33;

    /// <summary>The time between two pictures, in milliseconds. Starts at 33 (30 pictures a second) until enough have come to tell.</summary>
    public double IntervalMs { get { lock (_arrivals) return _intervalMs; } }

    /// <summary>A picture arrived at <paramref name="nowMs"/> (a millisecond clock).</summary>
    public void Arrived(long nowMs)
    {
        lock (_arrivals)
        {
            _arrivals.Enqueue(nowMs);
            while (_arrivals.Count > 2 && nowMs - _arrivals.Peek() > Window.TotalMilliseconds) _arrivals.Dequeue();
            // Too few pictures in the window (a slow stream, or one that has just begun or resumed) say nothing yet: the last pace stands.
            if (_arrivals.Count < MinPictures) return;
            long oldest = _arrivals.Peek();
            if (nowMs <= oldest) return;
            _intervalMs = Math.Clamp((nowMs - oldest) / (double)(_arrivals.Count - 1), MinIntervalMs, MaxIntervalMs);
        }
    }
}

/// <summary>
/// The short wait pictures have before they are drawn, so that uneven arrival does not look like stutter: they wait in a queue of a few and are
/// drawn on a steady clock (<see cref="PlayoutClock"/>). If more than a few are waiting the oldest are skipped, so the delay does not build up.
/// Pictures go in from the decoder's thread and come out on the one that draws.
/// </summary>
public sealed class PlayoutQueue<T> where T : class
{
    /// <summary>How many pictures wait while the clock is on time; one more than that and the clock is behind.</summary>
    public const int TargetQueue = 2, MaxQueue = 5;

    private readonly ConcurrentQueue<T> _queue = new();
    private readonly Action<T> _release;
    private readonly PlayoutClock _clock = new();
    private double _nextDue;
    private long _firstQueued, _late;
    private bool _playing;

    /// <param name="release">Takes back a picture that will not be shown (the viewer keeps a few to use again).</param>
    public PlayoutQueue(Action<T> release) => _release = release;

    public bool IsEmpty => _queue.IsEmpty;
    public int Count => _queue.Count;
    public double IntervalMs => _clock.IntervalMs;

    /// <summary>Pictures that were skipped because they came too bunched up to be shown in time.</summary>
    public long Late => Interlocked.Read(ref _late);

    /// <summary>A picture arrived at <paramref name="nowMs"/>.</summary>
    public void Arrive(T picture, long nowMs)
    {
        _clock.Arrived(nowMs);
        if (_queue.IsEmpty) Volatile.Write(ref _firstQueued, nowMs);
        _queue.Enqueue(picture);
        while (_queue.Count > MaxQueue && _queue.TryDequeue(out var old)) { Interlocked.Increment(ref _late); _release(old); }
    }

    /// <summary>The picture to draw at <paramref name="nowMs"/>, or false when none is due yet. Called often, from the thread that draws.</summary>
    public bool TryDraw(long nowMs, out T picture)
    {
        picture = default!;
        if (_queue.IsEmpty) { _playing = false; return false; }
        double interval = _clock.IntervalMs;
        if (!_playing)
        {
            // Start once a couple of pictures are waiting, or after a short wait if they come slowly.
            if (_queue.Count < TargetQueue && nowMs - Volatile.Read(ref _firstQueued) < 120) return false;
            _playing = true;
            _nextDue = nowMs;
        }
        if (nowMs < _nextDue) return false;
        if (!_queue.TryDequeue(out var next)) return false;
        picture = next;
        _nextDue = Math.Max(_nextDue + interval, nowMs - interval);   // a late draw does not make a burst afterwards
        // More waiting than the target means the clock is behind; show the newest of them instead of falling further back.
        while (_queue.Count > TargetQueue + 1 && _queue.TryDequeue(out var newer)) { Interlocked.Increment(ref _late); _release(picture); picture = newer; }
        return true;
    }

    /// <summary>Playing stops (the picture ended, or nothing is left); the next pictures start it afresh.</summary>
    public void Reset() => _playing = false;
}
