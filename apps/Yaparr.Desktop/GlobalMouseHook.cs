using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yaparr.Desktop;

/// <summary>
/// Reports when the middle, back or forward button of the mouse goes down and up anywhere in Windows, for push-to-talk while another program
/// has the focus. It only listens: the click still reaches the program in front.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GlobalMouseHook : IDisposable
{
    private const int WhMouseLl = 14, WmMButtonDown = 0x207, WmMButtonUp = 0x208, WmXButtonDown = 0x20B, WmXButtonUp = 0x20C;

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc proc, nint module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    private readonly int _button;
    private readonly Action<bool> _changed;
    private readonly HookProc _proc; // kept in a field so that it is not collected while Windows holds on to it
    private nint _hook;
    private bool _held;

    /// <param name="button">3 for the middle button, 4 for back, 5 for forward (<see cref="PushToTalkTrigger"/>).</param>
    /// <param name="changed">Called on the thread that made the hook with true when the button goes down and false when it comes up.</param>
    public GlobalMouseHook(int button, Action<bool> changed)
    {
        _button = button;
        _changed = changed;
        _proc = Callback;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        if (_hook == 0) throw new InvalidOperationException("Windows did not allow listening for the mouse button (error " + Marshal.GetLastWin32Error() + ").");
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && ButtonAndDirection((int)wParam, lParam) is var (button, down) && button == _button)
        {
            if (down) { if (!_held) { _held = true; _changed(true); } }
            else if (_held) { _held = false; _changed(false); }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // For an extra button the high word of the record's mouseData (after the 8 bytes of the position) says which one: 1 back, 2 forward.
    private static (int Button, bool Down)? ButtonAndDirection(int message, nint lParam) => message switch
    {
        WmMButtonDown => (PushToTalkTrigger.Middle, true),
        WmMButtonUp => (PushToTalkTrigger.Middle, false),
        WmXButtonDown or WmXButtonUp => ((Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF) switch
        {
            1 => (PushToTalkTrigger.Back, message == WmXButtonDown),
            2 => (PushToTalkTrigger.Forward, message == WmXButtonDown),
            _ => null,
        },
        _ => null,
    };

    public void Dispose()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
    }
}
