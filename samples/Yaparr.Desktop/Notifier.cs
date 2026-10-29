using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Yaparr.Desktop;

/// <summary>Short sounds for things that happen in the app. They are made from a few notes here, so there are no files to ship.</summary>
internal sealed class UiSounds
{
    private const int Rate = 48000;
    private const long MinGapMs = 700;

    // (frequency in Hz, length in ms)
    private static readonly float[] MessageClip = Notes((1319, 70), (1976, 120));
    private static readonly float[] JoinedClip = Notes((523, 90), (784, 130));
    private static readonly float[] LeftClip = Notes((784, 90), (523, 130));
    private static readonly float[] YouJoinedClip = Notes((523, 80), (659, 80), (784, 130));
    private static readonly float[] YouLeftClip = Notes((784, 80), (659, 80), (523, 130));

    private readonly Func<string?> _output;
    private readonly Action<string>? _log;
    private long _lastAt;

    public UiSounds(Func<string?> output, Action<string>? log)
    {
        _output = output;
        _log = log;
    }

    public bool Enabled { get; set; } = true;

    public void Message() => Play(MessageClip, throttled: true);
    public void SomeoneJoined() => Play(JoinedClip, throttled: false);
    public void SomeoneLeft() => Play(LeftClip, throttled: false);
    public void YouJoined() => Play(YouJoinedClip, throttled: false);
    public void YouLeft() => Play(YouLeftClip, throttled: false);

    private void Play(float[] clip, bool throttled)
    {
        if (!Enabled) return;
        if (throttled)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Exchange(ref _lastAt, now) < MinGapMs) return;
        }
        var output = _output();
        // Opening a sound device takes a moment, so it is done off the UI thread.
        _ = Task.Run(() =>
        {
            try { VoiceAudio.PlayOnce(clip, output); }
            catch (Exception ex) { _log?.Invoke($"could not play a sound: {ex.Message}"); }
        });
    }

    // A sequence of soft notes, each with a quick fade in and a smooth fade out so there are no clicks.
    private static float[] Notes(params (double Hz, int Ms)[] notes)
    {
        var all = new List<float>();
        foreach (var (hz, ms) in notes)
        {
            int count = Rate * ms / 1000;
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / count;
                double envelope = Math.Min(1, i / (Rate * 0.004)) * Math.Pow(1 - t, 1.5);
                all.Add((float)(0.22 * envelope * Math.Sin(2 * Math.PI * hz * i / Rate)));
            }
        }
        return all.ToArray();
    }
}

/// <summary>Makes the program's button in the taskbar flash until the window is brought to the front.</summary>
internal static class TaskbarFlash
{
    private const uint FlashStop = 0, FlashTray = 2, FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    public static void Start(Window window) => Send(window, FlashTray | FlashUntilForeground);

    public static void Stop(Window window) => Send(window, FlashStop);

    // Only Windows is covered; other systems have their own ways to ask for attention.
    private static void Send(Window window, uint flags)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = flags, Count = uint.MaxValue };
            FlashWindowEx(ref info);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
    }
}
