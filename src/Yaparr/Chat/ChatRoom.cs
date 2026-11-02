using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

public sealed class ChatRoom
{
    private readonly ChatNode _node;
    internal readonly ConcurrentDictionary<PeerId, ChatNode.PeerState> MemberStates = new();

    // Replaced when the owner replaces the room's secret; everything that has to stay the same across that goes by Key.
    private volatile RoomKey _secret;

    internal ChatRoom(ChatNode node, string name, RoomKey secret)
    {
        _node = node;
        _secret = secret;
        Name = name;
        Key = secret.RoomId;
    }

    public string Name { get; }

    internal RoomHistory History { get; set; } = null!;

    public IReadOnlyList<ChatMessage> GetHistory() => _node.GetHistory(this);

    /// <summary>Where the room is found now: derived from the current secret, so it changes when the secret is replaced.</summary>
    internal byte[] Topic => _secret.Topic;

    /// <summary>What the room is, for good: the topic of its first secret. History, admins, bans, channels and every signature are tied to it.</summary>
    internal string Key { get; }

    /// <summary>The room's identity for good (see <see cref="RoomKey.RoomId"/>).</summary>
    public string RoomId => Key;

    /// <summary>The room key as text.</summary>
    internal string SecretText => _secret.ToString();

    internal RoomKey CurrentKey => _secret;

    /// <summary>How many times the owner has replaced the room's secret.</summary>
    public int Epoch => _secret.Epoch;

    /// <summary>The invite for the room as it is now. After the secret has been replaced, the old invite no longer works; give out this one.</summary>
    public string Invite => _secret.ToString();

    internal string? RekeyHash;
    internal readonly object RekeyGate = new();

    // The secrets this room had before, newest first, and the order that replaced the last one. They let us find, and hand the new
    // secret to, listed people who were away when it was replaced and still come looking under an old one.
    internal readonly List<PreviousKey> PreviousKeys = new();
    internal StoredRekey? LastRekey;

    // Everyone seen in this room, by account, so the owner can give the new secret to people who are not online when it is replaced.
    internal readonly Dictionary<string, RosterEntry> Roster = new();
    internal readonly object RosterGate = new();
    internal bool RosterDirty;

    internal void SwitchTo(RoomKey key) => _secret = key;

    /// <summary>
    /// People who would get the new secret from <see cref="RotateSecret"/>: everyone this device has seen in the room (most recent first, up to
    /// 256), online or not, except yourself and anyone banned or blocked. Someone who is away gets it when they come back.
    /// </summary>
    public IReadOnlyList<RotationCandidate> RotationCandidates => _node.GetRotationCandidates(this);

    /// <summary>
    /// Everyone this device has seen in the room, here now or not, one entry per person (not per device), without you. The ones who
    /// are away come from the room's memory of who has been in it, which a room made by an earlier version, one with no owner, does not keep.
    /// </summary>
    public IReadOnlyList<RoomPerson> People => _node.GetRoomPeople(this);

    /// <summary>
    /// Replaces the room's secret (owner only) and gives the new one to <paramref name="recipients"/>, by account. Everyone else, banned people
    /// included, is locked out: they can no longer find the room or talk in it, which a ban alone cannot do. Anyone who is not online now has to be
    /// given <see cref="Invite"/> some other way. Returns the new invite.
    /// </summary>
    public string RotateSecret(IReadOnlyCollection<PeerId> recipients) => _node.RotateSecret(this, recipients);

    /// <summary>The secret was replaced, here or by the owner; the arguments are the old and the new invite. Remember the new one.</summary>
    public event Action<string, string>? SecretChanged;
    internal void RaiseSecretChanged(string oldInvite, string newInvite) => SecretChanged?.Invoke(oldInvite, newInvite);

    internal string RefFor(byte[] sessionId, PeerId prover) => RefForKey(_secret, sessionId, prover);

