using Yaparr.Identity;

namespace Yaparr.Chat;

public sealed partial class ChatNode
{
    private WakeBook _wakeBook = null!;
    private string? _wakeAddress;
    private readonly Dictionary<string, long> _wakeAsked = new();
    private const int MaxWakesPerSend = 32;

    /// <summary>
    /// Where someone can ask for this phone to be woken (see <see cref="WakeAddress"/>), or null for nowhere. It is told to everyone we are connected to, and to each
    /// new connection, so that when they send a message and we are not connected they can wake us. Setting it to null tells them to forget it.
    /// </summary>
    public string? WakeAddressOfMine
    {
        get => _wakeAddress;
        set
        {
            if (value != null && !WakeAddress.TryParse(value, out _)) throw new ArgumentException("That is not a wake address.", nameof(value));
            if (value == _wakeAddress) return;
            _wakeAddress = value;
            foreach (var peer in _peers.Values) Send(peer.Connection, new Wire { Type = Wire.Caps, Wake = value ?? "" });
        }
    }

    /// <summary>How many people's wake addresses are known (for diagnostics).</summary>
    public int KnownWakeAddresses => _wakeBook.Count;

    private static string? CleanWake(string? address) => WakeAddress.TryParse(address, out _) ? address : null;

    // Someone we are connected to told us where to wake them (or that nowhere is right now). Only they can say it: the connection proves who they are.
    private void OnWakeAddress(PeerState peer, string? address)
    {
        if (address == null) return;
        if (address.Length == 0)
        {
            peer.WakeUrl = null;
            _wakeBook.ForgetDevice(peer.Id);
            _wakeBook.Save();
            return;
        }
        if (CleanWake(address) is not { } url) return;
        peer.WakeUrl = url;
        foreach (var room in _rooms.Values)
            if (room.MemberStates.ContainsKey(peer.Id)) _wakeBook.Remember(peer.Id, peer.AccountId, url, room.Key);
        _wakeBook.Save();
    }

    private void NoteWakeAddress(PeerState peer, ChatRoom room)
    {
        if (peer.WakeUrl is { } url) _wakeBook.Remember(peer.Id, peer.AccountId, url, room.Key);
    }

    private bool IsConnectedPerson(string person) => _peers.Values.Any(s => s.Id.Value == person || s.AccountId.Value == person);

    // A message went to a room: the members we know how to wake and are not connected to are asked to look.
    private void WakeAbsentInRoom(ChatRoom room)
    {
        if (_options.WakePeer == null) return;
        var absent = _wakeBook.ForRoom(room.Key).Where(e => !IsMyDevice(new PeerId(e.Device)) && !IsMyDevice(new PeerId(e.Account))
            && !IsConnectedPerson(e.Account) && !IsConnectedPerson(e.Device) && !IsBannedIn(room, new PeerId(e.Account)) && !IsBannedIn(room, new PeerId(e.Device))).ToList();
        Wake(absent);
    }

    // A direct message went to someone who is not connected: they are asked to look.
    private void WakeAbsentDirect(PeerId recipient)
    {
        if (_options.WakePeer == null || IsConnectedPerson(recipient.Value)) return;
        Wake(_wakeBook.ForAccount(recipient).Where(e => !IsConnectedPerson(e.Device) && !IsMyDevice(new PeerId(e.Device))).ToList());
    }

    private void Wake(IReadOnlyList<WakeBook.Entry> absent)
    {
        if (absent.Count == 0 || _options.WakePeer is not { } ask) return;
        long now = Environment.TickCount64;
        var due = new List<string>();
        lock (_wakeAsked)
        {
            foreach (var url in absent.Select(e => e.Url).Distinct())
            {
                // Once in a while for each, however many messages there are: the phone gets what it missed when it comes up, not one wake-up per message.
                if (_wakeAsked.TryGetValue(url, out var last) && now - last < _options.WakeCooldown.TotalMilliseconds) continue;
                _wakeAsked[url] = now;
                due.Add(url);
                if (due.Count >= MaxWakesPerSend) break;
            }
            if (_wakeAsked.Count > 4096)
                foreach (var old in _wakeAsked.Where(kv => now - kv.Value > _options.WakeCooldown.TotalMilliseconds).Select(kv => kv.Key).ToList()) _wakeAsked.Remove(old);
        }
        foreach (var url in due) _ = AskToWakeAsync(ask, url);
    }

    private async Task AskToWakeAsync(Func<string, Task<WakeResult>> ask, string url)
    {
        try
        {
            var result = await ask(url).ConfigureAwait(false);
            if (result == WakeResult.Gone)
            {
                _wakeBook.ForgetUrl(url);
                _wakeBook.Save();
            }
            // Not sent: ask again with the next message rather than after the cooldown.
            else if (result == WakeResult.Failed) lock (_wakeAsked) _wakeAsked.Remove(url);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _options.Log?.Invoke("waking someone failed: " + ex.Message);
            lock (_wakeAsked) _wakeAsked.Remove(url);
        }
    }
}
