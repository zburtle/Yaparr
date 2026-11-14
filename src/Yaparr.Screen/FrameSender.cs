using System.Diagnostics;
using System.Threading.Channels;

namespace Yaparr.Screen;

/// <summary>
/// Sends the frames of one stream on a thread of its own. A frame's packets are spread out at a multiple of the bitrate (MediaSource.SendFrameAsync), so a
/// big frame takes a while to go out: at 2.8 Mbit/s, three times that is about a megabyte a second, and a 180 KB frame takes 170 ms. When the encoding loop
/// waited for that, the share's clock missed its ticks and the share sent 17 of 30 pictures a second while the encoder kept up (measured, 1.77.0). So the loop
/// hands each frame over (<see cref="Enqueue"/>) and goes on, and this empties the queue frame after frame at the paced rate: over a second the stream is far
/// below the pace, so a big frame only delays the few behind it. If sending ever falls more than <see cref="MaxBacklog"/> behind (a link far slower than
/// measured), what is queued is thrown away, a key frame is asked for, and frames are dropped until it comes: a viewer then waits a moment for the key frame
/// instead of getting the picture later and later.
/// </summary>
public sealed class FrameSender
{
    private readonly record struct Outgoing(byte[] Frame, bool IsKey, long EnqueuedAt);

    private readonly Func<byte[], bool, CancellationToken, Task> _deliver;
    private readonly ShareStats _stats;
    private readonly Action _askForKey;
    private readonly Func<long> _frameTicks;
    private readonly Channel<Outgoing> _outbox = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private long _oldestQueuedAt;   // the time stamp of the frame at the head of the queue (0 when it is empty)
    private int _queued;
    private volatile bool _skipToKey;
    private Task? _sending;

    /// <summary>How far behind sending may fall before what is queued is thrown away and a key frame is asked for.</summary>
    public static readonly TimeSpan MaxBacklog = TimeSpan.FromSeconds(1);

    /// <param name="deliver">Sends one frame (and keeps the stream's own figures); awaited, one at a time, in order.</param>
    /// <param name="stats">Where how long frames took to go out, and how long they waited, is counted.</param>
    /// <param name="askForKey">Asks the encoder for a key frame (when the queue was thrown away).</param>
    /// <param name="frameTicks">The time between two pictures now, in ticks: a wait longer than that is counted as holding the stream back.</param>
    public FrameSender(Func<byte[], bool, CancellationToken, Task> deliver, ShareStats stats, Action askForKey, Func<long> frameTicks)
    {
        _deliver = deliver; _stats = stats; _askForKey = askForKey; _frameTicks = frameTicks;
    }

    /// <summary>Frames waiting to be sent.</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>Starts the thread that sends (once).</summary>
    public void Start(CancellationToken ct) => _sending ??= Task.Run(() => SendLoopAsync(ct));

    /// <summary>No more frames: the thread ends once the queue is empty (or cancelled).</summary>
    public void Complete() => _outbox.Writer.TryComplete();

    /// <summary>Waits a little for the thread to end.</summary>
    public void Wait(TimeSpan timeout)
    {
        try { _sending?.Wait(timeout); } catch (AggregateException) { }
    }

    /// <summary>The encoding loop's side: never waits.</summary>
    public void Enqueue(byte[] frame, bool isKey)
    {
        long now = Stopwatch.GetTimestamp();
        long oldest = Interlocked.Read(ref _oldestQueuedAt);
        if (oldest != 0 && now - oldest > MaxBacklog.TotalSeconds * Stopwatch.Frequency)
        {
            int dropped = 0;
            while (_outbox.Reader.TryRead(out _)) { dropped++; Interlocked.Decrement(ref _queued); }
            Interlocked.Exchange(ref _oldestQueuedAt, 0);
            if (dropped > 0) _stats.DroppedFromQueue(dropped);
            _skipToKey = true;
            _askForKey();
        }
        if (_skipToKey)
        {
            if (!isKey) { _stats.DroppedFromQueue(1); return; }
            _skipToKey = false;
        }
        Interlocked.CompareExchange(ref _oldestQueuedAt, now, 0);
        Interlocked.Increment(ref _queued);
        _outbox.Writer.TryWrite(new Outgoing(frame, isKey, now));
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out var next))
                {
                    int left = Interlocked.Decrement(ref _queued);
                    // The next one in line, if any, is now the oldest that waits.
                    Interlocked.Exchange(ref _oldestQueuedAt, left > 0 && _outbox.Reader.TryPeek(out var after) ? after.EnqueuedAt : 0);
                    long start = Stopwatch.GetTimestamp();
                    try { await _deliver(next.Frame, next.IsKey, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException) { continue; }
                    long end = Stopwatch.GetTimestamp();
                    _stats.Sent(ToTicks(end - start), ToTicks(start - next.EnqueuedAt), _frameTicks());
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static long ToTicks(long stopwatchTicks) => (long)(stopwatchTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
}
