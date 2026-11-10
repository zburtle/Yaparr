using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

// Bans and deletions by the owner and admins. They are signed orders passed around the room like the admin list; every client
// checks the signature, checks that the signer is an owner or admin right now, and applies the same rules, so they all end
// up in the same place without a server. A ban is a promise of every honest client to ignore the person, not a lock: the
// banned person still has the room key (see the backlog on rotating it).
public sealed partial class ChatNode
{
    private const int MaxModEntries = 1024, ModBatch = 64, ModReservedForAdmins = 256, MaxOwnDeletesPerMember = 64;

    private static string ModFile(string directory, ChatRoom room) => Path.Combine(directory, room.Key + ".mod.json");

    private void LoadMods(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory || room.Owner == null) return;
        try
        {
            var path = ModFile(directory, room);
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<StoredMod[]>(File.ReadAllBytes(path), Json) ?? Array.Empty<StoredMod>();
            lock (room.ModGate)
                foreach (var m in stored)
                    if (SignedMod.Verify(m, room.Key, out var signer) && (!room.ModEntries.TryGetValue(SignedMod.SlotOf(m, signer), out var old) || old.Ts < m.Ts))
                        room.ModEntries[SignedMod.SlotOf(m, signer)] = m;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not read the room's bans: " + ex.Message);
        }
    }

    private void SaveMods(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory) return;
        try
        {
            StoredMod[] all;
            lock (room.ModGate) all = room.ModEntries.Values.ToArray();
            Directory.CreateDirectory(directory);
            var path = ModFile(directory, room);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(all, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the room's bans: " + ex.Message);
        }
    }

    // A deleted message that is older than the history we keep cannot come back, so its order is not worth keeping.
    private void PruneMods(ChatRoom room)
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(_options.MaxHistoryAge).ToUnixTimeMilliseconds();
        lock (room.ModGate)
            foreach (var k in room.ModEntries.Where(kv => kv.Value.Kind == SignedMod.DeleteMessage && kv.Value.Ts < cutoff).Select(kv => kv.Key).ToList())
                room.ModEntries.Remove(k);
    }

    // ---- what the orders add up to --------------------------------------------------------------

    internal ModView Moderation(ChatRoom room)
    {
        int version = Volatile.Read(ref room.ModVersion);
        if (room.ModCache is { } cached && room.ModCacheVersion == version) return cached;
        var view = BuildView(room);
        room.ModCache = view;
        room.ModCacheVersion = version;
        return view;
    }

    private ModView BuildView(ChatRoom room)
    {
        var view = new ModView();
        if (room.Owner == null) return view;
        StoredMod[] all;
        lock (room.ModGate) all = room.ModEntries.Values.ToArray();

        var roles = new Dictionary<string, RoomRole>();
        RoomRole SignerRole(StoredMod m)
        {
            var acct = m.Acct.Length == 0 ? Convert.FromBase64String(m.Pub) : Convert.FromBase64String(m.Acct);
            var id = PeerId.FromPublicKey(acct);
            if (!roles.TryGetValue(id.Value, out var role)) roles[id.Value] = role = RoleIn(room, id);
            return role;
        }

        foreach (var group in all.Where(m => m.Kind is SignedMod.Ban or SignedMod.Unban).GroupBy(m => m.Target))
        {
            // An admin's word does not reach another admin or the owner; the owner's word on a person beats any admin's.
            var targetRole = RoleIn(room, new PeerId(group.Key));
            var valid = group.Select(m => (Mod: m, Role: SignerRole(m)))
                .Where(x => x.Role == RoomRole.Owner || (x.Role == RoomRole.Admin && targetRole == RoomRole.Member)).ToList();
            var word = valid.Where(x => x.Role == RoomRole.Owner).OrderByDescending(x => x.Mod.Ts).Select(x => x.Mod).FirstOrDefault()
                       ?? valid.OrderByDescending(x => x.Mod.Ts).Select(x => x.Mod).FirstOrDefault();
            if (word is { Kind: SignedMod.Ban } && targetRole == RoomRole.Member) view.Banned[group.Key] = word.Label;
        }
        foreach (var m in all)
        {
            if (m.Kind == SignedMod.DeleteMessage && SignerRole(m) < RoomRole.Admin)
            {
                // Not an admin's order: it counts for the signer's own messages only.
                var acct = PeerId.FromPublicKey(m.Acct.Length == 0 ? Convert.FromBase64String(m.Pub) : Convert.FromBase64String(m.Acct));
                if (!view.OwnDeletes.TryGetValue(m.Target, out var signers)) view.OwnDeletes[m.Target] = signers = new HashSet<string>();
                signers.Add(acct.Value);
                continue;
            }
            if (m.Kind is not (SignedMod.DeleteMessage or SignedMod.DeleteSound or SignedMod.DeleteGif) || SignerRole(m) < RoomRole.Admin) continue;
            (m.Kind == SignedMod.DeleteMessage ? view.Messages : m.Kind == SignedMod.DeleteGif ? view.Gifs : view.Sounds).Add(m.Target);
        }
        foreach (var m in all)
        {
            if (m.Kind != SignedMod.RenameSound || m.Label.Length == 0 || SignerRole(m) < RoomRole.Admin) continue;
            if (!view.SoundNames.TryGetValue(m.Target, out var have) || have.Ts < m.Ts) view.SoundNames[m.Target] = (m.Label, m.Ts);
        }
        return view;
    }

    internal bool IsBannedIn(ChatRoom room, PeerId account)
    {
        var banned = Moderation(room).Banned;
        return banned.Count > 0 && (banned.ContainsKey(account.Value) || banned.ContainsKey(AccountOf(account).Value));
    }

    internal IReadOnlyList<BannedPerson> GetBanned(ChatRoom room) =>
        Moderation(room).Banned.Select(kv => new BannedPerson(new PeerId(kv.Key), kv.Value)).OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    internal bool IsMessageDeleted(ChatRoom room, StoredMessage m)
    {
        var view = Moderation(room);
        if (view.Messages.Count == 0 && view.OwnDeletes.Count == 0) return false;
        var key = SignedMessage.Key(m);
        if (view.Messages.Contains(key)) return true;
        // A member's own order: it only counts if the message is theirs, one of their account's devices sent it.
        return view.OwnDeletes.TryGetValue(key, out var signers) && signers.Contains(AccountOf(new PeerId(m.From)).Value);
    }

    internal bool IsSoundDeleted(ChatRoom room, string id) => Moderation(room).Sounds.Contains(id);

    internal bool IsGifDeleted(ChatRoom room, string id) => Moderation(room).Gifs.Contains(id);

    // ---- giving orders --------------------------------------------------------------------------

    private RoomRole RequireAdmin(ChatRoom room)
    {
        if (room.Owner == null) throw new InvalidOperationException("This room has no owner, so it has no admins. Rooms made with this version do.");
        var role = RoleIn(room, AccountId);
        if (role < RoomRole.Admin) throw new InvalidOperationException("Only the owner and the admins of the room can do that.");
        return role;
    }

    internal void Ban(ChatRoom room, PeerId account, string name)
    {
        var mine = RequireAdmin(room);
        if (!IsValidPeerId(account.Value)) throw new ArgumentException("Not an account id.", nameof(account));
        if (IsMyDevice(account)) throw new InvalidOperationException("You cannot ban yourself.");
        var theirs = RoleIn(room, account);
        if (theirs == RoomRole.Owner) throw new InvalidOperationException("The owner cannot be banned.");
        if (theirs == RoomRole.Admin && mine != RoomRole.Owner) throw new InvalidOperationException("Only the owner can ban an admin.");
        if (theirs == RoomRole.Admin) throw new InvalidOperationException("Remove them as an admin first, then ban them.");
        PublishMod(room, SignedMod.Ban, account.Value, string.IsNullOrWhiteSpace(name) ? account.Short : name.Trim());
    }

    internal void Unban(ChatRoom room, PeerId account)
    {
        RequireAdmin(room);
        var banned = Moderation(room).Banned;
        PublishMod(room, SignedMod.Unban, account.Value, banned.TryGetValue(account.Value, out var label) ? label : account.Short);
    }

    internal void DeleteMessage(ChatRoom room, ChatMessage message) => DeleteMessage(room, SignedMessage.KeyOf(message.From.Id.Value, message.Id));

    // By key (ChatMessage.Key), which is all an order to delete needs: a message you have just sent is on screen before it has come back as a ChatMessage.
    internal void DeleteMessage(ChatRoom room, string key)
    {
        if (room.Owner == null) throw new InvalidOperationException("This room has no owner, so messages in it cannot be deleted. Rooms made with this version can.");
        // An owner or admin deletes any message; anyone else only their own (sent from this device or another of their account).
        if (RoleIn(room, AccountId) < RoomRole.Admin && !MessageIsMine(room, key))
            throw new InvalidOperationException("Only the owner and the admins of the room can delete other people's messages.");
        PublishMod(room, SignedMod.DeleteMessage, key, "");
    }

    private bool MessageIsMine(ChatRoom room, string key) =>
        room.History.Snapshot().Any(m => SignedMessage.Key(m) == key && IsMyDevice(new PeerId(m.From)));

    internal void DeleteSound(ChatRoom room, string id)
    {
        RequireAdmin(room);
        bool known;
        lock (room.SoundGate) known = room.SoundEntries.ContainsKey(id);
        if (!known) throw new KeyNotFoundException("There is no such sound.");
        PublishMod(room, SignedMod.DeleteSound, id, "");
    }

    // An owner or admin gives a sound that someone else added another name. The sound itself, signed by whoever added it, is not touched: the
    // order is read next to it, and whichever is newer (this, or its owner's own rename) decides the name.
    internal void RenameSoundAsAdmin(ChatRoom room, string id, string name)
    {
        RequireAdmin(room);
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A sound needs a name.");
        if (name.Length > 64) throw new ArgumentException("That name is too long.");
        StoredSound? s;
        lock (room.SoundGate) room.SoundEntries.TryGetValue(id, out s);
        if (s == null || s.Deleted) throw new KeyNotFoundException("There is no such sound.");
        PublishMod(room, SignedMod.RenameSound, id, name);
    }

    internal void DeleteGif(ChatRoom room, string id)
    {
        RequireAdmin(room);
        bool known;
        lock (room.GifGate) known = room.GifEntries.ContainsKey(id);
        if (!known) throw new KeyNotFoundException("There is no such picture on the shelf.");
        PublishMod(room, SignedMod.DeleteGif, id, "");
    }

    private void PublishMod(ChatRoom room, string kind, string target, string label)
    {
        PruneMods(room);
        // Later than every order seen about the same thing, whatever this device's clock says.
        long ts;
        lock (room.ModGate)
        {
            if (room.ModEntries.Count >= MaxModEntries) throw new InvalidOperationException("This room has as many bans and deletions as it can keep track of.");
            long seen = room.ModEntries.Values.Where(m => m.Target == target).Select(m => m.Ts).DefaultIfEmpty(0).Max();
            ts = Math.Max(NextTimestamp(), seen + 1);
        }
        var entry = SignedMod.Create(_identity, room.Key, kind, target, label, ts);
        lock (room.ModGate) room.ModEntries[SignedMod.SlotOf(entry, AccountId)] = entry;
        Interlocked.Increment(ref room.ModVersion);
        SaveMods(room);
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Moderate, Room = RefTo(room, member), Mod = new[] { entry } });
        ApplyModeration(room);
    }

    private void SendModsTo(ChatRoom room, PeerState peer)
    {
        StoredMod[] all;
        lock (room.ModGate) all = room.ModEntries.Values.ToArray();
        for (int i = 0; i < all.Length; i += ModBatch)
            Send(peer.Connection, new Wire { Type = Wire.Moderate, Room = RefTo(room, peer), Mod = all.Skip(i).Take(ModBatch).ToArray() });
    }

    private void OnMods(PeerState peer, ChatRoom room, StoredMod[] mods)
    {
        if (room.Owner == null) return;
        var accepted = new List<StoredMod>();
        foreach (var m in mods.Take(ModBatch))
        {
            if (m.Pub is null || m.Acct is null) continue;
            PeerId guess;
            try { guess = PeerId.FromPublicKey(m.Acct.Length == 0 ? Convert.FromBase64String(m.Pub) : Convert.FromBase64String(m.Acct)); }
            catch (FormatException) { continue; }
            // Skipped before the signature is checked, which is what costs: ones we have, ones from people who are no admin, a full list.
            // The one thing a member who is no admin may order is the deletion of a message of their own, so that is let through, within limits: a
            // member can only fill so much of the list (a part is kept for the bans and deletions of admins), and an order about a message we hold
            // that someone else sent is dropped at once.
            bool admin = RoleIn(room, guess) >= RoomRole.Admin;
            if (!admin && (m.Kind != SignedMod.DeleteMessage || m.Target is null || !NotSomeoneElsesMessage(room, m.Target, guess))) continue;
            lock (room.ModGate)
            {
                if (m.Kind is null || m.Target is null) continue;
                if (!admin && (room.ModEntries.Count >= MaxModEntries - ModReservedForAdmins
                               || room.ModEntries.Keys.Count(k => k.EndsWith(":" + guess.Value, StringComparison.Ordinal)) >= MaxOwnDeletesPerMember)) continue;
                if (room.ModEntries.TryGetValue(SignedMod.SlotOf(m, guess), out var have) && have.Ts >= m.Ts) continue;
                if (have == null && room.ModEntries.Count >= MaxModEntries) continue;
            }
            if (!SignedMod.Verify(m, room.Key, out var signer) || signer != guess) continue;
            lock (room.ModGate)
            {
                var slot = SignedMod.SlotOf(m, signer);
                if (room.ModEntries.TryGetValue(slot, out var now) && now.Ts >= m.Ts) continue;
                room.ModEntries[slot] = m;
            }
            accepted.Add(m);
        }
        if (accepted.Count == 0) return;
        Interlocked.Increment(ref room.ModVersion);
        SaveMods(room);
        foreach (var m in accepted)
            Relay(peer, room, PeerId.FromPublicKey(Convert.FromBase64String(m.Pub)).Value, reference => new Wire { Type = Wire.Moderate, Room = reference, Mod = new[] { m } });
        ApplyModeration(room);
    }

    // False when this message is in our history and someone else sent it (so a member's order to delete it is not worth keeping); true when it is
    // theirs or we have not seen it, which is the case for a message that has not reached us yet.
    private bool NotSomeoneElsesMessage(ChatRoom room, string key, PeerId signer)
    {
        foreach (var stored in room.History.Snapshot())
            if (SignedMessage.Key(stored) == key) return AccountOf(new PeerId(stored.From)) == signer;
        return true;
    }

    // Makes the room match the orders: people who are banned leave the member list, voice and anything we would send; people
    // who are no longer banned may come back; deleted messages are forgotten. Also called when the admins change, since an
    // order only counts while the one who gave it is an admin.
    private void ApplyModeration(ChatRoom room)
    {
        Interlocked.Increment(ref room.ModVersion);
        var view = Moderation(room);

        bool membersChanged = false;
        foreach (var member in room.MemberStates.Values.ToList())
            if (IsBannedIn(room, member.Id) && room.MemberStates.TryRemove(member.Id, out _))
            {
                member.ClearLinks(room.Key);
                room.RaiseLeft(member.Info);
                membersChanged = true;
            }

        bool voiceChanged;
        lock (room.IndirectGate)
        {
            var gone = room.Voice.Keys.Where(id => IsBannedIn(room, id)).ToList();
            foreach (var id in gone) room.Voice.Remove(id);
            voiceChanged = gone.Count > 0;
        }

        if (view.Messages.Count > 0) room.History.Remove(view.Messages);

        // Sounds an admin took down no longer need their files.
        if (view.Sounds.Count > 0)
        {
            StoredSound[] sounds;
            lock (room.SoundGate) sounds = room.SoundEntries.Values.ToArray();
            ReleaseSoundFiles(sounds.Where(s => view.Sounds.Contains(s.Id)).Select(s => s.Hash));
        }

        // Someone whose ban was lifted is still connected and still in the room: let them back in.
        foreach (var p in _peers.Values.ToList())
            if (!room.MemberStates.ContainsKey(p.Id) && p.Connection.SessionId != null && p.Declares(RefFrom(room, p))) OnPeerEntersRoom(p, room);

        if (membersChanged) AfterMembershipChange(room); else RefreshIndirect(room);
        if (voiceChanged) VoiceRosterChanged(room);
        room.RaiseSoundsChanged();
        room.RaiseGifsChanged();
        room.RaiseModerationChanged();
    }
}
