using System.Runtime.InteropServices;

namespace Yaparr.Desktop;

/// <summary>
/// A pop-up in Windows' notification area, made the way programs without a packaged identity do it: a tray icon with a balloon, which
/// Windows 10 and 11 show as an ordinary toast and keep in the notification centre. The icon is added for the pop-up and taken away a
/// few seconds after the last one. Clicking the pop-up brings the window forward. Nothing happens on another system.
/// </summary>
internal static class WindowsToast
{
    public static bool Available => OperatingSystem.IsWindows();

    private const uint AddIcon = 0, ModifyIcon = 1, DeleteIcon = 2, SetVersion = 4;
    private const uint FlagMessage = 1, FlagIcon = 2, FlagTip = 4, FlagInfo = 16;
    private const uint InfoUser = 4, InfoLargeIcon = 0x20;
    private const uint CallbackMessage = 0x8000 + 41, BalloonUserClick = 0x405;
    private const uint IconId = 41;
    private const int GapBeforeRemoval = 8000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static readonly object Gate = new();
    private static readonly SubclassProc Hook = OnMessage;      // kept so it is not collected while Windows holds it
    private static IntPtr _window, _icon;
    private static bool _added, _hooked;
    private static Action? _onClick;
    private static Timer? _removal;

    /// <summary>Shows a pop-up. <paramref name="onClick"/> runs when it is clicked.</summary>
    public static void Show(IntPtr window, string title, string body, Action onClick)
    {
        if (!Available || window == IntPtr.Zero) return;
        lock (Gate)
        {
            _onClick = onClick;
            if (!_hooked && SetWindowSubclass(window, Hook, new UIntPtr(IconId), UIntPtr.Zero)) _hooked = true;
            _window = window;
            if (_icon == IntPtr.Zero) _icon = LoadIcon();

            var data = Data(window);
            data.Flags = FlagMessage | FlagIcon | FlagTip | FlagInfo;
            data.Info = body.Length > 250 ? body[..249] + "…" : body;
            data.InfoTitle = title.Length > 60 ? title[..59] + "…" : title;
            data.InfoFlags = InfoUser | InfoLargeIcon;
            if (!Shell_NotifyIconW(_added ? ModifyIcon : AddIcon, ref data)) { _added = false; return; }
            if (!_added)
            {
                _added = true;
                data.Version = 4;
                Shell_NotifyIconW(SetVersion, ref data);
            }
            _removal?.Dispose();
            _removal = new Timer(_ => Remove(), null, GapBeforeRemoval, Timeout.Infinite);
        }
    }

    /// <summary>The size Windows expects for the record (976 bytes in a 64-bit program); a wrong size makes every call fail.</summary>
    internal static int RecordSize => Marshal.SizeOf<NotifyIconData>();

    private static NotifyIconData Data(IntPtr window) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = window,
        Id = IconId,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "Yaparr",
        Info = "",
        InfoTitle = "",
        BalloonIcon = _icon,
    };

    private static void Remove()
    {
        lock (Gate)
        {
            if (!_added) return;
            var data = Data(_window);
            Shell_NotifyIconW(DeleteIcon, ref data);
            _added = false;
        }
    }

    // The program's own icon, from its .exe.
    private static IntPtr LoadIcon()
    {
        try
        {
            var small = new IntPtr[1];
            var file = Environment.ProcessPath;
            if (file != null && ExtractIconExW(file, 0, null, small, 1) > 0) return small[0];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return IntPtr.Zero;
    }

    private static IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == CallbackMessage && ((uint)(long)lParam & 0xFFFF) == BalloonUserClick)
        {
            Action? click;
            lock (Gate) click = _onClick;
            click?.Invoke();
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }
}
