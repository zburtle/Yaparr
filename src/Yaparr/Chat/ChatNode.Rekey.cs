using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

// Replacing a room's secret. A ban only makes honest apps ignore someone; this takes the room away from them, because they
// never get the new secret and so cannot find the room, answer its members' proofs or read what is sent in it. The owner gives
// the new secret to the people who should have it, and they pass it on to each other over their encrypted connections.
public sealed partial class ChatNode
{
    private const int MaxRoster = 512;
    private static readonly TimeSpan RosterLifetime = TimeSpan.FromDays(365), OldTopicLifetime = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<string, long> _rekeyOffers = new();

    // ---- who has been in the room ----

    private static string RosterFile(string directory, ChatRoom room) => Path.Combine(directory, room.Key + ".roster.json");

    private sealed record RosterRow(string A, string N, long T);

    private void LoadRoster(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory || room.Owner == null) return;
        try
        {
            var path = RosterFile(directory, room);
            if (!File.Exists(path)) return;
            var rows = JsonSerializer.Deserialize<RosterRow[]>(File.ReadAllBytes(path), Json) ?? Array.Empty<RosterRow>();
            lock (room.RosterGate)
                foreach (var row in rows.Take(MaxRoster).Where(r => IsValidPeerId(r.A ?? "") && r.N is { Length: <= 64 }))
                    room.Roster[row.A] = new RosterEntry(row.N, row.T);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not read who has been in the room: " + ex.Message);
        }
    }

    private void SaveRoster(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory || room.Owner == null) return;
        try
        {
            RosterRow[] rows;
            lock (room.RosterGate) { rows = room.Roster.Select(kv => new RosterRow(kv.Key, kv.Value.Name, kv.Value.LastSeen)).ToArray(); room.RosterDirty = false; }
            Directory.CreateDirectory(directory);
            var path = RosterFile(directory, room);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(rows, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save who has been in the room: " + ex.Message);
        }
    }

    // Remembers that this person was here, so a replaced secret can be handed to them even if they are away at the time.
    private void NoteMember(ChatRoom room, PeerId device, string? name)
    {
        if (room.Owner == null) return;
        var account = AccountOf(device);
        if (IsMyDevice(account) || IsMyDevice(device)) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (room.RosterGate)
        {
            string label = string.IsNullOrWhiteSpace(name) ? (room.Roster.TryGetValue(account.Value, out var known) ? known.Name : account.Short) : name.Trim();
            room.Roster[account.Value] = new RosterEntry(label.Length <= 64 ? label : label[..64], now);
            if (room.Roster.Count > MaxRoster)
                foreach (var k in room.Roster.OrderBy(kv => kv.Value.LastSeen).Take(room.Roster.Count - MaxRoster).Select(kv => kv.Key).ToList()) room.Roster.Remove(k);
            room.RosterDirty = true;
        }
    }

    private void MaintainRoster(ChatRoom room)
    {
        bool save;
        lock (room.RosterGate)
        {
            long cutoff = DateTimeOffset.UtcNow.Subtract(RosterLifetime).ToUnixTimeMilliseconds();
            foreach (var k in room.Roster.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList()) room.Roster.Remove(k);
            save = room.RosterDirty;
        }
        if (save) SaveRoster(room);
    }

    internal IReadOnlyList<RoomPerson> GetRoomPeople(ChatRoom room)
    {
        var people = new Dictionary<string, RoomPerson>();
        foreach (var member in room.Members)
        {
            if (IsMyDevice(member.Id) || IsMyDevice(member.Account)) continue;
            if (!people.ContainsKey(member.Account.Value))
                people[member.Account.Value] = new RoomPerson(member.Account, member.DisplayName, true, DateTimeOffset.UtcNow);
        }
        lock (room.RosterGate)
            foreach (var (key, entry) in room.Roster)
                if (IsValidPeerId(key) && !people.ContainsKey(key) && !IsMyDevice(new PeerId(key)))
                    people[key] = new RoomPerson(new PeerId(key), entry.Name, false, DateTimeOffset.FromUnixTimeMilliseconds(entry.LastSeen));
        return people.Values.ToList();
    }

    internal IReadOnlyList<RotationCandidate> GetRotationCandidates(ChatRoom room)
    {
        foreach (var member in room.Members) NoteMember(room, member.Id, member.DisplayName);
        var online = room.Members.Select(m => m.Account).ToHashSet();
        lock (room.RosterGate)
            return room.Roster
                .Where(kv => IsValidPeerId(kv.Key) && !IsMyDevice(new PeerId(kv.Key)) && !IsBannedIn(room, new PeerId(kv.Key)) && !IsBlocked(new PeerId(kv.Key)))
                .OrderByDescending(kv => online.Contains(new PeerId(kv.Key))).ThenByDescending(kv => kv.Value.LastSeen)
                .Take(SignedRekey.MaxRecipients)
                .Select(kv => new RotationCandidate(new PeerId(kv.Key), kv.Value.Name, online.Contains(new PeerId(kv.Key)), DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.LastSeen)))
                .ToList();
    }

    // ---- the secrets this room has had ----

    private static string RekeyFile(string directory, ChatRoom room) => Path.Combine(directory, room.Key + ".rekey.dat");

    private sealed record RekeyState(StoredRekey? Package, string[] Previous, long[] At);

    // Sealed like the other secrets: it holds room keys.
    private void LoadRekeyState(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory || room.Owner is not { } owner) return;
        try
        {
            var path = RekeyFile(directory, room);
            if (!File.Exists(path)) return;
            var state = JsonSerializer.Deserialize<RekeyState>(SecretStore.ReadAllBytes(path), Json);
            if (state == null) return;
            lock (room.RekeyGate)
            {
                for (int i = 0; i < state.Previous.Length && i < state.At.Length; i++)
                {
                    try
                    {
                        var key = RoomKey.Parse(state.Previous[i]);
                        if (key.RoomId == room.Key && key.Epoch < room.Epoch) room.PreviousKeys.Add(new PreviousKey(key, state.At[i]));
                    }
                    catch (FormatException) { }
                }
                // The order that gave us the secret we hold now; one for any other secret is of no use.
                if (state.Package is { } package && package.Epoch == room.Epoch && SignedRekey.Verify(package, room.Key, owner))
                {
                    room.LastRekey = package;
                    room.RekeyHash = SignedRekey.Hash(package);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _options.Log?.Invoke("could not read the room's earlier keys: " + ex.Message);
        }
    }

    private void SaveRekeyState(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory) return;
        try
        {
            RekeyState state;
            lock (room.RekeyGate)
                state = new RekeyState(room.LastRekey, room.PreviousKeys.Select(p => p.Key.ToString()).ToArray(), room.PreviousKeys.Select(p => p.At).ToArray());
            Directory.CreateDirectory(directory);
            var path = RekeyFile(directory, room);
            SecretStore.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(state, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the room's earlier keys: " + ex.Message);
        }
    }

    // Where the room can still be found by people who have not heard of the new secret: its present place, and the old ones for a while.
    private List<byte[]> TopicsOf(ChatRoom room)
    {
        var topics = new List<byte[]> { room.Topic };
        long cutoff = DateTimeOffset.UtcNow.Subtract(OldTopicLifetime).ToUnixTimeMilliseconds();
        lock (room.RekeyGate)
            if (room.LastRekey != null)
                topics.AddRange(room.PreviousKeys.Where(p => p.At >= cutoff).Select(p => p.Key.Topic));
        return topics;
    }

    // Someone who is on the list for the new secret, and who still goes by an old one, was away when it was replaced: give it to them.
    // Their account is proved by the connection, so nobody else gets it, not even someone who has the old secret.
    private void OfferRekeyTo(PeerState peer)
    {
        foreach (var room in _rooms.Values)
        {
            StoredRekey? package;
            List<PreviousKey> previous;
            lock (room.RekeyGate) { package = room.LastRekey; previous = room.PreviousKeys.ToList(); }
            if (package == null || previous.Count == 0 || room.Owner is not { } owner) continue;
            if (peer.AccountId != owner && !package.To.Contains(peer.AccountId.Value)) continue;

            foreach (var session in peer.Sessions())
                foreach (var old in previous)
                {
                    if (!peer.Declares(ChatRoom.RefForKey(old.Key, session, peer.Id))) continue;
                    string gate = room.Key + peer.Id.Value;
                    long now = Environment.TickCount64;
                    if (_rekeyOffers.TryGetValue(gate, out var last) && now - last < 30_000) return;
                    _rekeyOffers[gate] = now;
                    _options.Log?.Invoke($"giving {peer.Id.Short} the new key for '{room.Name}': they were away when it was replaced");
                    Send(peer.Connection, new Wire { Type = Wire.Rekey, Room = ChatRoom.RefForKey(old.Key, session, Id), Rk = package });
                    return;
                }
        }
    }

    internal string RotateSecret(ChatRoom room, IReadOnlyCollection<PeerId> recipients)
    {
        if (room.Owner is not { } owner) throw new InvalidOperationException("This room has no owner, so its secret cannot be replaced. Rooms made with this version can.");
        if (owner != AccountId) throw new InvalidOperationException("Only the owner of the room can replace its secret.");
        var to = recipients.Select(r => r.Value).Where(IsValidPeerId).Distinct()
            .Where(a => a != AccountId.Value && !IsBannedIn(room, new PeerId(a))).ToList();
        if (to.Count > SignedRekey.MaxRecipients) throw new InvalidOperationException($"The new key can be given to at most {SignedRekey.MaxRecipients} people at once.");

        lock (room.RekeyGate)
        {
            int epoch = room.Epoch + 1;
            if (epoch > 65535) throw new InvalidOperationException("This room's secret has been replaced as many times as it can be.");
            var package = SignedRekey.Create(_identity, room.Key, RandomNumberGenerator.GetBytes(32), epoch, to, NextTimestamp());
            ApplyRekey(room, package, from: null, owner);
        }
        return room.Invite;
    }

    private static bool Better(ChatRoom room, StoredRekey package)
    {
        if (package.Epoch != room.Epoch) return package.Epoch > room.Epoch;
        return room.RekeyHash != null && string.CompareOrdinal(SignedRekey.Hash(package), room.RekeyHash) < 0;
    }

    private void OnRekey(PeerState peer, ChatRoom room, StoredRekey package)
    {
        if (room.Owner is not { } owner || package.To is null || package.Sig is null || package.To.Length > SignedRekey.MaxRecipients) return;
        // Skipped before the signature is checked, which is what costs: old ones, and ones that are not for us.
        if (!Better(room, package)) return;
        if (AccountId != owner && !package.To.Contains(AccountId.Value)) return;
        if (!SignedRekey.Verify(package, room.Key, owner)) return;
        lock (room.RekeyGate)
        {
            if (!Better(room, package)) return;
            ApplyRekey(room, package, peer, owner);
        }
    }

    private void ApplyRekey(ChatRoom room, StoredRekey package, PeerState? from, PeerId owner)
    {
        var newKey = room.CurrentKey.Rotated(Convert.FromBase64String(package.Secret), package.Epoch);
        var oldInvite = room.Invite;

        // Passed on first, while the old secret still lets us reach people, and only to those who are listed.
        var listed = package.To.ToHashSet();
        foreach (var member in room.MemberStates.Values.ToList())
            if ((from == null || member.Id != from.Id) && (listed.Contains(member.AccountId.Value) || member.AccountId == owner))
                Send(member.Connection, new Wire { Type = Wire.Rekey, Room = RefTo(room, member), Rk = package });

        room.RekeyHash = SignedRekey.Hash(package);
        lock (room.RekeyGate) room.LastRekey = package;
        SwitchSecret(room, newKey, oldInvite);
        // Your other devices take the new key too.
        Stamp(RoomPrefix + room.Key, JsonSerializer.Serialize(new RoomValue(room.Name, room.Invite), Json), deleted: false);
    }

    // The room has a new secret, from the owner's order or from an invite we were handed. Everyone starts again under it: those who have
    // it prove so by their new proof, and those who do not simply no longer match.
    private void SwitchSecret(ChatRoom room, RoomKey newKey, string oldInvite)
    {
        var oldKey = room.CurrentKey;
        lock (room.RekeyGate)
        {
            room.PreviousKeys.Insert(0, new PreviousKey(oldKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if (room.PreviousKeys.Count > 8) room.PreviousKeys.RemoveRange(8, room.PreviousKeys.Count - 8);
        }
        room.SwitchTo(newKey);
        SaveRekeyState(room);
        SaveRoster(room);
        foreach (var member in room.MemberStates.Values.ToList())
            if (room.MemberStates.TryRemove(member.Id, out _))
            {
                member.ClearLinks(room.Key);
                room.RaiseLeft(member.Info);
            }

        foreach (var peer in _peers.Values.ToList())
        {
            SendHelloTo(peer);
            Send(peer.Connection, new Wire { Type = Wire.Join, Room = RefTo(room, peer) });
        }
        // Some may already have switched and declared the new room to us.
        foreach (var peer in _peers.Values.ToList())
            if (peer.Connection.SessionId != null && peer.Declares(RefFrom(room, peer))) OnPeerEntersRoom(peer, room);
        RefreshIndirect(room);

        // Look for the room, and be found, at its new place.
        _ = Task.Run(async () =>
        {
            try { await DiscoverRoomAsync(room, announce: true, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _options.Log?.Invoke($"room {room.Name} could not announce its new key: {ex.Message}"); }
        });
        room.RaiseSecretChanged(oldInvite, room.Invite);
    }

    // An invite for a room we are already in, with a newer secret (handed over by another of our devices, or pasted): take it.
    private void AdoptKey(ChatRoom existing, RoomKey key)
    {
        lock (existing.RekeyGate)
        {
            if (key.Epoch <= existing.Epoch || key.Owner != existing.Owner) return;
            var oldInvite = existing.Invite;
            existing.RekeyHash = null;
            lock (existing.RekeyGate) existing.LastRekey = null;
            SwitchSecret(existing, key, oldInvite);
        }
    }

    private void SendHelloTo(PeerState state) =>
        Send(state.Connection, new Wire { Type = Wire.Hello, Name = DisplayName, Deg = _peers.Count, Rly = IsRelayCapable, Rooms = _rooms.Values.Select(r => RefTo(r, state)).ToArray() });
}
