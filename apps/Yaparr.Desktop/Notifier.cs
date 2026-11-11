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
    private static readonly Dictionary<string, float[]> MessageClips = new()
    {
        ["Chime"] = Notes((1319, 70), (1976, 120)),
        ["Soft"] = Notes((880, 110), (659, 170)),
        ["Pop"] = Notes((1568, 40), (1047, 80)),
        ["Bell"] = Notes((1175, 60), (1568, 60), (2093, 170)),
        ["Drop"] = Notes((988, 60), (740, 60), (494, 150)),
    };

    /// <summary>The sounds a new message can make.</summary>
    public static IReadOnlyList<string> MessageStyles { get; } = MessageClips.Keys.ToList();

    /// <summary>Which of <see cref="MessageStyles"/> a new message makes; an unknown name means the first.</summary>
    public string MessageStyle { get; set; } = "Chime";
    // Being named or answered: three rising notes, more insistent than any of the message sounds so it can be told apart.
    private static readonly float[] MentionClip = Notes((1047, 70), (1319, 70), (1760, 160));
    // The push-to-talk key went down: one short, soft tick, so that you know the microphone is open without looking.
    private static readonly float[] TalkClip = Notes((1397, 35));
    private static readonly float[] JoinedClip = Notes((523, 90), (784, 130));
    private static readonly float[] LeftClip = Notes((784, 90), (523, 130));
    private static readonly float[] YouJoinedClip = Notes((523, 80), (659, 80), (784, 130));
    // The connection was lost (low and falling, a little longer than the others) and is back (rising in three steps).
    private static readonly float[] ConnectionLostClip = Notes((392, 110), (294, 110), (220, 220));
    private static readonly float[] ConnectionBackClip = Notes((587, 70), (784, 70), (1047, 150));
    // Sharing. Someone starts a share in your call: a double tap, then a rising note. Someone you were watching stops: falling. Someone starts or
    // stops watching yours: a short pair going up or down. A (0, ms) note is a rest.
    private static readonly float[] ShareStartedClip = Notes((880, 60), (0, 40), (880, 60), (1319, 150));
    private static readonly float[] ShareEndedClip = Notes((988, 70), (740, 70), (587, 160));
    private static readonly float[] ViewerJoinedClip = Notes((1175, 50), (1568, 90));
    private static readonly float[] ViewerLeftClip = Notes((1568, 50), (1175, 90));
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

    public void Message() => Play(MessageClips.GetValueOrDefault(MessageStyle) ?? MessageClips["Chime"], throttled: true);

    /// <summary>The sound of being named or answered. It is never held back by the gap between message sounds.</summary>
    public void Mentioned() => Play(MentionClip, throttled: false);

    /// <summary>The push-to-talk key went down. It has a setting of its own, so it plays even when the other sounds are off.</summary>
    public void PushToTalk() => Play(TalkClip, throttled: false, force: true);

    /// <summary>Plays a message sound to choose from, even when sounds are off.</summary>
    public void Preview(string style)
    {
        if (MessageClips.TryGetValue(style, out var clip)) Play(clip, throttled: false, force: true);
    }
    public void SomeoneJoined() => Play(JoinedClip, throttled: false);
    public void SomeoneLeft() => Play(LeftClip, throttled: false);
    public void YouJoined() => Play(YouJoinedClip, throttled: false);
    public void YouLeft() => Play(YouLeftClip, throttled: false);
    /// <summary>Someone else started sharing a screen in the call you are in.</summary>
    public void ShareStarted() => Play(ShareStartedClip, throttled: false);
    /// <summary>A screen you were watching ended.</summary>
    public void ShareEnded() => Play(ShareEndedClip, throttled: false);
    /// <summary>Someone started watching the screen you share.</summary>
    public void ViewerJoined() => Play(ViewerJoinedClip, throttled: false);
    /// <summary>Someone stopped watching the screen you share.</summary>
    public void ViewerLeft() => Play(ViewerLeftClip, throttled: false);
    public void ConnectionLost() => Play(ConnectionLostClip, throttled: false);
    public void ConnectionBack() => Play(ConnectionBackClip, throttled: false);

    private void Play(float[] clip, bool throttled, bool force = false)
    {
        if (!Enabled && !force) return;
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

    // Windows flashes the button; on Linux under X11 the window manager is asked to mark the window as wanting attention.
    private static void Send(Window window, uint flags)
    {
        if (OperatingSystem.IsLinux())
        {
            if (window.TryGetPlatformHandle() is { HandleDescriptor: "XID", Handle: var xid }) LinuxDesktop.DemandAttention(xid, flags != FlashStop);
            return;
        }
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
