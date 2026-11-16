using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>What came of asking for a wake-up.</summary>
public enum WakeResult
{
    /// <summary>The push was sent (it may still not reach a phone that is off).</summary>
    Sent,

    /// <summary>The address no longer leads to anyone (the person turned push off, or left): it is forgotten.</summary>
    Gone,

    /// <summary>It could not be sent now (the service is down, or the person was woken a lot lately). Tried again with the next message.</summary>
    Failed,
}

/// <summary>
/// A wake address is where to ask for someone's phone to be woken: <c>https://server/v1/push/wake/&lt;token&gt;</c>, on the push service of the Yaparr Cloud server the
/// person has an account with. It goes to the people in their rooms, who can use it to ask for a push with nothing in it, and for nothing else. Only the shape is
/// checked here; the server does the rest.
/// </summary>
public static class WakeAddress
{
    public const int MaxLength = 256;

    /// <summary>Whether this is a wake address: https, a name (not a number), no password, and the path of a wake token.</summary>
    public static bool TryParse(string? address, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(address) || address.Length > MaxLength) return false;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || parsed.HostNameType != UriHostNameType.Dns) return false;
        if (parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !parsed.Host.Contains('.')) return false;
        const string prefix = "/v1/push/wake/";
        var path = parsed.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length != prefix.Length + 43) return false;
        if (!path.AsSpan(prefix.Length).ToString().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        uri = parsed;
        return true;
    }

    /// <summary>The address for a token on a server.</summary>
    public static string For(Uri server, string token) => new Uri(server, "/v1/push/wake/" + token).ToString();
}

/// <summary>
/// The wake addresses of the people we have met in rooms, and which rooms they were in, so that a message to a room (or a direct message) can wake the members who
/// are not connected. Kept in a file beside the peer cache; nothing here is secret from the person it was given to, but it is not for anyone else, so it stays private.
/// </summary>
internal sealed class WakeBook
{
    private const int MaxEntries = 512;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);

    internal sealed class Entry
    {
        public string Device { get; set; } = "";
        public string Account { get; set; } = "";
        public string Url { get; set; } = "";
        public List<string> Rooms { get; set; } = new();
        public long Seen { get; set; }
    }

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Func<long> _now;
    private Dictionary<string, Entry> _entries = new();
    private bool _dirty;

    public WakeBook(string? path, Func<long>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (path == null || !File.Exists(path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
            foreach (var e in list.Where(e => e.Device.Length > 0 && WakeAddress.TryParse(e.Url, out _))) _entries[e.Device] = e;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
    }

    public void Remember(PeerId device, PeerId account, string url, string? roomKey)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(device.Value, out var entry) || entry.Url != url || entry.Account != account.Value)
            {
                entry = new Entry { Device = device.Value, Account = account.Value, Url = url };
                _entries[device.Value] = entry;
                _dirty = true;
            }
            entry.Seen = _now();
            if (roomKey != null && !entry.Rooms.Contains(roomKey))
            {
                entry.Rooms.Add(roomKey);
                _dirty = true;
            }
            if (_entries.Count > MaxEntries)
                foreach (var old in _entries.Values.OrderBy(e => e.Seen).Take(_entries.Count - MaxEntries).ToList()) _entries.Remove(old.Device);
        }
    }

    /// <summary>The person turned push off (or this device was reset): forget their address.</summary>
    public void ForgetDevice(PeerId device)
    {
        lock (_gate) _dirty |= _entries.Remove(device.Value);
    }

    /// <summary>The server says nobody is behind this address any more.</summary>
    public void ForgetUrl(string url)
    {
        lock (_gate)
            foreach (var e in _entries.Values.Where(e => e.Url == url).ToList())
            {
                _entries.Remove(e.Device);
                _dirty = true;
            }
    }

    private IReadOnlyList<Entry> Live()
    {
        long limit = _now() - (long)MaxAge.TotalMilliseconds;
        return _entries.Values.Where(e => e.Seen >= limit).ToList();
    }

    public IReadOnlyList<Entry> ForRoom(string roomKey)
    {
        lock (_gate) return Live().Where(e => e.Rooms.Contains(roomKey)).ToList();
    }

    public IReadOnlyList<Entry> ForAccount(PeerId account)
    {
        lock (_gate) return Live().Where(e => e.Account == account.Value || e.Device == account.Value).ToList();
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void Save()
    {
        if (_path == null) return;
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_entries.Values.ToList(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            _dirty = false;
        }
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (_gate) _dirty = true; }
    }
}
