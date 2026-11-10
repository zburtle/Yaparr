using Tmds.DBus.Protocol;

namespace Yaparr.Desktop;

/// <summary>
/// What a Flatpak app can do about its own updates. Flatpak installs a new version (<c>flatpak update</c>, the software centre) without closing a running
/// app, which carries on with the old files until it is started again. The Flatpak portal tells a running app when that has happened
/// (<c>org.freedesktop.portal.Flatpak.UpdateMonitor</c>), and starts a new sandbox with the latest version on request (<c>Spawn</c> with the
/// latest-version flag), which is how the app can restart into the new one. The portal is always reachable from the sandbox.
/// </summary>
internal sealed class FlatpakUpdates : IDisposable
{
    private const string Service = "org.freedesktop.portal.Flatpak";
    private const string PortalPath = "/org/freedesktop/portal/Flatpak";
    private const string PortalInterface = "org.freedesktop.portal.Flatpak";
    private const string MonitorInterface = "org.freedesktop.portal.Flatpak.UpdateMonitor";
    private const uint LatestVersionFlag = 1 << 1;

    private readonly DBusConnection _connection;
    private readonly IDisposable _watch;

    private FlatpakUpdates(DBusConnection connection, IDisposable watch)
    {
        _connection = connection;
        _watch = watch;
    }

    /// <summary>
    /// What the portal said when it saw a newer version. The app is running <paramref name="Running"/>; a version that is already installed on the
    /// computer, which the app is not running yet, is <paramref name="Installed"/>. Both are commit ids; when they differ, a restart is all it takes.
    /// </summary>
    public sealed record Info(string Running, string Installed, string Remote)
    {
        public bool InstalledAlready => Installed.Length > 0 && Running.Length > 0 && Installed != Running;
    }

    /// <summary>Starts watching. <paramref name="onUpdate"/> is called (on another thread) each time the portal reports. Null where there is no session bus or portal.</summary>
    public static async Task<FlatpakUpdates?> StartAsync(Action<Info> onUpdate, CancellationToken ct = default)
    {
        if (DBusAddress.Session is not { } address) return null;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);

            string token = "yaparr" + Environment.ProcessId;
            var writer = connection.GetMessageWriter();
            MessageBuffer call;
            try
            {
                writer.WriteMethodCallHeader(Service, PortalPath, PortalInterface, "CreateUpdateMonitor", "a{sv}");
                var options = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString("handle_token");
                writer.WriteVariantString(token);
                writer.WriteDictionaryEnd(options);
                call = writer.CreateMessage();
            }
            finally { writer.Dispose(); }
            string monitor = await connection.CallMethodAsync(call, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);

            var watch = await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = monitor, Interface = MonitorInterface, Member = "UpdateAvailable" },
                static (Message m, object? _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue(),
                (Notification<Dictionary<string, VariantValue>> n) =>
                {
                    if (!n.HasValue) return;
                    string Get(string key) => n.Value.TryGetValue(key, out var v) ? v.GetString() : "";
                    onUpdate(new Info(Get("running-commit"), Get("local-commit"), Get("remote-commit")));
                },
                false, ObserverFlags.None, null).ConfigureAwait(false);
            return new FlatpakUpdates(connection, watch);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            connection.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Starts the latest installed version of this app in a sandbox of its own, with <paramref name="environment"/> added. The caller then quits. False if the
    /// portal would not (the caller falls back to asking the person to open Yaparr again).
    /// </summary>
    public static async Task<bool> StartLatestAsync(string program, IReadOnlyDictionary<string, string> environment)
    {
        if (DBusAddress.Session is not { } address) return false;
        using var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            var writer = connection.GetMessageWriter();
            MessageBuffer call;
            try
            {
                writer.WriteMethodCallHeader(Service, PortalPath, PortalInterface, "Spawn", "ayaaya{uh}a{ss}ua{sv}");
                writer.WriteArray(NullTerminated(Environment.CurrentDirectory));
                var argv = writer.WriteArrayStart(DBusType.Array);
                writer.WriteArray(NullTerminated(program));
                writer.WriteArrayEnd(argv);
                var fds = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(fds);
                var envs = writer.WriteDictionaryStart();
                foreach (var (key, value) in environment)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(key);
                    writer.WriteString(value);
                }
                writer.WriteDictionaryEnd(envs);
                writer.WriteUInt32(LatestVersionFlag);
                var options = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(options);
                call = writer.CreateMessage();
            }
            finally { writer.Dispose(); }
            await connection.CallMethodAsync(call, static (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static byte[] NullTerminated(string text) => [.. System.Text.Encoding.UTF8.GetBytes(text), 0];

    public void Dispose()
    {
        _watch.Dispose();
        _connection.Dispose();
    }
}
