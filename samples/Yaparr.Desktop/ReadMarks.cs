namespace Yaparr.Desktop;

/// <summary>
/// For each text channel of each room, the time up to which it has been seen, kept in the settings so that what was unread when the app
/// was closed is still unread when it is opened again. What is newer than the mark is unread.
/// </summary>
internal sealed class ReadMarks
{
    private readonly Dictionary<string, long> _store;
    private readonly Func<long> _now;

    public ReadMarks(Dictionary<string, long> store, Func<long>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Something changed since <see cref="Saved"/> was last called.</summary>
    public bool Dirty { get; private set; }

    public void Saved() => Dirty = false;

    private static string KeyOf(string roomId, string channel) => roomId + "/" + channel;

    /// <summary>
    /// Up to which time (milliseconds since 1970) the channel has been seen. A channel seen for the first time, a new room or the first
    /// start after this was added, has everything in it so far counted as seen, so that old history does not all turn up as unread.
    /// </summary>
    public long Of(string roomId, string channel)
    {
        var key = KeyOf(roomId, channel);
        if (_store.TryGetValue(key, out var at)) return at;
        at = _now();
        _store[key] = at;
        Dirty = true;
        return at;
    }

    /// <summary>Records that the channel has been seen up to this time (now when none is given). A mark only moves forward.</summary>
    public void Mark(string roomId, string channel, long? at = null)
    {
        long when = at ?? _now();
        var key = KeyOf(roomId, channel);
        if (_store.TryGetValue(key, out var have) && have >= when) return;
        _store[key] = when;
        Dirty = true;
    }

    /// <summary>Whether a message sent at this time is newer than what has been seen in its channel.</summary>
    public bool IsUnread(string roomId, string channel, long messageTime) => messageTime > Of(roomId, channel);
}
