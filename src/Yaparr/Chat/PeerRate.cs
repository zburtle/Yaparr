namespace Yaparr.Chat;

/// <summary>
/// How much one connected peer may send us before the excess is dropped. Anyone who can reach us can open a
/// connection, and every member of a room can write to all the others, so no one peer gets to use unlimited time,
/// memory or bandwidth of the rest. A rate is what a peer may keep up; a burst is what it may send at once.
/// </summary>
public sealed class PeerLimits
{
    /// <summary>Chat frames of any kind.</summary>
    public int FramesBurst { get; init; } = 300;
    public double FramesPerSecond { get; init; } = 150;

    /// <summary>Bytes of chat data. The burst has to hold the largest message (a long history), so it is generous.</summary>
    public long BytesBurst { get; init; } = 8 * 1024 * 1024;
    public double BytesPerSecond { get; init; } = 1024 * 1024;

    /// <summary>Chat messages, direct and in rooms (messages that others send through this peer count too).</summary>
    public int MessagesBurst { get; init; } = 40;
    public double MessagesPerSecond { get; init; } = 10;

    /// <summary>Soundboard sounds being played.</summary>
    public int PlaysBurst { get; init; } = 20;
    public double PlaysPerSecond { get; init; } = 10;

    /// <summary>Soundboard lists, which make us download files.</summary>
    public int SoundListsBurst { get; init; } = 20;
    public double SoundListsPerSecond { get; init; } = 2;

    /// <summary>A peer that has this many frames dropped within <see cref="DropWindow"/> is disconnected.</summary>
    public int DropsBeforeClose { get; init; } = 500;
    public TimeSpan DropWindow { get; init; } = TimeSpan.FromSeconds(10);
}

internal sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _perMs;
    private readonly object _gate = new();
    private double _tokens;
    private long _last;

    public TokenBucket(double capacity, double perSecond)
    {
        _capacity = capacity;
        _perMs = perSecond / 1000;
        _tokens = capacity;
        _last = Environment.TickCount64;
    }

    public bool TryTake(double cost = 1, long? now = null)
    {
        lock (_gate)
        {
            long t = now ?? Environment.TickCount64;
            _tokens = Math.Min(_capacity, _tokens + Math.Max(0, t - _last) * _perMs);
            _last = t;
            // A request bigger than the bucket could never be allowed, so it takes the whole bucket instead.
            cost = Math.Min(cost, _capacity);
            if (_tokens < cost) return false;
            _tokens -= cost;
            return true;
        }
    }
}

/// <summary>The allowances of one peer.</summary>
internal sealed class PeerRate
{
    private readonly PeerLimits _limits;
    private readonly TokenBucket _frames, _bytes, _messages, _plays, _soundLists;
    private readonly object _gate = new();
    private long _windowStart;
    private int _dropped;

    public PeerRate(PeerLimits limits)
    {
        _limits = limits;
        _frames = new(limits.FramesBurst, limits.FramesPerSecond);
        _bytes = new(limits.BytesBurst, limits.BytesPerSecond);
        _messages = new(limits.MessagesBurst, limits.MessagesPerSecond);
        _plays = new(limits.PlaysBurst, limits.PlaysPerSecond);
        _soundLists = new(limits.SoundListsBurst, limits.SoundListsPerSecond);
    }

    public bool AdmitFrame(int bytes) => _frames.TryTake() && _bytes.TryTake(bytes);
    public bool AdmitMessages(int count) => _messages.TryTake(count);
    public bool AdmitPlay() => _plays.TryTake();
    public bool AdmitSoundList() => _soundLists.TryTake();

    /// <summary>Notes a dropped frame. Returns true once the peer has dropped so many in a short time that it should be disconnected.</summary>
    public bool RecordDrop(out int total)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (now - _windowStart > _limits.DropWindow.TotalMilliseconds) { _windowStart = now; _dropped = 0; }
            total = ++_dropped;
            return _dropped >= _limits.DropsBeforeClose;
        }
    }
}
