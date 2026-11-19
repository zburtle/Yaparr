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

    // When this device first looked for the room, and the last time a member was asked for, or sent, the messages it is missing.
    private readonly long _startedAt = Environment.TickCount64;
    internal long SyncRequestedAt, HistoryReceivedAt;

    // While earlier history has been asked for (see RequestEarlierHistory): until when, and the time before which messages that come are that history, not news.
    internal long BackfillUntil, BackfillBefore;

    /// <summary>
    /// Messages from before what this device holds arrived from other members, because they were asked for with <see cref="RequestEarlierHistory"/>. They are
    /// kept, not shown as new messages: read them with <see cref="GetEarlierHistory"/>.
    /// </summary>
    public event Action? EarlierHistoryReceived;
    internal void RaiseEarlierHistory() => EarlierHistoryReceived?.Invoke();

    /// <summary>History was taken into the store from the account's kept copy (sync) or messages were removed by it: what is shown should be read again.</summary>
    public event Action? HistoryChanged;
    internal void RaiseHistoryChanged() => HistoryChanged?.Invoke();

    /// <summary>
    /// Asks the connected members of the room (up to three) for the messages before the one with the key <paramref name="beforeKey"/> (<see cref="ChatMessage.Key"/>),
    /// in a text channel, when this device has nothing earlier. They answer with what they hold; it is kept and <see cref="EarlierHistoryReceived"/> is raised.
    /// Returns how many members were asked: 0 when nobody is connected, history sync is off, or the message is not held.
    /// </summary>
    public int RequestEarlierHistory(string? channel, string beforeKey) => _node.RequestEarlierHistory(this, channel, beforeKey);

    /// <summary>
    /// When the room began, if its owner's signed start record is held here (a room made with a version that writes one; the record travels with the history like any
    /// message, but is never shown). Null for any other room, or until the record has come: then the end of the history can only be a guess. Once this is set and nothing
    /// earlier is held, the history is at its beginning (<see cref="HistoryPage.AtStart"/>).
    /// </summary>
    public DateTimeOffset? StartedAt => _node.RoomStartOf(this);

    /// <summary>The owner writes the room's start record, once, right after making the room. Nothing happens for a room you do not own, one with no owner, or one that has a record already.</summary>
    public void RecordStart() => _node.WriteRoomStart(this);

    /// <summary>
    /// Where the room is in getting going on this device, for showing that something is happening. A room that has been looked for for a
    /// minute with no one found is <see cref="RoomSyncState.NoOneOnline"/>, which is not a problem: it is just quiet.
    /// </summary>
    public RoomSyncState SyncState
    {
        get
        {
            long now = Environment.TickCount64;
            if (MemberStates.IsEmpty && Indirect.IsEmpty) return now - _startedAt < RoomSyncTimes.GiveUpFindingMs ? RoomSyncState.FindingMembers : RoomSyncState.NoOneOnline;
            long last = Math.Max(SyncRequestedAt, HistoryReceivedAt);
            return last > 0 && now - last < RoomSyncTimes.QuietMs ? RoomSyncState.LoadingHistory : RoomSyncState.Ready;
        }
    }

    internal RoomHistory History { get; set; } = null!;

    /// <summary>
    /// The newest messages of each text channel (<see cref="ChatNode.InitialHistoryPerChannel"/> of them), oldest first. Everything said in the room is kept;
    /// earlier messages are read with <see cref="GetEarlierHistory"/>.
    /// </summary>
    public IReadOnlyList<ChatMessage> GetHistory() => _node.GetHistory(this);

    /// <summary>
    /// The messages of a text channel before the message with the key <paramref name="beforeKey"/> (<see cref="ChatMessage.Key"/>), oldest first, up to
    /// <paramref name="count"/> of them, for scrolling back through the history.
    /// </summary>
    public HistoryPage GetEarlierHistory(string? channel, string beforeKey, int count = 100) => _node.GetEarlierHistory(this, channel, beforeKey, Math.Clamp(count, 1, 500));

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
    // The root of the voice tree we were attached to and when we saw the tree go: that member is left out when choosing who starts the next one.
    internal Yaparr.Identity.PeerId? VoiceTreeLastRoot;
    internal long VoiceTreeLostAt;
    internal Yaparr.Voice.VoiceSession? ActiveVoice;
    internal bool SelfMuted, SelfDeafened;
    internal long LastVoiceAt;
    internal readonly object IndirectGate = new();
    internal readonly Dictionary<string, StoredSound> SoundEntries = new();
    internal readonly object SoundGate = new();
    internal readonly Dictionary<string, StoredGif> GifEntries = new();
    internal readonly object GifGate = new();
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

    /// <summary>
    /// Whether <paramref name="message"/> names <paramref name="account"/>: by name, or with @everyone when the one who wrote it is
    /// an admin or the owner of this room (from a member, @everyone is only words).
    /// </summary>
    public bool Names(ChatMessage message, PeerId account)
    {
        var named = message.Parts.Mentions;
        return named.Any(m => m.Account == account) || (named.Any(m => m.IsEveryone) && RoleOf(message.From.Account) >= RoomRole.Admin);
    }

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

    /// <summary>
    /// The owner (only) takes an account out of the room without banning it: it leaves everyone's list of people, and its app leaves the room if it is on, and
    /// is told so (<see cref="RemovedFromRoom"/>). Nothing keeps the person out: they come back by opening the invite again, which is the difference from
    /// <see cref="Ban"/>. Throws <see cref="InvalidOperationException"/> for anyone who is not the owner, for yourself, and in a room with no owner.
    /// </summary>
    public void RemoveFromRoom(PeerId account, string name) => _node.RemoveFromRoom(this, account, name);

    /// <summary>
    /// The owner (only) kicks an account out of the voice channel without banning it: its app leaves the call and everyone else stops listing it there
    /// (<see cref="VoiceKicked"/> tells it). It can join again whenever it likes. Throws <see cref="InvalidOperationException"/> for anyone who is not the
    /// owner, for yourself, when the person is not in voice, and in a room with no owner.
    /// </summary>
    public void KickFromVoice(PeerId account) => _node.KickFromVoice(this, account);

    /// <summary>
    /// The owner removed you from the room (<see cref="RemoveFromRoom"/>) while you were on: leave it, and say so. Only an order that is news raises it; one
    /// from before this room was opened here, or an old one, does not. Raised on another thread.
    /// </summary>
    public event Action? RemovedFromRoom;
    internal void RaiseRemovedFromRoom() => RemovedFromRoom?.Invoke();

    /// <summary>The owner kicked you from voice (<see cref="KickFromVoice"/>): your call here has ended. Raised on another thread.</summary>
    public event Action? VoiceKicked;
    internal void RaiseVoiceKicked() => VoiceKicked?.Invoke();

    // When this room was opened here (unix time), and the newest orders about this account that have been acted on, so that none is acted on twice.
    internal readonly long CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal long RemovalHandled, KickHandled;

    /// <summary>Deletes a message for everyone in the room (the owner and admins delete any message, everyone else their own).</summary>
    public void DeleteMessage(ChatMessage message) => _node.DeleteMessage(this, message);

    /// <summary>Deletes a message for everyone in the room by its <see cref="ChatMessage.Key"/> (the owner and admins any message, everyone else their own), for a message the caller sent a moment ago and holds only as that key.</summary>
    public void DeleteMessage(string key) => _node.DeleteMessage(this, key);

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

    /// <summary>
    /// Moves a channel up or down among the channels of its kind (owner or admin only). The first text channel and the first voice
    /// channel cannot be moved and stay first. Does nothing when the channel is already at that end.
    /// </summary>
    public void MoveChannel(string id, bool up) => _node.MoveChannel(this, id, up);

    /// <summary>A channel was added, renamed, moved or removed.</summary>
    public event Action? ChannelsChanged;

    internal void RaiseChannelsChanged()
    {
        // The voice session ends first, so that anyone who hears of the change finds you already out of the channel.
        _node.LeaveRemovedVoiceChannel(this);
        ChannelsChanged?.Invoke();
    }

    /// <summary>The voice channel you were in was removed, and you were taken out of it. The argument is the channel's id.</summary>
    public event Action<string>? VoiceChannelRemoved;
    internal void RaiseVoiceChannelRemoved(string channelId) => VoiceChannelRemoved?.Invoke(channelId);

    /// <summary>The owner appointed or removed an admin.</summary>
    public event Action? RolesChanged;
    internal void RaiseRolesChanged() => RolesChanged?.Invoke();

    /// <summary>The room's soundboard, by name. Anyone in the room can add sounds; only the person who added one can change or remove it.</summary>
    public IReadOnlyList<SoundInfo> Sounds => _node.GetSounds(this);

    /// <summary>A sound was added, changed, removed or finished downloading.</summary>
    public event Action? SoundsChanged;
    internal void RaiseSoundsChanged() => SoundsChanged?.Invoke();

    /// <summary>The room's GIF shelf: pictures any member added, newest first. Anyone in the room can add one; only the person who added one can remove it, and an admin can take any down.</summary>
    public IReadOnlyList<GifInfo> Gifs => _node.GetGifs(this);

    /// <summary>A picture was added to or removed from the shelf, or finished downloading.</summary>
    public event Action? GifsChanged;
    internal void RaiseGifsChanged() => GifsChanged?.Invoke();

    /// <summary>Adds a picture to the room's GIF shelf (PNG, JPEG, GIF or WebP up to <see cref="GifLimits.MaxBytes"/>).</summary>
    public GifInfo AddGif(byte[] data) => _node.AddGif(this, data);

    /// <summary>
    /// The room's custom emoji, by name: pictures any member added to be written as <c>:name:</c> (see <see cref="CustomEmoji"/>). They are kept with the shelf
    /// (the same signed list, removals and take-downs: <see cref="RemoveGif"/> and <see cref="DeleteGif"/> work on their ids), but are not pictures to send.
    /// </summary>
    public IReadOnlyList<GifInfo> Emoji => _node.GetEmoji(this);

    /// <summary>Adds a custom emoji to the room under a name. Throws <see cref="ArgumentException"/>, with a message for the person, for a name or picture that cannot be used or a name that is taken.</summary>
    public GifInfo AddEmoji(string name, byte[] data) => _node.AddEmoji(this, name, data);

    /// <summary>Removes a picture you added.</summary>
    public void RemoveGif(string id) => _node.RemoveGif(this, id);

    /// <summary>Takes a picture off the shelf for everyone. Only for the owner and admins of the room.</summary>
    public void DeleteGif(string id) => _node.DeleteGif(this, id);

    /// <summary>The picture's file, if it is on this computer.</summary>
    public byte[]? GetGif(string id) => _node.ReadGif(this, id);

    /// <summary>Starts downloading the shelf's pictures that are not here yet; <see cref="GifsChanged"/> is raised as each arrives. Call it when the shelf is opened.</summary>
    public void RequestGifs() => _node.RequestGifs(this);

    /// <summary>A sound is being played into the voice channel you are in (possibly by you).</summary>
    public event Action<PeerId, SoundInfo>? SoundPlayed;
    internal void RaiseSoundPlayed(PeerId by, SoundInfo sound) => SoundPlayed?.Invoke(by, sound);

    /// <summary>Adds a sound to the soundboard (see <see cref="SoundClip.FromPcm"/> and <see cref="SoundClip.FromWav"/>).</summary>
    public SoundInfo AddSound(string name, SoundClip clip) => _node.AddSound(this, name, clip);

    /// <summary>Renames a sound you added; the owner and the admins of the room can rename any.</summary>
    public void RenameSound(string id, string name) => _node.RenameSound(this, id, name);

    /// <summary>Removes a sound you added.</summary>
    public void RemoveSound(string id) => _node.RemoveSound(this, id);

    /// <summary>The sound itself, if it is on this computer, for previewing.</summary>
    public SoundClip? GetSound(string id) => _node.ReadSound(this, id);

    /// <summary>Plays a sound into the voice channel. False if you are not in voice, have deafened yourself, or do not have the sound yet.</summary>
    public bool PlaySound(string id) => _node.PlaySound(this, id);

    /// <summary>The people currently in this room's voice channel, including you if you have joined.</summary>
    public IReadOnlyList<VoiceParticipant> VoiceParticipants => _node.GetVoiceParticipants(this);

    /// <summary>
    /// The voice channel a screen share from this device goes to: the one you are in, or else the one another of your linked devices is in
    /// (you can share from a device that is not in the call while another of yours is). Null when none of your devices is in voice, and then
    /// there is nothing to share into.
    /// </summary>
    public string? ScreenShareChannel => ActiveVoice?.ChannelId ?? _node.ChannelOfMyOtherDevice(this);

    /// <summary>Whether this device may share its screen with the room now: one of your devices, this one or a linked one, is in a voice channel.</summary>
    public bool CanShareScreen => ScreenShareChannel != null;

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

    /// <summary>The reactions on a message changed (the key of the message, <see cref="ChatMessage.Key"/>): someone put an emoji on it, took one off, or one arrived with the history.</summary>
    public event Action<string>? ReactionsChanged;

    /// <summary>The reactions on the messages of this room, counted from its history now (people you blocked or the room banned do not count).</summary>
    public ReactionSet GetReactions() => _node.GetReactions(this);

    /// <summary>Which of the messages with these keys (<see cref="ChatMessage.Key"/>) are still to be shown: held, and not deleted or from someone blocked or banned.</summary>
    public ISet<string> WhichAreShown(IReadOnlyCollection<string> messageKeys) => _node.WhichAreShown(this, messageKeys);

    /// <summary>The reactions on the messages with these keys (<see cref="ChatMessage.Key"/>), counted from the history now.</summary>
    public ReactionSet GetReactions(IReadOnlyCollection<string> messageKeys) => _node.GetReactions(this, messageKeys);

    /// <summary>
    /// Puts <paramref name="emoji"/> (one of <see cref="EmojiData"/>) on the message with the key <paramref name="messageKey"/>, or takes it off again
    /// (<paramref name="on"/> false). It is sent as a message of its own, in <paramref name="channelId"/> (the channel of the message it is on), and
    /// is not shown as one. Returns its id.
    /// </summary>
    public Task<string> SendReactionAsync(string messageKey, string emoji, bool on, string? channelId = null)
    {
        var id = _node.SendRoomMessage(this, MessageEnvelope.WrapReaction(messageKey, emoji, on, _node.LinkedDeviceKey, _node.LinkedDeviceCertificate), channelId);
        ReactionsChanged?.Invoke(messageKey);
        return Task.FromResult(id);
    }

    /// <summary>
    /// Puts a custom emoji (a room's or your own, see <see cref="ChatNode.PrepareEmoji"/>) on the message with the key <paramref name="messageKey"/>, or takes it off again.
    /// The reaction carries the emoji's description, so everyone can fetch its picture from you or from whoever has it.
    /// </summary>
    public Task<string> SendReactionAsync(string messageKey, CustomEmoji custom, bool on, string? channelId = null)
    {
        var id = _node.SendRoomMessage(this, MessageEnvelope.WrapReaction(messageKey, "", on, _node.LinkedDeviceKey, _node.LinkedDeviceCertificate, custom), channelId);
        ReactionsChanged?.Invoke(messageKey);
        return Task.FromResult(id);
    }

    /// <summary>Who a message you sent has reached, while it is still being tracked.</summary>
    public IReadOnlyList<DeliveryRecipient> GetDeliveryDetails(string messageId) => _node.GetDeliveryDetails(messageId);

    /// <summary>Sends a message to a text channel (see <see cref="Channels"/>) and returns its id.</summary>
    public Task<string> SendAsync(string text, string channelId) => Task.FromResult(_node.SendRoomMessage(this, text, channelId));

    /// <summary>
    /// Sends a message that answers the message with the key <paramref name="replyTo"/> (<see cref="ChatMessage.Key"/>) and names
    /// <paramref name="mentions"/> (accounts, up to <see cref="MessageEnvelope.MaxMentions"/>); the words themselves should say
    /// who is named: each <see cref="Mention"/> says whom, and the name as it is written after an @ in the words. Either can be left out. Returns the id of the message.
    /// </summary>
    public Task<string> SendAsync(string text, string? channelId, string? replyTo, IReadOnlyCollection<Mention>? mentions = null, LinkPreview? preview = null, IReadOnlyCollection<CustomEmoji>? emoji = null) =>
        Task.FromResult(_node.SendRoomMessage(this, MessageEnvelope.Wrap(text, replyTo, mentions, preview, emoji), channelId));

    /// <summary>
    /// Asks the members you are connected to for messages you do not have, by key (<see cref="ChatMessage.Key"/>), for example the
    /// message a reply answers that has dropped out of your history. What they still hold arrives through
    /// <see cref="MessageReceived"/> (marked <see cref="ChatMessage.IsCatchUp"/>); what nobody holds never comes. Returns how many
    /// members were asked. At most <see cref="ChatNode.MaxWanted"/> keys are asked for at once.
    /// </summary>
    public int RequestMessages(IEnumerable<string> keys) => _node.RequestMessages(this, keys);

    /// <summary>Sends an unconfirmed message again to the members who have not confirmed it.</summary>
    public void Retry(string messageId) => _node.RetryMessage(messageId);

    public Task LeaveAsync() => _node.LeaveRoomAsync(this);

    // A reaction arrives as a message but is not shown as one: it changes what the message it is on shows.
    internal void RaiseMessage(ChatMessage m)
    {
        if (m.Parts.Reaction is { } reaction) ReactionsChanged?.Invoke(reaction.Target);
        else MessageReceived?.Invoke(m);
    }
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
    /// <summary>Whether this is the chat with yourself (<see cref="ChatNode.NotesChat"/>), where you write notes for your own devices. It cannot be removed.</summary>
    public bool IsNotes => _node.IsNotesChat(PeerId);

    /// <summary>The other person's connected device; none for the chat with yourself, which is not another person.</summary>
    public PeerInfo? Peer => IsNotes ? null : _node.GetPeerInfo(PeerId);

    /// <summary>Whether a message can be handed over now. The chat with yourself always can: a note is kept here and reaches your other devices when they are reachable.</summary>
    public bool IsConnected => IsNotes || Peer != null;

    /// <summary>The name to show: the connected person's, or else the last one they used in this conversation; "Notes to self" for the chat with yourself.</summary>
    public string DisplayName => IsNotes ? ChatNode.NotesName : Peer?.DisplayName ?? _node.DirectNameOf(PeerId) ?? PeerId.Short;

    /// <summary>The newest messages of this conversation (<see cref="ChatNode.InitialHistoryPerChannel"/> of them; those sent and received on any of your devices), oldest first. Everything is kept; earlier ones are read with <see cref="GetEarlierHistory"/>.</summary>
    public IReadOnlyList<ChatMessage> History => _node.GetDirectHistory(PeerId);

    /// <summary>
    /// Earlier messages of this conversation arrived from your other devices or from the other person's, because they were asked for with
    /// <see cref="RequestEarlierHistory"/>. They are kept, not shown as new messages: read them with <see cref="GetEarlierHistory"/>.
    /// </summary>
    public event Action? EarlierHistoryReceived;
    internal void RaiseEarlierHistory() => EarlierHistoryReceived?.Invoke();

    /// <summary>History was taken into the store from the account's kept copy (sync): what is shown should be read again.</summary>
    public event Action? HistoryChanged;
    internal void RaiseHistoryChanged() => HistoryChanged?.Invoke();

    /// <summary>
    /// Asks your other devices and the other person's (those connected, up to four) for the messages before the one with the id <paramref name="beforeId"/>, when this
    /// device holds nothing earlier. Returns how many were asked: 0 when none is connected, history sync is off, or the message is not held.
    /// </summary>
    public int RequestEarlierHistory(string beforeId) => _node.RequestEarlierDirectHistory(PeerId, beforeId);

    /// <summary>
    /// Brings the messages with these keys (<see cref="ChatMessage.Key"/>, what a reply names its original by) to the screen: the ones this device holds but has not shown
    /// are shown at once, and the rest are asked for from your other devices and the person's, and arrive through <see cref="MessageReceived"/>. Returns how many devices
    /// were asked, 0 if none could be, or -1 when everything was held here.
    /// </summary>
    public int RequestMessages(IEnumerable<string> keys) => _node.RequestDirectMessages(PeerId, keys);

    /// <summary>The messages before the one with the id <paramref name="beforeId"/> (<see cref="ChatMessage.Id"/>), oldest first, up to <paramref name="count"/> of them.</summary>
    public HistoryPage GetEarlierHistory(string beforeId, int count = 100) => _node.GetEarlierDirectHistory(PeerId, beforeId, Math.Clamp(count, 1, 500));

    /// <summary>The time of the newest message from them that you have read, on any of your devices.</summary>
    public DateTimeOffset? ReadUpTo => _node.DirectReadUpTo(PeerId);

    /// <summary>Records that you have read the conversation up to now, so your other devices stop counting it as unread.</summary>
    public void MarkRead() => _node.MarkDirectRead(PeerId);

    /// <summary>Another of your devices read this conversation.</summary>
    public event Action? ReadChanged;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<DeliveryStatus>? DeliveryChanged;

    /// <summary>The reactions on a message changed (the key of the message, <see cref="ChatMessage.Key"/>): someone put an emoji on it, took one off, or one arrived with the history.</summary>
    public event Action<string>? ReactionsChanged;

    /// <summary>The reactions on the messages of this conversation, yours and theirs.</summary>
    public ReactionSet GetReactions() => _node.GetDirectReactions(PeerId);

    /// <summary>The reactions on the messages with these keys (<see cref="ChatMessage.Key"/>), yours and theirs.</summary>
    public ReactionSet GetReactions(IReadOnlyCollection<string> messageKeys) => _node.GetDirectReactions(PeerId, messageKeys);

    /// <summary>Puts <paramref name="emoji"/> on the message with the key <paramref name="messageKey"/>, or takes it off again (<paramref name="on"/> false). The emoji has to be one of <see cref="EmojiData"/>.</summary>
    public Task<string> SendReactionAsync(string messageKey, string emoji, bool on)
    {
        var id = _node.SendDirectMessage(PeerId, MessageEnvelope.WrapReaction(messageKey, emoji, on));
        ReactionsChanged?.Invoke(messageKey);
        return Task.FromResult(id);
    }

    /// <summary>Puts a custom emoji (see <see cref="ChatNode.PrepareEmoji"/>) on the message with the key <paramref name="messageKey"/>, or takes it off again.</summary>
    public Task<string> SendReactionAsync(string messageKey, CustomEmoji custom, bool on)
    {
        var id = _node.SendDirectMessage(PeerId, MessageEnvelope.WrapReaction(messageKey, "", on, custom: custom));
        ReactionsChanged?.Invoke(messageKey);
        return Task.FromResult(id);
    }

    public Task<string> SendAsync(string text) => Task.FromResult(_node.SendDirectMessage(PeerId, text));

    /// <summary>Sends a message that answers the message with the key <paramref name="replyTo"/> (<see cref="ChatMessage.Key"/>).</summary>
    public Task<string> SendAsync(string text, string? replyTo, LinkPreview? preview = null, IReadOnlyCollection<CustomEmoji>? emoji = null) => Task.FromResult(_node.SendDirectMessage(PeerId, MessageEnvelope.Wrap(text, replyTo, null, preview, emoji)));

    /// <summary>Who a message you sent has reached, while it is still being tracked.</summary>
    public IReadOnlyList<DeliveryRecipient> GetDeliveryDetails(string messageId) => _node.GetDeliveryDetails(messageId);

    public void Retry(string messageId) => _node.RetryMessage(messageId);

    // A reaction arrives as a message but is not shown as one: it changes what the message it is on shows.
    internal void RaiseMessage(ChatMessage m)
    {
        if (m.Parts.Reaction is { } reaction) ReactionsChanged?.Invoke(reaction.Target);
        else MessageReceived?.Invoke(m);
    }
    internal void RaiseDelivery(DeliveryStatus s) => DeliveryChanged?.Invoke(s);
    internal void RaiseRead() => ReadChanged?.Invoke();
}

/// <summary>How far a room is in getting going on this device (see <see cref="ChatRoom.SyncState"/>).</summary>
public enum RoomSyncState
{
    /// <summary>No one from the room has been reached yet.</summary>
    FindingMembers,

    /// <summary>Connected to members, and the messages missed so far are still arriving.</summary>
    LoadingHistory,

    /// <summary>Connected, and nothing more is arriving.</summary>
    Ready,

    /// <summary>Looked for a while and found no one; the room is quiet, and will connect when someone comes online.</summary>
    NoOneOnline,
}

internal static class RoomSyncTimes
{
    public const long GiveUpFindingMs = 60_000;

    /// <summary>History counts as still arriving until this long after the last request or batch.</summary>
    public const long QuietMs = 3_000;
}