    internal static string RefForKey(RoomKey key, byte[] sessionId, PeerId prover)
    {
        var data = sessionId.Concat(Encoding.UTF8.GetBytes(prover.Value)).ToArray();
        return Convert.ToBase64String(HMACSHA256.HashData(key.AuthKey, data));
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
    internal readonly Dictionary<string, StoredSound> SoundEntries = new();
    internal readonly object SoundGate = new();
    internal readonly Dictionary<string, StoredRole> RoleEntries = new();
    internal readonly object RoleGate = new();
    internal readonly Dictionary<string, StoredMod> ModEntries = new();
    internal readonly object ModGate = new();
    internal int ModVersion;
    internal ModView? ModCache;
    internal int ModCacheVersion = -1;
    internal readonly Dictionary<string, StoredChannel> ChannelEntries = new();
    internal readonly object ChannelGate = new();
    internal int ChannelVersion;
    internal IReadOnlyList<ChannelInfo>? ChannelCache;
    internal int ChannelCacheVersion = -1;
    internal long LastSoundFetchAt;
    internal long LastPresenceAt, LastShuffleAt;

    public IReadOnlyCollection<PeerInfo> Members =>
        MemberStates.Values.Select(s => s.Info).Concat(Indirect.Values).ToList();

    public event Action<ChatMessage>? MessageReceived;

    /// <summary>Raised when someone joins or leaves the voice channel or mutes or unmutes themselves.</summary>
    public event Action? VoiceChanged;
    internal void RaiseVoiceChanged() => VoiceChanged?.Invoke();

    /// <summary>Who is in the voice channel you are in and whom you cannot reach right now (nobody, when you are not in voice).</summary>
    public VoiceReach VoiceReach => _node.GetVoiceReach(this);

    /// <summary>You joined voice in this room on another of your devices, so this device left it.</summary>
    public event Action<PeerId>? VoiceTakenOver;
    internal void RaiseVoiceTakenOver(PeerId device) => VoiceTakenOver?.Invoke(device);

    /// <summary>The streams shared with this room right now (a screen, for example), as last announced.</summary>
    public IReadOnlyList<MediaGroupInfo> MediaGroups =>
        Media.Values.Where(g => g.Kind != "voice").Select(g => g.Info).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Where this node stands in the room's voice tree, if the channel is big enough to have one.</summary>
    public MediaStatus? VoiceTreeStatus =>
        Media.Values.FirstOrDefault(g => g.Kind == "voice" && g.Joined) is { } g ? _node.StatusOf(g) : null;

    /// <summary>A stream was shared or ended.</summary>
    public event Action? MediaGroupsChanged;
    internal void RaiseMediaChanged() => MediaGroupsChanged?.Invoke();

    /// <summary>The account that owns the room, named in its invite; null for a room made by an earlier version, which has no admins.</summary>
    public PeerId? Owner => _secret.Owner;

    /// <summary>What you are in this room.</summary>
    public RoomRole MyRole => _node.RoleIn(this, _node.AccountId);

    /// <summary>What a person (by account, see <see cref="PeerInfo.Account"/>) is in this room.</summary>
    public RoomRole RoleOf(PeerId account) => _node.RoleIn(this, account);

    /// <summary>The admins the owner has appointed, not counting the owner.</summary>
    public IReadOnlyList<PeerId> Admins => _node.GetAdmins(this);

    /// <summary>Appoints or removes an admin. Only the owner can; everyone in the room sees it.</summary>
    public void SetAdmin(PeerId account, bool admin) => _node.SetAdmin(this, account, admin);

    /// <summary>The people this room's admins have banned, as far as this device knows. Empty for a room with no owner.</summary>
    public IReadOnlyList<BannedPerson> Banned => _node.GetBanned(this);

    /// <summary>Whether this account is banned from the room.</summary>
    public bool IsBanned(PeerId account) => _node.IsBannedIn(this, account);

    /// <summary>
    /// Bans an account from the room (owner or admin only; an admin cannot ban another admin or the owner). Every member's app
    /// then ignores the person: their messages are hidden, they leave the member list and voice, and nothing is sent to them.
    /// This does not take the room key away from them; it holds until they come back under another identity.
    /// </summary>
    public void Ban(PeerId account, string name) => _node.Ban(this, account, name);

    /// <summary>Lifts a ban.</summary>
    public void Unban(PeerId account) => _node.Unban(this, account);

    /// <summary>Deletes a message for everyone in the room (owner or admin only).</summary>
    public void DeleteMessage(ChatMessage message) => _node.DeleteMessage(this, message);

    /// <summary>Removes a sound from the soundboard, whoever added it (owner or admin only).</summary>
    public void DeleteSound(string id) => _node.DeleteSound(this, id);

    /// <summary>A ban, an unban, or a deleted message or sound changed what the room shows. Read <see cref="GetHistory"/> and the member lists again.</summary>
    public event Action? ModerationChanged;
    internal void RaiseModerationChanged() => ModerationChanged?.Invoke();

    /// <summary>
    /// The room's text and voice channels, in order. Every room has the first text channel (<see cref="RoomChannels.DefaultText"/>) and the
    /// first voice channel (<see cref="RoomChannels.DefaultVoice"/>). Everyone can use every channel; only the owner and admins change the list.
    /// </summary>
    public IReadOnlyList<ChannelInfo> Channels => _node.GetChannels(this);

    /// <summary>Adds a channel (owner or admin only).</summary>
    public ChannelInfo CreateChannel(string name, ChannelKind kind) => _node.CreateChannel(this, name, kind);

    /// <summary>Renames a channel, the first text and voice channels included (owner or admin only).</summary>
    public void RenameChannel(string id, string name) => _node.RenameChannel(this, id, name);

    /// <summary>Removes a channel (owner or admin only); the first text and voice channels cannot be removed. Its messages stay in the history but are no longer shown.</summary>
    public void DeleteChannel(string id) => _node.DeleteChannel(this, id);

    /// <summary>A channel was added, renamed or removed.</summary>
    public event Action? ChannelsChanged;
    internal void RaiseChannelsChanged() => ChannelsChanged?.Invoke();

    /// <summary>The owner appointed or removed an admin.</summary>
    public event Action? RolesChanged;
    internal void RaiseRolesChanged() => RolesChanged?.Invoke();

    /// <summary>The room's soundboard, by name. Anyone in the room can add sounds; only the person who added one can change or remove it.</summary>
    public IReadOnlyList<SoundInfo> Sounds => _node.GetSounds(this);

    /// <summary>A sound was added, changed, removed or finished downloading.</summary>
    public event Action? SoundsChanged;
    internal void RaiseSoundsChanged() => SoundsChanged?.Invoke();

    /// <summary>A sound is being played into the voice channel you are in (possibly by you).</summary>
    public event Action<PeerId, SoundInfo>? SoundPlayed;
    internal void RaiseSoundPlayed(PeerId by, SoundInfo sound) => SoundPlayed?.Invoke(by, sound);

    /// <summary>Adds a sound to the soundboard (see <see cref="SoundClip.FromPcm"/> and <see cref="SoundClip.FromWav"/>).</summary>
    public SoundInfo AddSound(string name, SoundClip clip) => _node.AddSound(this, name, clip);

    /// <summary>Renames a sound you added.</summary>
    public void RenameSound(string id, string name) => _node.RenameSound(this, id, name);

    /// <summary>Removes a sound you added.</summary>
    public void RemoveSound(string id) => _node.RemoveSound(this, id);

    /// <summary>The sound itself, if it is on this computer, for previewing.</summary>
    public SoundClip? GetSound(string id) => _node.ReadSound(this, id);

    /// <summary>Plays a sound into the voice channel. False if you are not in voice, have deafened yourself, or do not have the sound yet.</summary>
    public bool PlaySound(string id) => _node.PlaySound(this, id);

    /// <summary>The people currently in this room's voice channel, including you if you have joined.</summary>
    public IReadOnlyList<VoiceParticipant> VoiceParticipants => _node.GetVoiceParticipants(this);

    /// <summary>Your session in this room's voice channel, or null if you have not joined.</summary>
    public Yaparr.Voice.VoiceSession? CurrentVoice => ActiveVoice;

    /// <summary>Joins the voice channel. Dispose or Leave the returned session to leave.</summary>
    public Yaparr.Voice.VoiceSession JoinVoice() => _node.JoinVoice(this, RoomChannels.DefaultVoice);

    /// <summary>Joins a voice channel (see <see cref="Channels"/>), leaving the one you are in. Everyone in a room can use every channel.</summary>
    public Yaparr.Voice.VoiceSession JoinVoice(string channelId) => _node.JoinVoice(this, channelId);
    public event Action<PeerInfo>? MemberJoined;
    public event Action<PeerInfo>? MemberLeft;

    /// <summary>A member of this room changed their display name.</summary>
    public event Action<PeerInfo>? MemberRenamed;
    public event Action<DeliveryStatus>? DeliveryChanged;

    /// <summary>Sends a message and returns its id, which <see cref="DeliveryChanged"/> reports on.</summary>
    public Task<string> SendAsync(string text) => Task.FromResult(_node.SendRoomMessage(this, text, null));

    /// <summary>Who a message you sent has reached, while it is still being tracked.</summary>
    public IReadOnlyList<DeliveryRecipient> GetDeliveryDetails(string messageId) => _node.GetDeliveryDetails(messageId);

    /// <summary>Sends a message to a text channel (see <see cref="Channels"/>) and returns its id.</summary>
    public Task<string> SendAsync(string text, string channelId) => Task.FromResult(_node.SendRoomMessage(this, text, channelId));

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

    /// <summary>The name to show: the connected person's, or else the last one they used in this conversation.</summary>
    public string DisplayName => Peer?.DisplayName ?? _node.DirectNameOf(PeerId) ?? PeerId.Short;

    /// <summary>The messages of this conversation that are kept (those sent and received on any of your devices), oldest first.</summary>
    public IReadOnlyList<ChatMessage> History => _node.GetDirectHistory(PeerId);

    /// <summary>The time of the newest message from them that you have read, on any of your devices.</summary>
    public DateTimeOffset? ReadUpTo => _node.DirectReadUpTo(PeerId);

    /// <summary>Records that you have read the conversation up to now, so your other devices stop counting it as unread.</summary>
    public void MarkRead() => _node.MarkDirectRead(PeerId);

    /// <summary>Another of your devices read this conversation.</summary>
    public event Action? ReadChanged;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<DeliveryStatus>? DeliveryChanged;

    public Task<string> SendAsync(string text) => Task.FromResult(_node.SendDirectMessage(PeerId, text));

    /// <summary>Who a message you sent has reached, while it is still being tracked.</summary>
    public IReadOnlyList<DeliveryRecipient> GetDeliveryDetails(string messageId) => _node.GetDeliveryDetails(messageId);

    public void Retry(string messageId) => _node.RetryMessage(messageId);

    internal void RaiseMessage(ChatMessage m) => MessageReceived?.Invoke(m);
    internal void RaiseDelivery(DeliveryStatus s) => DeliveryChanged?.Invoke(s);
    internal void RaiseRead() => ReadChanged?.Invoke();
}
