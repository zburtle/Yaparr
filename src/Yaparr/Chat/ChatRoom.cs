using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

public sealed class ChatRoom
{
    private readonly ChatNode _node;
    internal readonly ConcurrentDictionary<PeerId, ChatNode.PeerState> MemberStates = new();

    private readonly RoomKey? _secret;

    internal ChatRoom(ChatNode node, string name, RoomKey? secret)
    {
        _node = node;
        _secret = secret;
        Name = name;
        Topic = secret?.Topic ?? Topics.ForRoom(name);
        Key = Convert.ToHexString(Topic).ToLowerInvariant();
    }

    public string Name { get; }

    internal RoomHistory History { get; set; } = null!;

    public IReadOnlyList<ChatMessage> GetHistory() => _node.GetHistory(this);

    public bool IsPrivate => _secret != null;

    internal byte[] Topic { get; }
    internal string Key { get; }

    internal string RefFor(byte[] sessionId, PeerId prover)
    {
        if (_secret == null) return Key;
        var data = sessionId.Concat(Encoding.UTF8.GetBytes(prover.Value)).ToArray();
        return Convert.ToBase64String(HMACSHA256.HashData(_secret.AuthKey, data));
    }

    public IReadOnlyCollection<PeerInfo> Members => MemberStates.Values.Select(s => s.Info).ToList();

    public event Action<ChatMessage>? MessageReceived;
    public event Action<PeerInfo>? MemberJoined;
    public event Action<PeerInfo>? MemberLeft;

    public Task SendAsync(string text)
    {
        _node.SendRoomMessage(this, text);
        return Task.CompletedTask;
    }

    public Task LeaveAsync() => _node.LeaveRoomAsync(this);

    internal void RaiseMessage(ChatMessage m) => MessageReceived?.Invoke(m);
    internal void RaiseJoined(PeerInfo p) => MemberJoined?.Invoke(p);
    internal void RaiseLeft(PeerInfo p) => MemberLeft?.Invoke(p);
}

public sealed class DirectChat
{
    private readonly ChatNode _node;

    internal DirectChat(ChatNode node, PeerId peer)
    {
        _node = node;
        PeerId = peer;
    }

    public PeerId PeerId { get; }
    public PeerInfo? Peer => _node.GetPeerInfo(PeerId);
    public bool IsConnected => Peer != null;

    public event Action<ChatMessage>? MessageReceived;

    public Task SendAsync(string text)
    {
        _node.SendDirectMessage(PeerId, text);
        return Task.CompletedTask;
    }

    internal void RaiseMessage(ChatMessage m) => MessageReceived?.Invoke(m);
}
