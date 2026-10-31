using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// One setting of a person's account, as kept and passed between their devices. Each key holds the latest value;
/// the one with the newer time wins, and a removal is kept as an entry too (with <see cref="Deleted"/> set) so it
/// can overrule an older value on a device that was offline.
/// </summary>
public sealed record AccountEntry(string Key, string? Value, long Ts, bool Deleted = false);

/// <summary>A room one of your devices joined: its key, and the name you gave it.</summary>
public sealed record SyncedRoom(string Name, string Key);

// Your devices keep your name, your blocks and your list of rooms the same. The entries only travel between
// connections whose certificate says they belong to the same account, and they are saved so a device that was
// switched off picks up where it was.
public sealed partial class ChatNode
{
    private const string NameKey = "name", BlockPrefix = "block:", RoomPrefix = "room:", ReadPrefix = "read:";
    private const int MaxEntryKey = 100, MaxEntryValue = 400, EntriesPerMessage = 20;

    private readonly object _acctGate = new();
    private readonly Dictionary<string, AccountEntry> _acct = new();

    private sealed record RoomValue(string N, string K);

    /// <summary>A sibling device added a room (true) or removed one (false). Join or leave it, and remember the change.</summary>
    public event Action<SyncedRoom, bool>? SyncedRoomChanged;

    /// <summary>The name changed because another of your devices renamed you. Remember the new name.</summary>
    public event Action<string>? DisplayNameChanged;

    /// <summary>Everything the account currently holds, for saving or for handing to a device being linked.</summary>
    public IReadOnlyList<AccountEntry> AccountEntries
    {
        get { lock (_acctGate) return _acct.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList(); }
    }

    /// <summary>The rooms your account has, according to your devices so far.</summary>
    public IReadOnlyList<SyncedRoom> SyncedRooms
    {
        get { lock (_acctGate) return _acct.Values.Where(e => !e.Deleted && e.Key.StartsWith(RoomPrefix, StringComparison.Ordinal)).Select(ParseRoom).OfType<SyncedRoom>().ToList(); }
    }

    private void LoadAccountState()
    {
        var path = _options.AccountStatePath;
        if (path != null && File.Exists(path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<AccountEntry[]>(SecretStore.ReadAllBytes(path), Json);
                foreach (var e in saved ?? Array.Empty<AccountEntry>()) if (IsValidEntry(e)) _acct[e.Key] = e;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _options.Log?.Invoke($"could not read the saved account settings: {ex.Message}");
            }
        }
        bool seeded = false;
        foreach (var entry in _options.AccountEntries)
            if (IsValidEntry(entry) && Newer(entry, _acct.GetValueOrDefault(entry.Key))) { _acct[entry.Key] = entry; seeded = true; }
        if (seeded) SaveAccountStateLocked();
        // People blocked before this existed: the oldest possible time, so any later unblock on another device overrules it.
        foreach (var blocked in _options.BlockedPeers) _acct.TryAdd(BlockPrefix + blocked.Value, new AccountEntry(BlockPrefix + blocked.Value, "1", 0));
    }

