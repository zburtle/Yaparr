using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

public sealed class ChatRoom
{
    private readonly ChatNode _node;
    internal readonly ConcurrentDictionary<PeerId, ChatNode.PeerState> MemberStates = new();

    private readonly RoomKey _secret;

    internal ChatRoom(ChatNode node, string name, RoomKey secret)
    {
        _node = node;
        _secret = secret;
        Name = name;
        Topic = secret.Topic;
        Key = Convert.ToHexString(Topic).ToLowerInvariant();
    }

    public string Name { get; }

    internal RoomHistory History { get; set; } = null!;

    public IReadOnlyList<ChatMessage> GetHistory() => _node.GetHistory(this);

    internal byte[] Topic { get; }
    internal string Key { get; }

    /// <summary>The room key as text.</summary>
    internal string SecretText => _secret.ToString();

    internal string RefFor(byte[] sessionId, PeerId prover)
    {
        var data = sessionId.Concat(Encoding.UTF8.GetBytes(prover.Value)).ToArray();
        return Convert.ToBase64String(HMACSHA256.HashData(_secret.AuthKey, data));
    }

    internal readonly ConcurrentDictionary<PeerId, PeerInfo> Indirect = new();
    internal readonly Dictionary<PeerId, PresenceEntry> Presence = new();
    internal readonly Dictionary<PeerId, VoiceEntry> Voice = new();
    internal readonly ConcurrentDictionary<string, MediaGroup> Media = new();
    internal long VoiceTreeWantedSince, VoiceTreeLowSince;
    internal Yaparr.Voice.VoiceSession? ActiveVoice;
    internal bool SelfMuted, SelfDeafened;
    internal long LastVoiceAt;
    internal readonly object IndirectGate = new();
    internal long LastPresenceAt, LastShuffleAt;

    public IReadOnlyCollection<PeerInfo> Members =>
        MemberStates.Values.Select(s => s.Info).Concat(Indirect.Values).ToList();

    public event Action<ChatMessage>? MessageReceived;

    /// <summary>Raised when someone joins or leaves the voice channel or mutes or unmutes themselves.</summary>
    public event Action? VoiceChanged;
    internal void RaiseVoiceChanged() => VoiceChanged?.Invoke();

    /// <summary>The streams shared with this room right now (a screen, for example), as last announced.</summary>
    public IReadOnlyList<MediaGroupInfo> MediaGroups =>
        Media.Values.Where(g => g.Kind != "voice").Select(g => g.Info).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Where this node stands in the room's voice tree, if the channel is big enough to have one.</summary>
    public MediaStatus? VoiceTreeStatus =>
        Media.Values.FirstOrDefault(g => g.Kind == "voice" && g.Joined) is { } g ? _node.StatusOf(g) : null;

    /// <summary>A stream was shared or ended.</summary>
    public event Action? MediaGroupsChanged;
    internal void RaiseMediaChanged() => MediaGroupsChanged?.Invoke();

    /// <summary>The people currently in this room's voice channel, including you if you have joined.</summary>
    public IReadOnlyList<VoiceParticipant> VoiceParticipants => _node.GetVoiceParticipants(this);

    /// <summary>Your session in this room's voice channel, or null if you have not joined.</summary>
    public Yaparr.Voice.VoiceSession? CurrentVoice => ActiveVoice;

    /// <summary>Joins the voice channel. Dispose or Leave the returned session to leave.</summary>
    public Yaparr.Voice.VoiceSession JoinVoice() => _node.JoinVoice(this);
    public event Action<PeerInfo>? MemberJoined;
    public event Action<PeerInfo>? MemberLeft;

    /// <summary>A member of this room changed their display name.</summary>
    public event Action<PeerInfo>? MemberRenamed;
    public event Action<DeliveryStatus>? DeliveryChanged;

    /// <summary>Sends a message and returns its id, which <see cref="DeliveryChanged"/> reports on.</summary>
    public Task<string> SendAsync(string text) => Task.FromResult(_node.SendRoomMessage(this, text));

    /// <summary>Sends an unconfirmed message again to the members who have not confirmed it.</summary>
    public void Retry(string messageId) => _node.RetryMessage(messageId);

    public Task LeaveAsync() => _node.LeaveRoomAsync(this);

    internal void RaiseMessage(ChatMessage m) => MessageReceived?.Invoke(m);
    internal void RaiseJoined(PeerInfo p) => MemberJoined?.Invoke(p);
    internal void RaiseLeft(PeerInfo p) => MemberLeft?.Invoke(p);
    internal void RaiseRenamed(PeerInfo p) => MemberRenamed?.Invoke(p);
    internal void RaiseDelivery(DeliveryStatus s) => DeliveryChanged?.Invoke(s);
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
    public event Action<DeliveryStatus>? DeliveryChanged;

    public Task<string> SendAsync(string text) => Task.FromResult(_node.SendDirectMessage(PeerId, text));

    public void Retry(string messageId) => _node.RetryMessage(messageId);

    internal void RaiseMessage(ChatMessage m) => MessageReceived?.Invoke(m);
    internal void RaiseDelivery(DeliveryStatus s) => DeliveryChanged?.Invoke(s);
}
