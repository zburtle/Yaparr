using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>
/// Which of the settings are the same on every device of an account, and how each travels as account entries (<see cref="ChatNode.SetAccountSetting"/>): the muted rooms, the
/// rooms that notify for every message, the sound of a room, the message sound, whether messages notify and only for mentions, link previews and leaving messages for
/// people who are away. The look of the app, the audio devices, keys, codecs, what the window does on close and the like belong to a device and are not here.
/// A list is one entry for each item (<c>muted:&lt;room id&gt;</c>), a value that is the default has no entry, so putting one back to the default is a removal.
/// </summary>
public static class SettingsSync
{
    private const string Muted = "muted:", NotifyAll = "notify-all:", RoomSound = "room-sound:";
    private const string MessageSoundName = "message-sound", OnlyMentions = "only-mentions", NotifyMessages = "notify-messages", LinkPreviews = "link-previews", LeaveForAbsent = "leave-for-absent";

    private static readonly AppSettings Defaults = new();

    /// <summary>The synced settings as they are now: name to value, only where the value is not the default.</summary>
    public static Dictionary<string, string> Snapshot(AppSettings s)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var room in s.MutedRooms) all[Muted + room] = "1";
        foreach (var room in s.NotifyAllRooms) all[NotifyAll + room] = "1";
        foreach (var (room, sound) in s.RoomSounds) all[RoomSound + room] = sound;
        if (s.MessageSound != Defaults.MessageSound) all[MessageSoundName] = s.MessageSound;
        if (s.NotifyOnlyMentions != Defaults.NotifyOnlyMentions) all[OnlyMentions] = Flag(s.NotifyOnlyMentions);
        if (s.NotifyMessages != Defaults.NotifyMessages) all[NotifyMessages] = Flag(s.NotifyMessages);
        if (s.LinkPreviews != Defaults.LinkPreviews) all[LinkPreviews] = Flag(s.LinkPreviews);
        if (s.LeaveForAbsent != Defaults.LeaveForAbsent) all[LeaveForAbsent] = Flag(s.LeaveForAbsent);
        return all.Where(p => Fits(p.Key, p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    /// <summary>Applies one change from another device: <paramref name="value"/> null puts the setting back to its default. Returns true if the settings changed.</summary>
    public static bool Apply(AppSettings s, string name, string? value)
    {
        if (name.StartsWith(Muted, StringComparison.Ordinal)) return Toggle(s.MutedRooms, name[Muted.Length..], value != null);
        if (name.StartsWith(NotifyAll, StringComparison.Ordinal)) return Toggle(s.NotifyAllRooms, name[NotifyAll.Length..], value != null);
        if (name.StartsWith(RoomSound, StringComparison.Ordinal))
        {
            var room = name[RoomSound.Length..];
            if (room.Length == 0) return false;
            if (value == null) return s.RoomSounds.Remove(room);
            if (s.RoomSounds.TryGetValue(room, out var had) && had == value) return false;
            s.RoomSounds[room] = value;
            return true;
        }
        switch (name)
        {
            case MessageSoundName: return Set(s.MessageSound, value ?? Defaults.MessageSound, v => s.MessageSound = v);
            case OnlyMentions: return Set(s.NotifyOnlyMentions, value == null ? Defaults.NotifyOnlyMentions : value == "1", v => s.NotifyOnlyMentions = v);
            case NotifyMessages: return Set(s.NotifyMessages, value == null ? Defaults.NotifyMessages : value == "1", v => s.NotifyMessages = v);
            case LinkPreviews: return Set(s.LinkPreviews, value == null ? Defaults.LinkPreviews : value == "1", v => s.LinkPreviews = v);
            case LeaveForAbsent: return Set(s.LeaveForAbsent, value == null ? Defaults.LeaveForAbsent : value == "1", v => s.LeaveForAbsent = v);
            default: return false;   // a setting a newer version knows: kept in the account, not used here
        }
    }

    /// <summary>
    /// What to tell the account so that it holds <paramref name="now"/>: a value for each setting that is not what <paramref name="before"/> had, and null for each that went
    /// back to the default. <paramref name="held"/> is what the account holds already, so what it holds equal is not sent again.
    /// </summary>
    public static Dictionary<string, string?> Changes(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> now, IReadOnlyDictionary<string, string> held)
    {
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in now)
            if ((!before.TryGetValue(name, out var was) || was != value) && (!held.TryGetValue(name, out var in_) || in_ != value)) changes[name] = value;
        foreach (var name in before.Keys)
            if (!now.ContainsKey(name) && held.ContainsKey(name)) changes[name] = null;
        return changes;
    }

    /// <summary>Settings this device has that the account does not hold yet (a device that had them before it was signed in): they are given to the account, once.</summary>
    public static Dictionary<string, string?> Missing(IReadOnlyDictionary<string, string> now, IReadOnlyDictionary<string, string> held) =>
        now.Where(p => !held.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);

    private static string Flag(bool on) => on ? "1" : "0";

    private static bool Fits(string name, string value) => name.Length > 0 && "setting:".Length + name.Length <= 100 && value.Length <= 400;

    private static bool Toggle(List<string> list, string room, bool on)
    {
        if (room.Length == 0) return false;
        if (on) { if (list.Contains(room)) return false; list.Add(room); return true; }
        return list.Remove(room);
    }

    private static bool Set<T>(T current, T value, Action<T> assign) where T : notnull
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(value);
        return true;
    }
}

/// <summary>
/// Keeps the synced settings of one device and the account in step (see <see cref="SettingsSync"/>): <see cref="Seed"/> once when the app starts, <see cref="Remote"/> when
/// another device changed one, <see cref="Local"/> after the settings were saved here. It remembers what it last knew, so each change is sent once and a change that came from
/// another device is not sent back. Call it from one thread (the window's).
/// </summary>
public sealed class SettingsPush
{
    private readonly ChatNode _node;
    private Dictionary<string, string> _last = new(StringComparer.Ordinal);

    public SettingsPush(ChatNode node) => _node = node;

    /// <summary>On start: takes what the account holds into <paramref name="settings"/> (returns true if that changed them, so they are saved), and gives the account what this device has that it does not.</summary>
    public bool Seed(AppSettings settings)
    {
        var held = _node.AccountSettings;
        bool changed = false;
        foreach (var (name, value) in held) changed |= SettingsSync.Apply(settings, name, value);
        _last = SettingsSync.Snapshot(settings);
        foreach (var (name, value) in SettingsSync.Missing(_last, held)) _node.SetAccountSetting(name, value);
        return changed;
    }

    /// <summary>Another device changed a setting: applies it (true if the settings changed, so they are saved).</summary>
    public bool Remote(AppSettings settings, string name, string? value)
    {
        bool changed = SettingsSync.Apply(settings, name, value);
        if (changed) _last = SettingsSync.Snapshot(settings);
        return changed;
    }

    /// <summary>The settings were saved here: what differs from what was last known goes to the account.</summary>
    public void Local(AppSettings settings)
    {
        var now = SettingsSync.Snapshot(settings);
        foreach (var (name, value) in SettingsSync.Changes(_last, now, _node.AccountSettings)) _node.SetAccountSetting(name, value);
        _last = now;
    }
}
