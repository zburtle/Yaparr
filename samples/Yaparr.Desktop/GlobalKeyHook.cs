using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yaparr.Desktop;

/// <summary>
/// Reports when one key goes down and up anywhere in Windows, for push-to-talk while another program has the focus. It only listens: the key
/// still reaches the program in front. Windows does not show keys pressed in a program running as administrator to one that is not.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GlobalKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13, WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;

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

    private readonly uint _key;
    private readonly Action<bool> _changed;
    private readonly HookProc _proc; // kept in a field so that it is not collected while Windows holds on to it
    private nint _hook;
    private bool _held;

    /// <param name="virtualKey">The Windows virtual-key code of the key.</param>
    /// <param name="changed">Called on the thread that made the hook with true when the key goes down and false when it comes up.</param>
    public GlobalKeyHook(uint virtualKey, Action<bool> changed)
    {
        _key = virtualKey;
        _changed = changed;
        _proc = Callback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == 0) throw new InvalidOperationException("Windows did not allow listening for the key (error " + Marshal.GetLastWin32Error() + ").");
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        // The first field of the structure Windows passes is the key code.
        if (code >= 0 && (uint)Marshal.ReadInt32(lParam) == _key)
        {
            int message = (int)wParam;
            if (message is WmKeyDown or WmSysKeyDown)
            {
                // Holding a key repeats the message; only the first one counts.
                if (!_held) { _held = true; _changed(true); }
            }
            else if (message is WmKeyUp or WmSysKeyUp && _held)
            {
                _held = false;
                _changed(false);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
    }
}
