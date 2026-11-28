using System.Collections.Concurrent;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

// A room's watch playlist: what is to be played next when someone streams a film from their Jellyfin server into the room. The person who is streaming is the one who keeps it:
// they sign the whole list each time (the newest version wins) and send it to the room, and it is kept on every member's computer so that it is there for the next stream.
// Everyone else asks them (a signed request, which may pass through other members on its way): to add something, if they allow it, to remove something that person added, and
// to look through their library to find something to add. Those requests are answered by the streaming app, which is the only one that can reach the library.
public sealed partial class ChatNode
{
    private const int AskHops = 3;
    private readonly ConcurrentDictionary<string, long> _seenAsks = new();

    private static string PlaylistFile(string directory, ChatRoom room) => Path.Combine(directory, room.Key + ".playlist.json");

    private void LoadPlaylist(ChatRoom room)
    {
        if (_options.SoundboardDirectory is not { } directory) return;
        try
        {
            var path = PlaylistFile(directory, room);
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<StoredPlaylist>(File.ReadAllBytes(path), Json);
            if (stored != null && SignedPlaylist.Verify(stored, room.Key)) lock (room.PlaylistGate) room.PlaylistState = stored;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not read the playlist: " + ex.Message);
        }
    }

    private void SavePlaylist(ChatRoom room)
    {
        if (_options.SoundboardDirectory is not { } directory) return;
        try
        {
            StoredPlaylist? state;
            lock (room.PlaylistGate) state = room.PlaylistState;
            if (state == null) return;
            Directory.CreateDirectory(directory);
            var path = PlaylistFile(directory, room);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(state, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the playlist: " + ex.Message);
        }
    }

    internal PlaylistSnapshot GetPlaylist(ChatRoom room)
    {
        StoredPlaylist? s;
        lock (room.PlaylistGate) s = room.PlaylistState;
        return s == null ? PlaylistSnapshot.Empty : new PlaylistSnapshot(s.Version, s.ServerId, s.HostName, s.OthersMayAdd, s.Items, s.From);
    }

    // ---- the streamer's side ----

    /// <summary>
    /// This device is streaming into the room from the Jellyfin server <paramref name="serverId"/>: it keeps the playlist from now on. Someone else's playlist is taken over as it
    /// is, except that others may not add until this streamer says so.
    /// </summary>
    internal void StartHostingPlaylist(ChatRoom room, string serverId)
    {
        var now = GetPlaylist(room);
        room.HostedServerId = serverId;
        StoredPlaylist? held;
        lock (room.PlaylistGate) held = room.PlaylistState;
        if (held == null || held.From != Id.Value || held.ServerId != serverId)
            PublishPlaylist(room, now.Items, othersMayAdd: held?.From == Id.Value && now.OthersMayAdd, serverId);
    }

    internal void StopHostingPlaylist(ChatRoom room) => room.HostedServerId = null;

    private string RequireHosting(ChatRoom room) =>
        room.HostedServerId ?? throw new InvalidOperationException("Only the person who is streaming can change the playlist.");

    private void PublishPlaylist(ChatRoom room, IReadOnlyList<PlaylistItem> items, bool othersMayAdd, string serverId)
    {
        long version;
        lock (room.PlaylistGate) version = Math.Max(NextTimestamp(), (room.PlaylistState?.Version ?? 0) + 1);
        var state = SignedPlaylist.Create(_identity, room.Key, DisplayName, serverId, version, othersMayAdd, items.ToArray());
        lock (room.PlaylistGate) room.PlaylistState = state;
        SavePlaylist(room);
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Playlist, Room = RefTo(room, member), Pls = new[] { state } });
        room.RaisePlaylistChanged();
    }

    private PlaylistItem MakeItem(PlaylistDraft draft, string serverId, string addedBy, string addedByName) =>
        new(PlaylistRules.NewId(), serverId, draft.ItemId, PlaylistRules.Clean(draft.Title, PlaylistLimits.MaxTitle), PlaylistRules.Clean(draft.Subtitle, PlaylistLimits.MaxSubtitle), draft.Kind,
            draft.RuntimeTicks, draft.SeriesId, addedBy, PlaylistRules.Clean(addedByName, PlaylistLimits.MaxName), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal PlaylistItem AddToPlaylist(ChatRoom room, PlaylistDraft draft)
    {
        var server = RequireHosting(room);
        if (!PlaylistRules.IsValidDraft(draft)) throw new ArgumentException("That cannot be put in the playlist.");
        var now = GetPlaylist(room);
        if (now.Items.Count >= PlaylistLimits.MaxItems) throw new InvalidOperationException($"The playlist holds at most {PlaylistLimits.MaxItems} items.");
        var item = MakeItem(draft, server, AccountId.Value, DisplayName);
        PublishPlaylist(room, now.Items.Append(item).ToList(), now.OthersMayAdd, server);
        return item;
    }

    internal void RemoveFromPlaylist(ChatRoom room, string entryId)
    {
        var server = RequireHosting(room);
        var now = GetPlaylist(room);
        if (now.Items.All(i => i.Id != entryId)) return;
        PublishPlaylist(room, now.Items.Where(i => i.Id != entryId).ToList(), now.OthersMayAdd, server);
    }

    internal void MoveInPlaylist(ChatRoom room, string entryId, bool up)
    {
        var server = RequireHosting(room);
        var items = GetPlaylist(room).Items.ToList();
        int at = items.FindIndex(i => i.Id == entryId), to = up ? at - 1 : at + 1;
        if (at < 0 || to < 0 || to >= items.Count) return;
        (items[at], items[to]) = (items[to], items[at]);
        PublishPlaylist(room, items, GetPlaylist(room).OthersMayAdd, server);
    }

    internal void ClearPlaylist(ChatRoom room)
    {
        var server = RequireHosting(room);
        var now = GetPlaylist(room);
        if (now.Items.Count == 0) return;
        PublishPlaylist(room, Array.Empty<PlaylistItem>(), now.OthersMayAdd, server);
    }

    internal void SetOthersMayAdd(ChatRoom room, bool allowed)
    {
        var server = RequireHosting(room);
        var now = GetPlaylist(room);
        if (now.OthersMayAdd == allowed && now.ServerId == server) return;
        PublishPlaylist(room, now.Items, allowed, server);
    }

    // ---- taking in the streamer's list, from the streamer or from a member who passes it on ----

    private void SendPlaylistTo(ChatRoom room, PeerState peer)
    {
        StoredPlaylist? state;
        lock (room.PlaylistGate) state = room.PlaylistState;
        if (state != null) Send(peer.Connection, new Wire { Type = Wire.Playlist, Room = RefTo(room, peer), Pls = new[] { state } });
    }

    private void OnPlaylist(PeerState peer, ChatRoom room, StoredPlaylist[] states)
    {
        foreach (var s in states.Take(2))
        {
            if (s is null || !SignedPlaylist.Verify(s, room.Key) || !IsValidPeerId(s.From)) continue;
            var author = new PeerId(s.From);
            if (IsBannedIn(room, AccountOf(author)) || IsBlocked(author)) continue;
            // The one who is streaming here keeps the list; it is not replaced by someone else's.
            if (room.HostedServerId != null && s.From != Id.Value) continue;
            lock (room.PlaylistGate)
            {
                if (room.PlaylistState is { } have && have.Version >= s.Version) continue;
                room.PlaylistState = s;
            }
            SavePlaylist(room);
            Relay(peer, room, s.From, reference => new Wire { Type = Wire.Playlist, Room = reference, Pls = new[] { s } });
            room.RaisePlaylistChanged();
        }
    }

    // ---- everyone else's side: requests to the streamer ----

    private bool FirstTimeAsk(string id)
    {
        if (_seenAsks.Count > 4000) _seenAsks.Clear();
        return _seenAsks.TryAdd(id, Environment.TickCount64);
    }

    // Towards a member: straight to them when they are one of ours, otherwise to everyone else we are connected to, a few steps at most.
    private void RouteToward(ChatRoom room, string to, PeerState? from, Func<string, Wire> build)
    {
        if (IsValidPeerId(to) && room.MemberStates.TryGetValue(new PeerId(to), out var target))
        {
            Send(target.Connection, build(RefTo(room, target)));
            return;
        }
        foreach (var member in room.MemberStates.Values)
        {
            if (from != null && member.Id == from.Id) continue;
            Send(member.Connection, build(RefTo(room, member)));
        }
    }

    private StoredAsk MakeAsk(ChatRoom room, PeerId host, string op, string body)
    {
        if (body.Length > PlaylistLimits.MaxBody) throw new ArgumentException("That request is too long.");
        return SignedAsk.Create(_identity, room.Key, host.Value, PlaylistRules.NewId() + PlaylistRules.NewId(), op, DisplayName, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void SendAsk(ChatRoom room, StoredAsk ask)
    {
        if (!room.MemberStates.Any()) throw new InvalidOperationException("Nobody else in the room is connected to you now.");
        FirstTimeAsk(ask.Id);
        RouteToward(room, ask.To, null, reference => new Wire { Type = Wire.PlaylistAsk, Room = reference, Ask = ask, To = ask.To, Ttl = AskHops });
    }

    /// <summary>Asks the streamer to add something. Whether it was added shows in the playlist; a streamer who does not allow it ignores it.</summary>
    internal void AskPlaylistAdd(ChatRoom room, PeerId host, PlaylistDraft draft) =>
        SendAsk(room, MakeAsk(room, host, "add", JsonSerializer.Serialize(draft, Json)));

    /// <summary>Asks the streamer to remove something this person added.</summary>
    internal void AskPlaylistRemove(ChatRoom room, PeerId host, string entryId) => SendAsk(room, MakeAsk(room, host, "remove", entryId));

    /// <summary>Asks the streamer's app a question about their library; the answer is whatever it sent back.</summary>
    internal async Task<string> BrowsePlaylistAsync(ChatRoom room, PeerId host, string body, CancellationToken ct)
    {
        var ask = MakeAsk(room, host, "browse", body);
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (room.PlaylistAnswers.Count > 50) room.PlaylistAnswers.Clear();
        room.PlaylistAnswers[ask.Id] = waiting;
        try
        {
            SendAsk(room, ask);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            await using var _ = timeout.Token.Register(() => waiting.TrySetCanceled());
            return await waiting.Task.ConfigureAwait(false);
        }
        finally { room.PlaylistAnswers.TryRemove(ask.Id, out _); }
    }

    private void OnAsk(PeerState peer, ChatRoom room, Wire wire)
    {
        var ask = wire.Ask;
        if (ask is null || ask.To is null || ask.Id is null) return;
        if (ask.To != Id.Value)
        {
            // Not for us: passed on towards who it is for, once.
            if (wire.Ttl > 1 && FirstTimeAsk(ask.Id)) RouteToward(room, ask.To, peer, reference => new Wire { Type = Wire.PlaylistAsk, Room = reference, Ask = ask, To = ask.To, Ttl = wire.Ttl - 1 });
            return;
        }
        if (room.HostedServerId is not { } server || !FirstTimeAsk(ask.Id)) return;
        if (!SignedAsk.Verify(ask, room.Key, out var account, out var device) || IsBannedIn(room, account) || IsBlocked(device)) return;
        var now = GetPlaylist(room);
        switch (ask.Op)
        {
            case "add" when now.OthersMayAdd && now.Items.Count < PlaylistLimits.MaxItems:
                PlaylistDraft? draft;
                try { draft = JsonSerializer.Deserialize<PlaylistDraft>(ask.Body, Json); }
                catch (JsonException) { return; }
                if (draft is null || !PlaylistRules.IsValidDraft(draft)) return;
                PublishPlaylist(room, now.Items.Append(MakeItem(draft, server, account.Value, ask.Name.Length > 0 ? ask.Name : "Someone")).ToList(), now.OthersMayAdd, server);
                break;

            case "remove":
                // Only what the person added themselves (the streamer removes any, with RemoveFromPlaylist).
                if (now.Items.Any(i => i.Id == ask.Body && i.AddedBy == account.Value))
                    PublishPlaylist(room, now.Items.Where(i => i.Id != ask.Body).ToList(), now.OthersMayAdd, server);
                break;

            case "browse":
                var answerTo = device.Value;
                var asked = ask.Id;
                if (!now.OthersMayAdd) { SendPlaylistAnswer(room, answerTo, asked, "{\"error\":\"The streamer does not let others add to the playlist.\"}"); break; }
                room.RaisePlaylistBrowse(new PlaylistBrowseRequest(ask.Body, ask.Name, answer => SendPlaylistAnswer(room, answerTo, asked, answer)));
                break;
        }
    }

    private void SendPlaylistAnswer(ChatRoom room, string to, string id, string text) =>
        RouteToward(room, to, null, reference => new Wire { Type = Wire.PlaylistAnswer, Room = reference, To = to, Id = id, Text = text, Ttl = AskHops });

    private void OnPlaylistAnswer(PeerState peer, ChatRoom room, Wire wire)
    {
        if (wire.To is null || wire.Id is null || wire.Text is null) return;
        if (wire.To == Id.Value)
        {
            if (room.PlaylistAnswers.TryRemove(wire.Id, out var waiting)) waiting.TrySetResult(wire.Text);
            return;
        }
        if (wire.Ttl > 1) RouteToward(room, wire.To, peer, reference => new Wire { Type = Wire.PlaylistAnswer, Room = reference, To = wire.To, Id = wire.Id, Text = wire.Text, Ttl = wire.Ttl - 1 });
    }
}