    private void SaveAccountStateLocked()
    {
        var path = _options.AccountStatePath;
        if (path == null) return;
        try
        {
            SecretStore.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(_acct.Values.ToArray(), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke($"could not save the account settings: {ex.Message}");
        }
    }

    private static bool IsValidEntry(AccountEntry e) =>
        e.Key is { Length: > 0 and <= MaxEntryKey } && (e.Value == null || e.Value.Length <= MaxEntryValue)
        && (e.Key == NameKey || e.Key.StartsWith(BlockPrefix, StringComparison.Ordinal) || e.Key.StartsWith(RoomPrefix, StringComparison.Ordinal)
            || e.Key.StartsWith(ReadPrefix, StringComparison.Ordinal));

    // A total order, so devices that see the same entries in a different order still end up with the same one.
    private static bool Newer(AccountEntry candidate, AccountEntry? current)
    {
        if (current == null) return true;
        if (candidate.Ts != current.Ts) return candidate.Ts > current.Ts;
        if (candidate.Deleted != current.Deleted) return candidate.Deleted;
        return string.CompareOrdinal(candidate.Value, current.Value) > 0;
    }

    private static SyncedRoom? ParseRoom(AccountEntry e)
    {
        try { return JsonSerializer.Deserialize<RoomValue>(e.Value ?? "", Json) is { N.Length: > 0, K.Length: > 0 } v ? new SyncedRoom(v.N, v.K) : null; }
        catch (JsonException) { return null; }
    }

    // Records a change made on this device and tells the other devices.
    private void Stamp(string key, string? value, bool deleted)
    {
        AccountEntry entry;
        lock (_acctGate)
        {
            entry = new AccountEntry(key, value, NextTimestamp(), deleted);
            _acct[key] = entry;
            SaveAccountStateLocked();
        }
        SendToSiblings(new[] { entry });
    }

    private void NoteRoomChanged(ChatRoom room, bool joined)
    {
        var key = RoomPrefix + room.Key;
        var value = JsonSerializer.Serialize(new RoomValue(room.Name, room.SecretText), Json);
        lock (_acctGate)
        {
            // Already recorded that way (a restart joining the saved rooms again, or a room a sibling just added).
            if (_acct.TryGetValue(key, out var existing) && existing.Deleted != joined) return;
        }
        Stamp(key, value, deleted: !joined);
    }

    private void NoteBlockChanged(PeerId person, bool blocked) => Stamp(BlockPrefix + person.Value, "1", deleted: !blocked);

    private bool IsSibling(PeerState peer) => peer.Id != Id && peer.AccountId == AccountId;

    private void SendToSiblings(IReadOnlyList<AccountEntry> entries)
    {
        if (entries.Count == 0) return;
        foreach (var sibling in _peers.Values.Where(IsSibling)) SendEntries(sibling, entries);
    }

    private void SendEntries(PeerState sibling, IReadOnlyList<AccountEntry> entries)
    {
        for (int i = 0; i < entries.Count; i += EntriesPerMessage)
            Send(sibling.Connection, new Wire { Type = Wire.Account, Entries = entries.Skip(i).Take(EntriesPerMessage).ToArray() });
    }

    private void SendAccountState(PeerState sibling) => SendEntries(sibling, AccountEntries);

    private void OnAccountEntries(PeerState peer, AccountEntry[] entries)
    {
        // Only another of your own devices may change your settings; the connection's certificate is what proves it.
        if (!IsSibling(peer)) return;
        var applied = new List<AccountEntry>();
        var newerHere = new List<AccountEntry>();
        lock (_acctGate)
        {
            foreach (var e in entries.Take(200))
            {
                if (!IsValidEntry(e)) continue;
                var current = _acct.GetValueOrDefault(e.Key);
                if (Newer(e, current)) { _acct[e.Key] = e; applied.Add(e); }
                else if (current != null && Newer(current, e)) newerHere.Add(current);
            }
            if (applied.Count > 0) SaveAccountStateLocked();
        }
        foreach (var e in applied) ApplyEntry(e);
        // The sender is behind on these, for example after being switched off for a while. Equal entries are not
        // sent back, which is what ends the exchange.
        SendEntries(peer, newerHere);
    }

    private void ApplyEntry(AccountEntry e)
    {
        if (e.Key == NameKey)
        {
            var name = CleanName(e.Value);
            if (e.Deleted || name.Length == 0 || name == _displayName) return;
            ApplyName(name);
            DisplayNameChanged?.Invoke(name);
        }
        else if (e.Key.StartsWith(BlockPrefix, StringComparison.Ordinal))
        {
            var id = e.Key[BlockPrefix.Length..];
            if (!IsValidPeerId(id)) return;
            try { if (e.Deleted) Unblock(new PeerId(id), announce: false); else Block(new PeerId(id), announce: false); }
            catch (ArgumentException) { }
        }
        else if (e.Key.StartsWith(ReadPrefix, StringComparison.Ordinal))
        {
            var id = e.Key[ReadPrefix.Length..];
            if (IsValidPeerId(id) && _directChats.TryGetValue(new PeerId(id), out var chat)) chat.RaiseRead();
        }
        else if (e.Key.StartsWith(RoomPrefix, StringComparison.Ordinal))
        {
            var key = e.Key[RoomPrefix.Length..];
            if (ParseRoom(e) is not { } room) return;
            if (_rooms.ContainsKey(key) == !e.Deleted) return;
            SyncedRoomChanged?.Invoke(room, !e.Deleted);
        }
    }
}
