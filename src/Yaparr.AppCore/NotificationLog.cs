namespace Yaparr.AppCore;

/// <summary>What a notification is about. Only messages for now; requests and invitations will be more kinds, so an entry keeps its kind as text and one this version does not know is kept as it is.</summary>
public static class NotificationKinds
{
    public const string Message = "message";
}

/// <summary>One thing that was worth a notification: kept so that it can be looked at later, whether or not the system showed it, and whether or not it was seen.</summary>
public sealed class NotificationEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>When it happened, in milliseconds since 1970.</summary>
    public long Time { get; set; }
    public string Kind { get; set; } = NotificationKinds.Message;
    /// <summary>The chat it belongs to: a room's id, or the other person's id.</summary>
    public string ChatKey { get; set; } = "";
    public bool IsRoom { get; set; }
    /// <summary>The chat's name when it happened.</summary>
    public string Title { get; set; } = "";
    public string Sender { get; set; } = "";
    /// <summary>The words, or what the message carried ("Sent a picture").</summary>
    public string Body { get; set; } = "";
    /// <summary>The text channel of a room message, so that opening the entry can go there.</summary>
    public string? Channel { get; set; }
    /// <summary>Identifies the message, so that the same one is never listed twice.</summary>
    public string? MessageKey { get; set; }
    /// <summary>The message named you (or answered you), not only arrived.</summary>
    public bool Mentioned { get; set; }
    public bool Read { get; set; }
}

/// <summary>
/// The list of notifications that were missed or may have been: every message that was worth a notification while it was not in front of you, newest
/// first, until it is seen. It is kept on the device (the file is sealed, like the other private files), holds at most <see cref="MaxEntries"/>, and drops
/// what is more than <see cref="KeepFor"/> old. Opening a chat marks its entries seen; they stay listed, so that what was missed can still be found.
/// </summary>
public sealed class NotificationLog
{
    public const int MaxEntries = 200;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private readonly List<NotificationEntry> _entries;   // oldest first, as written
    private readonly Func<long> _now;

    public NotificationLog(IEnumerable<NotificationEntry>? entries = null, Func<long>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _entries = entries?.Where(e => e != null).OrderBy(e => e.Time).ToList() ?? new List<NotificationEntry>();
        Prune();
    }

    /// <summary>Something changed since <see cref="Saved"/> was last called.</summary>
    public bool Dirty { get; private set; }

    public void Saved() => Dirty = false;

    /// <summary>Raised after any change, for a screen showing the list or a count.</summary>
    public event Action? Changed;

    /// <summary>The entries, newest first.</summary>
    public IReadOnlyList<NotificationEntry> Items => _entries.AsEnumerable().Reverse().ToList();

    /// <summary>The entries to save, oldest first.</summary>
    public IReadOnlyList<NotificationEntry> ForSaving => _entries.ToList();

    public int UnreadCount => _entries.Count(e => !e.Read);

    public int UnreadIn(string chatKey) => _entries.Count(e => !e.Read && e.ChatKey == chatKey);

    /// <summary>Adds an entry (stamped with now if it has no time). A message already listed is not added again. Returns whether it was added.</summary>
    public bool Add(NotificationEntry entry)
    {
        if (entry.MessageKey is { Length: > 0 } key && _entries.Any(e => e.MessageKey == key)) return false;
        if (entry.Time == 0) entry.Time = _now();
        // Kept in time order, though a message that waited before being announced can arrive after a newer one.
        int at = _entries.Count;
        while (at > 0 && _entries[at - 1].Time > entry.Time) at--;
        _entries.Insert(at, entry);
        Prune();
        Touch();
        return true;
    }

    /// <summary>The chat was opened: what it had is seen. Returns how many entries that changed.</summary>
    public int MarkChatRead(string chatKey)
    {
        int changed = 0;
        foreach (var e in _entries)
            if (!e.Read && e.ChatKey == chatKey) { e.Read = true; changed++; }
        if (changed > 0) Touch();
        return changed;
    }

    public bool MarkRead(string id)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry == null || entry.Read) return false;
        entry.Read = true;
        Touch();
        return true;
    }

    public int MarkAllRead()
    {
        int changed = 0;
        foreach (var e in _entries)
            if (!e.Read) { e.Read = true; changed++; }
        if (changed > 0) Touch();
        return changed;
    }

    public bool Remove(string id)
    {
        if (_entries.RemoveAll(e => e.Id == id) == 0) return false;
        Touch();
        return true;
    }

    /// <summary>A chat was left or removed: what it had has nowhere to lead.</summary>
    public int RemoveChat(string chatKey)
    {
        int removed = _entries.RemoveAll(e => e.ChatKey == chatKey);
        if (removed > 0) Touch();
        return removed;
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Touch();
    }

    // Too many, or too old: the oldest go.
    private void Prune()
    {
        long oldest = _now() - (long)KeepFor.TotalMilliseconds;
        int removed = _entries.RemoveAll(e => e.Time < oldest);
        if (_entries.Count > MaxEntries) { removed += _entries.Count - MaxEntries; _entries.RemoveRange(0, _entries.Count - MaxEntries); }
        if (removed > 0) Dirty = true;
    }

    private void Touch()
    {
        Dirty = true;
        Changed?.Invoke();
    }

    /// <summary>"Alex in the new room", or just "Alex" in a direct chat: who, and where.</summary>
    public static string Heading(NotificationEntry e)
    {
        if (e.Sender.Length == 0) return e.Title;
        return e.IsRoom && e.Title.Length > 0 && !string.Equals(e.Sender, e.Title, StringComparison.Ordinal) ? e.Sender + " in " + e.Title : e.Sender;
    }
}
