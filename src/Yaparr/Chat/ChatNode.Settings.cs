namespace Yaparr.Chat;

// The preferences a person wants the same on every device (muted rooms, a room's sound, whether messages notify) are entries of the account of their own, one for each
// thing (setting:<name>), so the usual rule decides between devices: the newer write of one wins, and changing one never touches the others. What the names mean is
// the apps' business (see SettingsSync in AppCore); the node only keeps and passes them on. A value that is back to the default is the entry marked deleted.
public sealed partial class ChatNode
{
    private const string SettingPrefix = "setting:";

    /// <summary>A setting was changed by another of your devices (or the account's cloud copy): its name (without the prefix) and its new value, null when it is back to the default.</summary>
    public event Action<string, string?>? SettingChanged;

    /// <summary>The settings the account holds now, by name.</summary>
    public IReadOnlyDictionary<string, string> AccountSettings
    {
        get
        {
            lock (_acctGate)
                return _acct.Values.Where(e => !e.Deleted && e.Value != null && e.Key.StartsWith(SettingPrefix, StringComparison.Ordinal))
                    .ToDictionary(e => e.Key[SettingPrefix.Length..], e => e.Value!, StringComparer.Ordinal);
        }
    }

    /// <summary>Sets a setting of the account, or (with null) puts it back to the default. Nothing happens if it already is so.</summary>
    public void SetAccountSetting(string name, string? value)
    {
        if (name.Length == 0 || SettingPrefix.Length + name.Length > MaxEntryKey) throw new ArgumentException("The name of a setting is too long.", nameof(name));
        if (value != null && value.Length > MaxEntryValue) throw new ArgumentException("The value of a setting is too long.", nameof(value));
        lock (_acctGate)
        {
            _acct.TryGetValue(SettingPrefix + name, out var held);
            if (value == null ? held == null || held.Deleted : held is { Deleted: false } && held.Value == value) return;
        }
        Stamp(SettingPrefix + name, value, deleted: value == null);
    }
}
