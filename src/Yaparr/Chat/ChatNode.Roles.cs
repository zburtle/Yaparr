using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

// Who runs a room. The invite names the owner, and the owner's devices sign statements appointing or removing admins; the
// statements are passed around the room like the soundboard list. Only the owner appoints, which keeps the rule simple
// to check: an admin cannot make more admins, so a removed admin has nobody to hand the role on to, and a statement cannot be
// made to look older than it is to slip in before a removal.
public sealed partial class ChatNode
{
    private const int MaxRoleEntries = 256;

    private static string RolesFile(string directory, ChatRoom room) => Path.Combine(directory, room.Key + ".roles.json");

    private void LoadRoles(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory || room.Owner is not { } owner) return;
        try
        {
            var path = RolesFile(directory, room);
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<StoredRole[]>(File.ReadAllBytes(path), Json) ?? Array.Empty<StoredRole>();
            lock (room.RoleGate)
                foreach (var r in stored.Where(r => SignedRole.Verify(r, room.Key, owner)))
                    if (!room.RoleEntries.TryGetValue(r.Subject, out var old) || old.Ts < r.Ts) room.RoleEntries[r.Subject] = r;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not read the room's admins: " + ex.Message);
        }
    }

    private void SaveRoles(ChatRoom room)
    {
        if (_options.RolesDirectory is not { } directory) return;
        try
        {
            StoredRole[] all;
            lock (room.RoleGate) all = room.RoleEntries.Values.ToArray();
            Directory.CreateDirectory(directory);
            var path = RolesFile(directory, room);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(all, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the room's admins: " + ex.Message);
        }
    }

    internal RoomRole RoleIn(ChatRoom room, PeerId account)
    {
        if (room.Owner is not { } owner) return RoomRole.Member;
        if (account == owner) return RoomRole.Owner;
        lock (room.RoleGate) return room.RoleEntries.TryGetValue(account.Value, out var r) && r.Admin ? RoomRole.Admin : RoomRole.Member;
    }

    internal IReadOnlyList<PeerId> GetAdmins(ChatRoom room)
    {
        lock (room.RoleGate)
            return room.RoleEntries.Values.Where(r => r.Admin).Select(r => new PeerId(r.Subject)).OrderBy(p => p.Value, StringComparer.Ordinal).ToList();
    }

    internal void SetAdmin(ChatRoom room, PeerId account, bool admin)
    {
        if (room.Owner is not { } owner) throw new InvalidOperationException("This room has no owner, so it has no admins. Rooms made with this version do.");
        if (owner != AccountId) throw new InvalidOperationException("Only the owner of the room can appoint or remove admins.");
        if (account == owner) throw new InvalidOperationException("The owner is always in charge and cannot be made an admin.");
        if (!IsValidPeerId(account.Value)) throw new ArgumentException("Not an account id.", nameof(account));
        lock (room.RoleGate)
        {
            if (!room.RoleEntries.ContainsKey(account.Value) && room.RoleEntries.Count >= MaxRoleEntries)
                throw new InvalidOperationException("This room has as many admins as it can keep track of.");
        }
        // Later than every statement we have seen, not just later than our clock: the newest statement wins, so a change
        // made from a device whose clock runs slow must still beat the one it replaces.
        long ts;
        lock (room.RoleGate) ts = Math.Max(NextTimestamp(), (room.RoleEntries.Count == 0 ? 0 : room.RoleEntries.Values.Max(r => r.Ts)) + 1);
        var entry = SignedRole.Create(_identity, room.Key, account, admin, ts);
        lock (room.RoleGate) room.RoleEntries[entry.Subject] = entry;
        SaveRoles(room);
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Roles, Room = RefTo(room, member), Rol = new[] { entry } });
        Interlocked.Increment(ref room.ModVersion);
        Interlocked.Increment(ref room.ChannelVersion);
        room.RaiseRolesChanged();
        room.RaiseChannelsChanged();
        // An order counts only while the one who gave it is an admin, so a change of admins can change who is banned.
        if (room.ModEntries.Count > 0) ApplyModeration(room);
    }

    private void SendRolesTo(ChatRoom room, PeerState peer)
    {
        StoredRole[] all;
        lock (room.RoleGate) all = room.RoleEntries.Values.ToArray();
        for (int i = 0; i < all.Length; i += 32)
            Send(peer.Connection, new Wire { Type = Wire.Roles, Room = RefTo(room, peer), Rol = all.Skip(i).Take(32).ToArray() });
    }

    private void OnRoles(PeerState peer, ChatRoom room, StoredRole[] roles)
    {
        if (room.Owner is not { } owner) return;
        var accepted = new List<StoredRole>();
        foreach (var r in roles.Take(32))
        {
            // A statement we already have, or an older one, is skipped before the signature is checked, which is what costs.
            lock (room.RoleGate)
            {
                if (room.RoleEntries.TryGetValue(r.Subject ?? "", out var have) && have.Ts >= r.Ts) continue;
                if (have == null && room.RoleEntries.Count >= MaxRoleEntries) continue;
            }
            if (!SignedRole.Verify(r, room.Key, owner)) continue;
            lock (room.RoleGate)
            {
                if (room.RoleEntries.TryGetValue(r.Subject, out var now) && now.Ts >= r.Ts) continue;
                room.RoleEntries[r.Subject] = r;
            }
            accepted.Add(r);
        }
        if (accepted.Count == 0) return;
        SaveRoles(room);
        foreach (var r in accepted)
            Relay(peer, room, PeerId.FromPublicKey(Convert.FromBase64String(r.Pub)).Value, reference => new Wire { Type = Wire.Roles, Room = reference, Rol = new[] { r } });
        Interlocked.Increment(ref room.ModVersion);
        Interlocked.Increment(ref room.ChannelVersion);
        room.RaiseRolesChanged();
        room.RaiseChannelsChanged();
        if (room.ModEntries.Count > 0) ApplyModeration(room);
    }
}
