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
                else channels[word.Id] = new ChannelInfo(word.Id, word.Name, word.Kind == "voice" ? ChannelKind.Voice : ChannelKind.Text, word.Order, IsDefault: false);
            }
        }
        var list = channels.Values.OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(RoomChannels.MaxChannels).ToList();
        room.ChannelCache = list;
        room.ChannelCacheVersion = version;
        return list;
    }

    internal ChannelInfo? FindChannel(ChatRoom room, string id) => GetChannels(room).FirstOrDefault(c => c.Id == id);

    internal ChannelInfo CreateChannel(ChatRoom room, string name, ChannelKind kind)
    {
        RequireAdmin(room);
        name = CleanChannelName(name);
        var existing = GetChannels(room);
        if (existing.Count >= RoomChannels.MaxChannels) throw new InvalidOperationException($"A room can have at most {RoomChannels.MaxChannels} channels.");
        if (existing.Any(c => c.Kind == kind && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("There is already a channel with that name.");
        var id = SignedChannel.NewId();
        PublishChannel(room, id, name, kind, order: existing.Max(c => c.Order) + 1, deleted: false);
        return FindChannel(room, id)!;
    }

    internal void RenameChannel(ChatRoom room, string id, string name)
    {
        RequireAdmin(room);
        name = CleanChannelName(name);
        var channel = FindChannel(room, id) ?? throw new KeyNotFoundException("There is no such channel.");
        if (GetChannels(room).Any(c => c.Id != id && c.Kind == channel.Kind && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("There is already a channel with that name.");
        PublishChannel(room, id, name, channel.Kind, channel.Order, deleted: false);
    }

    internal void DeleteChannel(ChatRoom room, string id)
    {
        RequireAdmin(room);
        var channel = FindChannel(room, id) ?? throw new KeyNotFoundException("There is no such channel.");
        if (channel.IsDefault) throw new InvalidOperationException("The first text channel and the first voice channel are always there.");
        PublishChannel(room, id, channel.Name, channel.Kind, channel.Order, deleted: true);
    }

    private static string CleanChannelName(string name)
    {
        name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new ArgumentException("A channel needs a name.");
        return name.Length <= 64 ? name : name[..64];
    }

    private void PublishChannel(ChatRoom room, string id, string name, ChannelKind kind, int order, bool deleted)
    {
        long ts;
        lock (room.ChannelGate)
        {
            if (room.ChannelEntries.Count >= MaxChannelEntries && !room.ChannelEntries.ContainsKey(id + ":" + AccountId.Value))
                throw new InvalidOperationException("This room has as many channel changes as it can keep track of.");
            long seen = room.ChannelEntries.Values.Where(c => c.Id == id).Select(c => c.Ts).DefaultIfEmpty(0).Max();
            ts = Math.Max(NextTimestamp(), seen + 1);
        }
        var entry = SignedChannel.Create(_identity, room.Key, id, name, kind, order, deleted, ts);
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
