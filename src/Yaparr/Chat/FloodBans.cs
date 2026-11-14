using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// A peer that was disconnected for flooding (it kept sending past its allowance, see <see cref="PeerLimits"/>) is refused for a while when it connects
/// again: it could reconnect at once and start over. The first ban is <see cref="PeerLimits.BanFirst"/>; a peer that floods again soon after a ban has
/// ended gets twice as long (up to <see cref="PeerLimits.BanLongest"/>), and one that stays away longer than the last ban's length starts again from
/// the first. It is the device that is banned, for as long as the program runs: it costs nothing to keep and nothing is written to disk. Pure: the clock is
/// passed in.
/// </summary>
internal sealed class FloodBans
{
    private readonly long _firstMs, _longestMs;
    private readonly Dictionary<PeerId, (long Until, long Length)> _bans = new();
    private readonly object _gate = new();

    public FloodBans(PeerLimits limits)
    {
        _firstMs = (long)limits.BanFirst.TotalMilliseconds;
        _longestMs = Math.Max(_firstMs, (long)limits.BanLongest.TotalMilliseconds);
    }

    /// <summary>Bans a peer from <paramref name="nowMs"/>; returns for how long.</summary>
    public TimeSpan Ban(PeerId peer, long nowMs)
    {
        lock (_gate)
        {
            long length = _firstMs;
            // Flooding again before a time as long as the last ban has passed since it ended: twice as long.
            if (_bans.TryGetValue(peer, out var last) && nowMs < last.Until + last.Length) length = Math.Min(_longestMs, last.Length * 2);
            _bans[peer] = (nowMs + length, length);
            return TimeSpan.FromMilliseconds(length);
        }
    }

    /// <summary>Whether the peer is banned now, and for how much longer.</summary>
    public bool IsBanned(PeerId peer, long nowMs, out TimeSpan remaining)
    {
        lock (_gate)
        {
            if (_bans.TryGetValue(peer, out var ban) && nowMs < ban.Until)
            {
                remaining = TimeSpan.FromMilliseconds(ban.Until - nowMs);
                return true;
            }
        }
        remaining = TimeSpan.Zero;
        return false;
    }

    /// <summary>Forgets bans that ended so long ago that the next one would start from the first length anyway.</summary>
    public void Forget(long nowMs)
    {
        lock (_gate)
            foreach (var peer in _bans.Where(kv => nowMs >= kv.Value.Until + kv.Value.Length).Select(kv => kv.Key).ToList()) _bans.Remove(peer);
    }

    /// <summary>How many peers are on the list (for tests and the diagnostics).</summary>
    public int Count { get { lock (_gate) return _bans.Count; } }
}
