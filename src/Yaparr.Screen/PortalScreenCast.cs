using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Yaparr.Screen;

/// <summary>What the portal said about the stream: its node on PipeWire, its size in pixels (0 when it did not say) and whether it is a window.</summary>
public sealed record PortalStream(uint NodeId, int Width, int Height, bool IsWindow);

/// <summary>
/// One screen or window handed over by the desktop's ScreenCast portal (xdg-desktop-portal, over D-Bus), which is how a program
/// may capture the screen on Wayland. The desktop shows its own picker, the user chooses, and the answer is a PipeWire stream:
/// <see cref="PipeWireFd"/> is a connection to PipeWire that can see only that stream, and <see cref="NodeId"/> names the stream
/// on it. Disposing, or the user stopping the share from the desktop's own indicator, ends it.
/// </summary>
public sealed class PortalScreenCast : IDisposable
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    private static int _counter;

    private readonly DBusConnection _connection;
    private readonly string _session;
    private IDisposable? _closedWatch;
    private volatile bool _disposed;

    private PortalScreenCast(DBusConnection connection, string session, SafeFileHandle fd, uint node, int width, int height, bool window)
    {
        _connection = connection;
        _session = session;
        PipeWireFd = fd;
        Stream = new PortalStream(node, width, height, window);
    }

    /// <summary>A connection to PipeWire that sees only the chosen stream. Owned by this object; do not close it.</summary>
    public SafeFileHandle PipeWireFd { get; }

    /// <summary>The stream on <see cref="PipeWireFd"/>.</summary>
    public PortalStream Stream { get; }

    public uint NodeId => Stream.NodeId;

    /// <summary>The size of the picture in pixels as the desktop reported it; 0 when it did not say.</summary>
    public int Width => Stream.Width;
    public int Height => Stream.Height;

    /// <summary>Whether the user chose a window rather than a monitor.</summary>
    public bool IsWindow => Stream.IsWindow;

    /// <summary>The share ended from the desktop's side (the user stopped it, or the window closed). Raised on another thread.</summary>
    public event Action? Closed;

    /// <summary>Whether a session bus and the ScreenCast portal can be reached; false on a system without one.</summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))) return false;
        try
        {
            using var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("no session bus"));
            connection.ConnectAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    // What goes in the arguments of a portal call. A ref struct cannot be captured, so this is a delegate of its own.
    private delegate void Fill(ref MessageWriter writer, string token);

    /// <summary>
    /// Asks the portal for a monitor or a window. The desktop shows its picker (or, on a desktop set up to share without asking,
    /// does not), so this can take as long as the user does; <paramref name="ct"/> gives up. Throws if the user says no.
    /// </summary>
    public static async Task<PortalScreenCast> StartAsync(bool window, bool showCursor, CancellationToken ct = default)
    {
        var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("There is no D-Bus session bus, so the desktop cannot be asked to share the screen."));
        string? session = null;
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            string sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');

            // 1. A session.
            var created = await RequestAsync(connection, sender, "CreateSession", "a{sv}", (ref MessageWriter w, string token) =>
            {
                var options = w.WriteDictionaryStart();
                WriteEntry(ref w, "handle_token", token);
                WriteEntry(ref w, "session_handle_token", "yaparr" + Environment.ProcessId + "s" + Interlocked.Increment(ref _counter));
                w.WriteDictionaryEnd(options);
            }, ct).ConfigureAwait(false);
            session = created.GetValueOrDefault("session_handle").GetString();

            // 2. What to offer: monitors (1) or windows (2), one of them, with the pointer drawn into the picture (2) or left out (1).
            string chosen = session;
            await RequestAsync(connection, sender, "SelectSources", "oa{sv}", (ref MessageWriter w, string token) =>
            {
                w.WriteObjectPath(chosen);
                var options = w.WriteDictionaryStart();
                WriteEntry(ref w, "handle_token", token);
                WriteEntry(ref w, "types", window ? 2u : 1u);
                WriteEntry(ref w, "multiple", false);
                WriteEntry(ref w, "cursor_mode", showCursor ? 2u : 1u);
                w.WriteDictionaryEnd(options);
            }, ct).ConfigureAwait(false);

            // 3. The user chooses.
            var started = await RequestAsync(connection, sender, "Start", "osa{sv}", (ref MessageWriter w, string token) =>
            {
                w.WriteObjectPath(chosen);
                w.WriteString("");
                var options = w.WriteDictionaryStart();
                WriteEntry(ref w, "handle_token", token);
                w.WriteDictionaryEnd(options);
            }, ct).ConfigureAwait(false);

            if (!started.TryGetValue("streams", out var streams) || streams.Count == 0)
                throw new InvalidOperationException("The desktop did not give a picture to share.");
            var stream = streams.GetItem(0);
            uint node = stream.GetItem(0).GetUInt32();
            int width = 0, height = 0;
            var properties = stream.GetItem(1).GetDictionary<string, VariantValue>();
            if (properties.TryGetValue("size", out var size) && size.Count >= 2) { width = size.GetItem(0).GetInt32(); height = size.GetItem(1).GetInt32(); }
            bool isWindow = properties.TryGetValue("source_type", out var type) ? type.GetUInt32() == 2 : window;

            // 4. A connection to PipeWire that can see that stream.
            var message = CreateCall(connection, "OpenPipeWireRemote", "oa{sv}", (ref MessageWriter w, string _) =>
            {
                w.WriteObjectPath(chosen);
                var options = w.WriteDictionaryStart();
                w.WriteDictionaryEnd(options);
            });
            var fd = await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadHandle<SafeFileHandle>(), null).ConfigureAwait(false);

            var cast = new PortalScreenCast(connection, chosen, fd, node, width, height, isWindow);
            cast._closedWatch = await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = chosen, Interface = "org.freedesktop.portal.Session", Member = "Closed" },
                static (Message _, object? _) => 0,
                (Notification<int> n) => { if (n.HasValue && !cast._disposed) cast.Closed?.Invoke(); },
                false, ObserverFlags.None, null).ConfigureAwait(false);
            return cast;
        }
        catch
        {
            if (session != null) CloseSession(connection, session);
            connection.Dispose();
            throw;
        }
    }

    // One portal call that answers later: its result arrives as the Response signal of a request object whose path is known in
    // advance from the token we pick, so the signal is watched before the call is made and cannot be missed.
    private static async Task<Dictionary<string, VariantValue>> RequestAsync(DBusConnection connection, string sender, string member, string signature, Fill fill, CancellationToken ct)
    {
        string token = "yaparr" + Environment.ProcessId + "r" + Interlocked.Increment(ref _counter);
        string path = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        var answer = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Sender = Service, Path = path, Interface = RequestInterface, Member = "Response" },
            static (Message m, object? _) =>
            {
                var reader = m.GetBodyReader();
                uint code = reader.ReadUInt32();
                return (code, reader.ReadDictionaryOfStringToVariantValue());
            },
            (Notification<(uint Code, Dictionary<string, VariantValue> Results)> n) =>
            {
                if (n.HasValue) answer.TrySetResult(n.Value);
                else if (n.IsCompletion) answer.TrySetException(new InvalidOperationException("The connection to the desktop ended before it answered."));
            },
            false, ObserverFlags.None, null).ConfigureAwait(false);

        var message = CreateCall(connection, member, signature, fill, token);
        await connection.CallMethodAsync(message).ConfigureAwait(false);

        using var registration = ct.Register(() => answer.TrySetCanceled(ct));
        var (code, results) = await answer.Task.ConfigureAwait(false);
        if (code == 1) throw new OperationCanceledException("The share was cancelled in the desktop's picker.");
        if (code != 0) throw new InvalidOperationException("The desktop refused to share the screen.");
        return results;
    }

    private static MessageBuffer CreateCall(DBusConnection connection, string member, string signature, Fill fill, string token = "")
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, PortalPath, ScreenCastInterface, member, signature);
            fill(ref writer, token);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private static void WriteEntry(ref MessageWriter w, string key, string value) { w.WriteDictionaryEntryStart(); w.WriteString(key); w.WriteVariantString(value); }
    private static void WriteEntry(ref MessageWriter w, string key, uint value) { w.WriteDictionaryEntryStart(); w.WriteString(key); w.WriteVariantUInt32(value); }
    private static void WriteEntry(ref MessageWriter w, string key, bool value) { w.WriteDictionaryEntryStart(); w.WriteString(key); w.WriteVariantBool(value); }

    private static void CloseSession(DBusConnection connection, string session)
    {
        try
        {
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(Service, session, "org.freedesktop.portal.Session", "Close");
                connection.TrySendMessage(writer.CreateMessage());
            }
            finally { writer.Dispose(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closedWatch?.Dispose();
        CloseSession(_connection, _session);
        PipeWireFd.Dispose();
        _connection.Dispose();
    }
}
