using System.Collections.Concurrent;
using Yaparr.Identity;

namespace Yaparr.Chat;

// A personal block: nothing from that person reaches you, and you do nothing for them. It is only your own view of
// the room. Everyone else still sees them, and your app still passes the room's messages along as usual.
public sealed partial class ChatNode
{
    private readonly ConcurrentDictionary<PeerId, byte> _blocked = new();

    /// <summary>The people you have blocked.</summary>
    public IReadOnlyCollection<PeerId> BlockedPeers => _blocked.Keys.ToList();

    public bool IsBlocked(PeerId peer) => _blocked.ContainsKey(peer);

    /// <summary>Raised when someone is blocked (true) or unblocked (false), so the app can remember the list.</summary>
    public event Action<PeerId, bool>? BlockChanged;

    /// <summary>
    /// Blocks someone. Their messages, pictures and voice no longer reach you, they leave your member lists and
    /// voice channel, and your connection to them is closed and not opened again. You also stop relaying for them.
    /// </summary>
    public void Block(PeerId peer)
    {
        if (peer == Id) throw new ArgumentException("You cannot block yourself.", nameof(peer));
        if (!_blocked.TryAdd(peer, 0)) return;
        _options.Log?.Invoke($"blocked {peer.Short}");
        Network.Get(peer)?.Close(notifyPeer: true);
        RefreshBlockedViews();
        BlockChanged?.Invoke(peer, true);
    }

    public void Unblock(PeerId peer)
    {
        if (!_blocked.TryRemove(peer, out _)) return;
        _options.Log?.Invoke($"unblocked {peer.Short}");
        RefreshBlockedViews();
        BlockChanged?.Invoke(peer, false);
    }

    private void RefreshBlockedViews()
    {
        foreach (var room in _rooms.Values)
        {
            RefreshIndirect(room);
            VoiceRosterChanged(room);
        }
    }
}
