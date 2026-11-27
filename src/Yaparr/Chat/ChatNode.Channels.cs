using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

// A room's channels. Text and voice channels are signed statements by the owner and the admins, passed around the room like the
// admin list; every member sees all of them, and anyone can use any of them. A channel is organisation, not a lock: everyone in
// the room still gets every message, whatever its channel.
public sealed partial class ChatNode
{
    private const int MaxChannelEntries = 256, ChannelBatch = 32;

    private static string ChannelsFile(string directory, ChatRoom room) => Path.Combine(directory, room.Key + ".channels.json");

    private void LoadChannels(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory || room.Owner == null) return;
        try
        {
            var path = ChannelsFile(directory, room);
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<StoredChannel[]>(File.ReadAllBytes(path), Json) ?? Array.Empty<StoredChannel>();
            lock (room.ChannelGate)
                foreach (var c in stored)
                    if (SignedChannel.Verify(c, room.Key, out var signer) && (!room.ChannelEntries.TryGetValue(c.Id + ":" + signer.Value, out var old) || old.Ts < c.Ts))
                        room.ChannelEntries[c.Id + ":" + signer.Value] = c;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not read the room's channels: " + ex.Message);
        }
    }

    private void SaveChannels(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory) return;
        try
        {
            StoredChannel[] all;
            lock (room.ChannelGate) all = room.ChannelEntries.Values.ToArray();
            Directory.CreateDirectory(directory);
            var path = ChannelsFile(directory, room);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(all, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the room's channels: " + ex.Message);
        }
    }

    private static ChannelInfo DefaultText() => new(RoomChannels.DefaultText, "general", ChannelKind.Text, 0, IsDefault: true);
    private static ChannelInfo DefaultVoice() => new(RoomChannels.DefaultVoice, "Voice", ChannelKind.Voice, 1, IsDefault: true);

    // The channels as the statements of current admins add up: the newest word on each channel wins.
    internal IReadOnlyList<ChannelInfo> GetChannels(ChatRoom room)
    {
        int version = Volatile.Read(ref room.ChannelVersion);
        if (room.ChannelCache is { } cached && room.ChannelCacheVersion == version) return cached;

        var channels = new Dictionary<string, ChannelInfo> { [RoomChannels.DefaultText] = DefaultText(), [RoomChannels.DefaultVoice] = DefaultVoice() };
        if (room.Owner != null)
        {
            StoredChannel[] all;
            lock (room.ChannelGate) all = room.ChannelEntries.Values.ToArray();
            var roles = new Dictionary<string, bool>();
            bool IsAdmin(StoredChannel c)
            {
                var acct = c.Acct.Length == 0 ? Convert.FromBase64String(c.Pub) : Convert.FromBase64String(c.Acct);
                var id = PeerId.FromPublicKey(acct);
                if (!roles.TryGetValue(id.Value, out var admin)) roles[id.Value] = admin = RoleIn(room, id) >= RoomRole.Admin;
                return admin;
            }
            foreach (var group in all.Where(IsAdmin).GroupBy(c => c.Id))
            {
                var word = group.OrderByDescending(c => c.Ts).First();
                bool isDefault = RoomChannels.IsDefault(word.Id);
                if (word.Deleted && !isDefault) { channels.Remove(word.Id); continue; }
                // A default channel keeps its kind and place whatever a statement says; only its name can change.
                if (isDefault) channels[word.Id] = channels[word.Id] with { Name = word.Deleted ? channels[word.Id].Name : word.Name };
                else channels[word.Id] = new ChannelInfo(word.Id, word.Name, word.Kind == "voice" ? ChannelKind.Voice : ChannelKind.Text, word.Order, IsDefault: false, word.Category ?? "");
            }
        }
        var list = channels.Values.OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(RoomChannels.MaxChannels).ToList();
        room.ChannelCache = list;
        room.ChannelCacheVersion = version;
        return list;
    }

    internal ChannelInfo? FindChannel(ChatRoom room, string id) => GetChannels(room).FirstOrDefault(c => c.Id == id);

    internal ChannelInfo CreateChannel(ChatRoom room, string name, ChannelKind kind, string category = "")
    {
        RequireAdmin(room);
        name = CleanChannelName(name);
        var existing = GetChannels(room);
        category = CanonicalCategory(existing, category);
        if (existing.Count >= RoomChannels.MaxChannels) throw new InvalidOperationException($"A room can have at most {RoomChannels.MaxChannels} channels.");
        if (existing.Any(c => c.Kind == kind && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("There is already a channel with that name.");
        var id = SignedChannel.NewId();
        PublishChannel(room, id, name, kind, order: OrderAtEndOf(existing, category), deleted: false, category);
        return FindChannel(room, id)!;
    }

    internal void RenameChannel(ChatRoom room, string id, string name)
    {
        RequireAdmin(room);
        name = CleanChannelName(name);
        var channel = FindChannel(room, id) ?? throw new KeyNotFoundException("There is no such channel.");
        if (GetChannels(room).Any(c => c.Id != id && c.Kind == channel.Kind && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("There is already a channel with that name.");
        PublishChannel(room, id, name, channel.Kind, channel.Order, deleted: false, channel.Category);
    }

    internal void DeleteChannel(ChatRoom room, string id)
    {
        RequireAdmin(room);
        var channel = FindChannel(room, id) ?? throw new KeyNotFoundException("There is no such channel.");
        if (channel.IsDefault) throw new InvalidOperationException("The first text channel and the first voice channel are always there.");
        PublishChannel(room, id, channel.Name, channel.Kind, channel.Order, deleted: true, channel.Category);
    }

    // A channel moves up or down among the channels of its kind. Every statement already carries the place of its channel, and every version
    // sorts by it, so a move is the same kind of statement as a rename. The first text channel and the first voice channel keep their places
    // (they stay first), and the places of the others are handed out again in the new order, so that two that were equal (two admins at
    // once) are told apart. Only a channel whose place changed gets a new statement.
    internal void MoveChannel(ChatRoom room, string id, bool up)
    {
        RequireAdmin(room);
        var channel = FindChannel(room, id) ?? throw new KeyNotFoundException("There is no such channel.");
        if (channel.IsDefault) throw new InvalidOperationException("The first text channel and the first voice channel stay where they are.");
        var sameKind = GetChannels(room).Where(c => c.Kind == channel.Kind && !c.IsDefault && string.Equals(c.Category, channel.Category, StringComparison.CurrentCultureIgnoreCase)).ToList();
        int at = sameKind.FindIndex(c => c.Id == id), to = up ? at - 1 : at + 1;
        if (to < 0 || to >= sameKind.Count) return;

        int first = Math.Max(2, sameKind.Min(c => c.Order));
        var wanted = sameKind.ToList();
        (wanted[at], wanted[to]) = (wanted[to], wanted[at]);
        for (int i = 0; i < wanted.Count; i++)
            if (wanted[i].Order != first + i)
                PublishChannel(room, wanted[i].Id, wanted[i].Name, wanted[i].Kind, first + i, deleted: false, wanted[i].Category);
    }

    // ---- categories: a name the admins give to a group of channels. A category is nothing but that name on its channels (it is there while a channel is in it),
    // so making, renaming and removing one are statements about the channels in it. Categories show in the order of their first channel; the channels outside
    // any category come first. The first text and voice channels are never in one. ----

    // The spelling the room already uses for this name (a name is the same category whatever its capitals); the name cleaned up when it is new.
    private static string CanonicalCategory(IReadOnlyList<ChannelInfo> channels, string? name)
    {
        var clean = RoomChannels.CleanCategory(name);
        return channels.FirstOrDefault(c => string.Equals(c.Category, clean, StringComparison.CurrentCultureIgnoreCase))?.Category ?? clean;
    }

    // A place after the last channel of the category (after the last channel at all for one that is new), so that putting a channel in a category
    // neither moves the category nor any other.
    private static int OrderAtEndOf(IReadOnlyList<ChannelInfo> channels, string category)
    {
        var inCategory = category.Length == 0 ? channels : channels.Where(c => string.Equals(c.Category, category, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return (inCategory.Count > 0 ? inCategory : channels).Max(c => c.Order) + 1;
    }

    internal IReadOnlyList<string> GetCategories(ChatRoom room) => RoomChannels.CategoriesOf(GetChannels(room));

    /// <summary>Puts a channel in a category (made if it is new), or out of any (an empty name).</summary>
    internal void SetChannelCategory(ChatRoom room, string id, string? category)
    {
        RequireAdmin(room);
        var all = GetChannels(room);
        var channel = all.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("There is no such channel.");
        if (channel.IsDefault) throw new InvalidOperationException("The first text channel and the first voice channel stay at the top, outside any category.");
        var name = CanonicalCategory(all, category);
        if (string.Equals(name, channel.Category, StringComparison.CurrentCultureIgnoreCase) && name == channel.Category) return;
        PublishChannel(room, id, channel.Name, channel.Kind, name.Length == 0 ? channel.Order : OrderAtEndOf(all.Where(c => c.Id != id).ToList(), name), deleted: false, name);
    }

    internal void RenameCategory(ChatRoom room, string oldName, string newName)
    {
        RequireAdmin(room);
        var all = GetChannels(room);
        var members = all.Where(c => string.Equals(c.Category, oldName, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (members.Count == 0) throw new KeyNotFoundException("There is no such category.");
        var clean = RoomChannels.CleanCategory(newName);
        if (clean.Length == 0) throw new ArgumentException("A category needs a name.");
        if (all.Any(c => string.Equals(c.Category, clean, StringComparison.CurrentCultureIgnoreCase) && !string.Equals(c.Category, oldName, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("There is already a category with that name.");
        foreach (var c in members.Where(c => c.Category != clean))
            PublishChannel(room, c.Id, c.Name, c.Kind, c.Order, deleted: false, clean);
    }

    /// <summary>Takes a category away; its channels stay, outside any category.</summary>
    internal void DeleteCategory(ChatRoom room, string name)
    {
        RequireAdmin(room);
        var members = GetChannels(room).Where(c => string.Equals(c.Category, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (members.Count == 0) throw new KeyNotFoundException("There is no such category.");
        foreach (var c in members) PublishChannel(room, c.Id, c.Name, c.Kind, c.Order, deleted: false, "");
    }

    // The places of all the channels are handed out again so that each category is one run, in the new order of the categories; only a channel whose place
    // changed gets a new statement.
    internal void MoveCategory(ChatRoom room, string name, bool up)
    {
        RequireAdmin(room);
        var all = GetChannels(room);
        var order = RoomChannels.CategoriesOf(all);
        int at = order.FindIndex(n => string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase)), to = up ? at - 1 : at + 1;
        if (at < 0) throw new KeyNotFoundException("There is no such category.");
        if (to < 0 || to >= order.Count) return;
        (order[at], order[to]) = (order[to], order[at]);
        var loose = all.Where(c => !c.IsDefault && c.Category.Length == 0).OrderBy(c => c.Order);
        var sequence = loose.Concat(order.SelectMany(n => all.Where(c => !c.IsDefault && string.Equals(c.Category, n, StringComparison.CurrentCultureIgnoreCase)).OrderBy(c => c.Order))).ToList();
        for (int i = 0; i < sequence.Count; i++)
            if (sequence[i].Order != 2 + i)
                PublishChannel(room, sequence[i].Id, sequence[i].Name, sequence[i].Kind, 2 + i, deleted: false, sequence[i].Category);
    }

    // The voice channel you are in is gone (removed here or by another admin, or its admin lost the role): you leave it, instead of the
    // session going on in a channel that no longer exists.
    internal void LeaveRemovedVoiceChannel(ChatRoom room)
    {
        if (room.ActiveVoice is not { } session) return;
        if (FindChannel(room, session.ChannelId) is { Kind: ChannelKind.Voice }) return;
        _options.Log?.Invoke($"the voice channel you were in, in '{room.Name}', was removed; leaving it");
        session.Leave();
        room.RaiseVoiceChannelRemoved(session.ChannelId);
    }

    private static string CleanChannelName(string name)
    {
        name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new ArgumentException("A channel needs a name.");
        return name.Length <= 64 ? name : name[..64];
    }

    private void PublishChannel(ChatRoom room, string id, string name, ChannelKind kind, int order, bool deleted, string category = "")
    {
        long ts;
        lock (room.ChannelGate)
        {
            if (room.ChannelEntries.Count >= MaxChannelEntries && !room.ChannelEntries.ContainsKey(id + ":" + AccountId.Value))
                throw new InvalidOperationException("This room has as many channel changes as it can keep track of.");
            long seen = room.ChannelEntries.Values.Where(c => c.Id == id).Select(c => c.Ts).DefaultIfEmpty(0).Max();
            ts = Math.Max(NextTimestamp(), seen + 1);
        }
        var entry = SignedChannel.Create(_identity, room.Key, id, name, kind, order, deleted, ts, category);
        lock (room.ChannelGate) room.ChannelEntries[id + ":" + AccountId.Value] = entry;
        Interlocked.Increment(ref room.ChannelVersion);
        SaveChannels(room);
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Channels, Room = RefTo(room, member), Chn = new[] { entry } });
        room.RaiseChannelsChanged();
    }

    private void SendChannelsTo(ChatRoom room, PeerState peer)
    {
        StoredChannel[] all;
        lock (room.ChannelGate) all = room.ChannelEntries.Values.ToArray();
        for (int i = 0; i < all.Length; i += ChannelBatch)
            Send(peer.Connection, new Wire { Type = Wire.Channels, Room = RefTo(room, peer), Chn = all.Skip(i).Take(ChannelBatch).ToArray() });
    }

    private void OnChannels(PeerState peer, ChatRoom room, StoredChannel[] channels)
    {
        if (room.Owner == null) return;
        var accepted = new List<StoredChannel>();
        foreach (var c in channels.Take(ChannelBatch))
        {
            if (c.Pub is null || c.Acct is null || c.Id is null) continue;
            PeerId guess;
            try { guess = PeerId.FromPublicKey(c.Acct.Length == 0 ? Convert.FromBase64String(c.Pub) : Convert.FromBase64String(c.Acct)); }
            catch (FormatException) { continue; }
            // Skipped before the signature is checked, which is what costs: people who are no admin, ones we have, a full list.
            if (RoleIn(room, guess) < RoomRole.Admin) continue;
            var slot = c.Id + ":" + guess.Value;
            lock (room.ChannelGate)
            {
                if (room.ChannelEntries.TryGetValue(slot, out var have) && have.Ts >= c.Ts) continue;
                if (have == null && room.ChannelEntries.Count >= MaxChannelEntries) continue;
            }
            if (!SignedChannel.Verify(c, room.Key, out var signer) || signer != guess) continue;
            lock (room.ChannelGate)
            {
                if (room.ChannelEntries.TryGetValue(slot, out var now) && now.Ts >= c.Ts) continue;
                room.ChannelEntries[slot] = c;
            }
            accepted.Add(c);
        }
        if (accepted.Count == 0) return;
        Interlocked.Increment(ref room.ChannelVersion);
        SaveChannels(room);
        foreach (var c in accepted)
            Relay(peer, room, PeerId.FromPublicKey(Convert.FromBase64String(c.Pub)).Value, reference => new Wire { Type = Wire.Channels, Room = reference, Chn = new[] { c } });
        room.RaiseChannelsChanged();
    }
}
