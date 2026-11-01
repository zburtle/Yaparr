namespace Yaparr.Chat;

/// <summary>
/// Decides when the app should tell its user "the connection was lost, trying again" and "it is back". It is fed how many of the
/// people it should be connected to are reachable. It counts as lost when that was at least <c>minPeers</c> a moment ago (or one, and the
/// network changed: Wi-Fi to cable, waking from sleep, a VPN toggled) and is now none, for a few seconds. A person leaving, or a
/// connection that flickers for a moment, is not announced. For a call, <c>minPeers</c> is one: whoever is in the call with you is
/// someone you should stay connected to.
/// </summary>
public sealed class ConnectionWatch
{
    private readonly long _confirmMs, _windowMs, _networkMemoryMs;
    private readonly int _minPeers;
    private readonly Queue<(long At, int Peers)> _samples = new();
    private long _networkChangedAt = long.MinValue / 2;
    private long _zeroSince;
    private long _lostAt;

    /// <param name="confirm">How long nobody has to stay connected before it is called lost.</param>
    /// <param name="window">How far back "a moment ago" reaches.</param>
    /// <param name="networkMemory">How long after a network change one lost connection is enough to call it lost.</param>
    /// <param name="minPeers">How many people reachable a moment ago make losing them all worth announcing.</param>
    public ConnectionWatch(TimeSpan? confirm = null, TimeSpan? window = null, TimeSpan? networkMemory = null, int minPeers = 2)
    {
        _minPeers = minPeers;
        _confirmMs = (long)(confirm ?? TimeSpan.FromSeconds(4)).TotalMilliseconds;
        _windowMs = (long)(window ?? TimeSpan.FromSeconds(20)).TotalMilliseconds;
        _networkMemoryMs = (long)(networkMemory ?? TimeSpan.FromMinutes(2)).TotalMilliseconds;
    }

    /// <summary>True from the moment the connection is called lost until someone is connected again.</summary>
    public bool Reconnecting { get; private set; }

    /// <summary>The connection was lost and the app is trying again.</summary>
    public event Action? Lost;

    /// <summary>Someone is connected again after a loss.</summary>
    public event Action? Restored;

    /// <summary>How long it has been lost, or zero.</summary>
    public TimeSpan LostFor(long nowMs) => Reconnecting ? TimeSpan.FromMilliseconds(Math.Max(0, nowMs - _lostAt)) : TimeSpan.Zero;

    /// <summary>Forgets everything, without announcing anything: there is nothing to stay connected to any more (the call ended, or the others left).</summary>
    public void Reset()
    {
        _samples.Clear();
        _zeroSince = 0;
        Reconnecting = false;
    }

    /// <summary>The machine's network changed (see <see cref="ChatNode.NetworkChanged"/>).</summary>
    public void NetworkChanged(long nowMs) => _networkChangedAt = nowMs;

    /// <summary>Gives the number of people connected right now; call it whenever it changes and every few seconds.</summary>
    public void Update(int peers, long nowMs)
    {
        _samples.Enqueue((nowMs, peers));
        while (_samples.Count > 0 && nowMs - _samples.Peek().At > _windowMs) _samples.Dequeue();

        if (peers > 0)
        {
            _zeroSince = 0;
            if (Reconnecting)
            {
                Reconnecting = false;
                Restored?.Invoke();
            }
            return;
        }

        if (Reconnecting) return;
        int recent = _samples.Count == 0 ? 0 : _samples.Max(s => s.Peers);
        bool networkChanged = nowMs - _networkChangedAt <= _networkMemoryMs;
        if (!(recent >= _minPeers || (recent >= 1 && networkChanged))) { _zeroSince = 0; return; }

        if (_zeroSince == 0) _zeroSince = nowMs;
        if (nowMs - _zeroSince < _confirmMs) return;
        Reconnecting = true;
        _lostAt = nowMs;
        Lost?.Invoke();
    }
}
