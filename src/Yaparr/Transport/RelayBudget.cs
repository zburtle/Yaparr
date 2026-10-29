using System.Collections.Concurrent;

namespace Yaparr.Transport;

/// <summary>What this node has done as a relay for other members.</summary>
public sealed record RelayStats(long ForwardedFrames, long ForwardedBytes, long DroppedFrames, int ActivePairs);

/// <summary>A bucket that refills at a steady rate and holds at most one second's worth.</summary>
internal sealed class TokenBucket
{
    private readonly double _perMs;
    private readonly double _capacity;
    private double _tokens;
    private long _at;
    private readonly object _gate = new();

    public TokenBucket(int bytesPerSecond, long nowMs)
    {
        _perMs = bytesPerSecond / 1000.0;
        _capacity = bytesPerSecond;
        _tokens = _capacity;
        _at = nowMs;
    }

    public bool TryTake(int bytes, long nowMs)
    {
        lock (_gate)
        {
            _tokens = Math.Min(_capacity, _tokens + Math.Max(0, nowMs - _at) * _perMs);
            _at = nowMs;
            if (_tokens < bytes) return false;
            _tokens -= bytes;
            return true;
        }
    }
}

/// <summary>
/// Limits what a node forwards for others, so that being a good relay never uses up its connection:
/// one cap for everything, a smaller one for each sender, and a limit on how many pairs it serves at once.
/// </summary>
internal sealed class RelayBudget
{
    private const long PairLifetimeMs = 60_000;

    private readonly int _total, _perSender, _maxPairs;
    private readonly TokenBucket _all;
    private readonly ConcurrentDictionary<string, TokenBucket> _senders = new();
    private readonly ConcurrentDictionary<(string, string), long> _pairs = new();
    private long _frames, _bytes, _dropped;

    public RelayBudget(int bytesPerSecond, int bytesPerSender, int maxPairs, long nowMs)
    {
        _total = bytesPerSecond;
        _perSender = bytesPerSender;
        _maxPairs = maxPairs;
        _all = new TokenBucket(bytesPerSecond, nowMs);
    }

    public RelayStats Stats(long nowMs)
    {
        Prune(nowMs);
        return new RelayStats(Interlocked.Read(ref _frames), Interlocked.Read(ref _bytes), Interlocked.Read(ref _dropped), _pairs.Count);
    }

    /// <summary>True if a frame of this size from one member to another may be forwarded now.</summary>
    public bool TryForward(string from, string to, int bytes, long nowMs)
    {
        var pair = string.CompareOrdinal(from, to) < 0 ? (from, to) : (to, from);
        if (!_pairs.ContainsKey(pair))
        {
            Prune(nowMs);
            if (_pairs.Count >= _maxPairs) { Interlocked.Increment(ref _dropped); return false; }
        }
        var sender = _senders.GetOrAdd(from, _ => new TokenBucket(_perSender, nowMs));
        if (!sender.TryTake(bytes, nowMs) || !_all.TryTake(bytes, nowMs))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        _pairs[pair] = nowMs;
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, bytes);
        return true;
    }

    private void Prune(long nowMs)
    {
        foreach (var kv in _pairs)
            if (nowMs - kv.Value > PairLifetimeMs) _pairs.TryRemove(kv.Key, out _);
    }
}
