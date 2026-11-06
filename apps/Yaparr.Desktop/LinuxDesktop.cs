using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace Yaparr.Desktop;

/// <summary>
/// What Avalonia has no call for on Linux, done through the desktop's own services: a pop-up (org.freedesktop.Notifications, which
/// Flatpak lets through), the unread count on the launcher icon (the Unity launcher signal that KDE and the Ubuntu dock read) and
/// the window manager's request for attention on X11. Every call is best effort and does nothing on another system or when the
/// service is not there.
/// </summary>
internal static class LinuxDesktop
{
    private static uint _lastNotification;
    private static long _lastCount = -1;
    private static bool _failed;

    public static bool Available => OperatingSystem.IsLinux();

    private static string DesktopId => Updater.IsFlatpak ? "com.yaparr.Yaparr.desktop" : "yaparr.desktop";
    private static string IconName => Updater.IsFlatpak ? "com.yaparr.Yaparr" : "yaparr";

    /// <summary>Shows a pop-up. A later one replaces an earlier one that is still showing, so a busy room does not pile them up.</summary>
    public static void Notify(string title, string body, Action<string>? log = null)
    {
        if (!Available) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var connection = DBusConnection.Session;
                var message = NotifyMessage(connection, title, body, _lastNotification);
                _lastNotification = await connection.CallMethodAsync(message, (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null).ConfigureAwait(false);
            }
            catch (Exception ex) { Failed("show a pop-up", ex, log); }
        });
    }

    // The message is built apart from the call because the writer cannot be kept across an await.
    private static MessageBuffer NotifyMessage(DBusConnection connection, string title, string body, uint replaces)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.Notifications", path: "/org/freedesktop/Notifications",
            @interface: "org.freedesktop.Notifications", member: "Notify", signature: "susssasa{sv}i");
        writer.WriteString("Yaparr");
        writer.WriteUInt32(replaces);
        writer.WriteString(IconName);
        writer.WriteString(title);
        writer.WriteString(body);
        writer.WriteArray(Array.Empty<string>());
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["desktop-entry"] = DesktopId[..^".desktop".Length] });
        writer.WriteInt32(-1);
        return writer.CreateMessage();
    }

    /// <summary>Puts a number on the launcher icon; 0 takes it off.</summary>
    public static void SetUnread(int count, Action<string>? log = null)
    {
        if (!Available || count == _lastCount) return;
        _lastCount = count;
        _ = Task.Run(() =>
        {
            try
            {
                var connection = DBusConnection.Session;
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(path: "/com/canonical/unity/launcherentry/yaparr", @interface: "com.canonical.Unity.LauncherEntry",
                    member: "Update", signature: "sa{sv}");
                writer.WriteString("application://" + DesktopId);
                writer.WriteDictionary(new Dictionary<string, VariantValue> { ["count"] = (long)count, ["count-visible"] = count > 0 });
                connection.TrySendMessage(writer.CreateMessage());
            }
            catch (Exception ex) { Failed("set the unread count", ex, log); }
        });
    }

    private static void Failed(string what, Exception ex, Action<string>? log)
    {
        if (_failed) return;
        _failed = true;
        log?.Invoke($"could not {what} through the desktop: {ex.Message}");
    }

    // ---- X11: ask the window manager to draw attention to the window ----------------------------------------------

    private const int ClientMessage = 33;
    private const long SubstructureRedirect = 1 << 20, SubstructureNotify = 1 << 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int Type;
        public ulong Serial;
        public int SendEvent;
        public IntPtr Display;
        public ulong Window;
        public ulong MessageType;
        public int Format;
        public long Data0, Data1, Data2, Data3, Data4;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern ulong XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern ulong XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XSendEvent(IntPtr display, ulong window, bool propagate, long mask, IntPtr ev);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);

    /// <summary>Sets or clears "demands attention" on an X11 window, which a taskbar shows as a flashing or highlighted button. Nothing on Wayland.</summary>
    public static void DemandAttention(IntPtr window, bool on, Action<string>? log = null)
    {
        if (!Available || window == IntPtr.Zero || Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return;
        IntPtr display = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            var ev = new XClientMessageEvent
            {
                Type = ClientMessage,
                Display = display,
                Window = (ulong)window,
                MessageType = XInternAtom(display, "_NET_WM_STATE", false),
                Format = 32,
                Data0 = on ? 1 : 0,                                                       // _NET_WM_STATE_ADD or _REMOVE
                Data1 = (long)XInternAtom(display, "_NET_WM_STATE_DEMANDS_ATTENTION", false),
                Data3 = 1,                                                                // from an application
            };
            buffer = Marshal.AllocHGlobal(192);                                           // the size of the XEvent union
            for (int i = 0; i < 192; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.StructureToPtr(ev, buffer, false);
            XSendEvent(display, XDefaultRootWindow(display), false, SubstructureRedirect | SubstructureNotify, buffer);
            XFlush(display);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Failed("ask for attention", ex, log);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (display != IntPtr.Zero) XCloseDisplay(display);
        }
    }
}
