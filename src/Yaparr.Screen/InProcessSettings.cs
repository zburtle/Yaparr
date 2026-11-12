using System.Diagnostics;

namespace Yaparr.Screen;

/// <summary>How much of a share is done inside this program with FFmpeg's libraries (see <see cref="InProcessCapture"/> and <see cref="InProcessEncoder"/>).</summary>
public enum InProcessMode
{
    /// <summary>A separate FFmpeg captures, scales and encodes.</summary>
    Off,
    /// <summary>A separate FFmpeg captures; the encoding is done here (a key frame can be made on the very next picture).</summary>
    Encoder,
    /// <summary>Capture and encoding here; the pictures are copied to memory and converted by the processor.</summary>
    CaptureOnCpu,
    /// <summary>Capture and encoding here; the pictures stay on the graphics card from the capture to the encoder.</summary>
    CaptureOnCard,
}

/// <summary>
/// Which <see cref="InProcessMode"/> a share starts with. On Windows it is the whole of it (capture and encoding here, the pictures on the card) wherever
/// that can be done (FFmpeg's libraries next to the program, an NVIDIA card; anything else is a share as before, with a separate FFmpeg), and the
/// environment variable YAPARR_INPROCESS changes it: <c>0</c> (or <c>off</c>) for a separate FFmpeg only, <c>1</c> for the encoder here only,
/// <c>capture-cpu</c> for capture here with the pictures copied to memory, <c>capture</c> for the default spelled out. Elsewhere it is off.
/// </summary>
public static class InProcessSettings
{
    /// <summary>The mode and whether it was asked for in so many words (a person who asks is not stopped by <see cref="InProcessGuard"/>).</summary>
    public static (InProcessMode Mode, bool Asked) Parse(string? value, bool windows)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "0" or "off" or "no" or "false": return (InProcessMode.Off, true);
            case "1" or "encoder": return (InProcessMode.Encoder, true);
            case "capture-cpu": return (InProcessMode.CaptureOnCpu, true);
            case "capture" or "on" or "auto" or "true": return (windows ? InProcessMode.CaptureOnCard : InProcessMode.Encoder, true);
            default: return (windows ? InProcessMode.CaptureOnCard : InProcessMode.Off, false);
        }
    }

    /// <summary>The mode from the environment of this program.</summary>
    public static (InProcessMode Mode, bool Asked) FromEnvironment() => Parse(Environment.GetEnvironmentVariable("YAPARR_INPROCESS"), OperatingSystem.IsWindows());
}

/// <summary>
/// A share that runs inside this program has lost what a separate FFmpeg gives: when native code crashes, the program goes with it. So that a computer
/// where it does (a driver, a card, a window of a kind nobody tried) does not lose the program every time it shares, a share that starts here leaves a
/// mark and takes it away when it ends; a mark found at the next start means the program ended during such a share, and after <see cref="Limit"/> of
/// those in a row the shares go back to a separate FFmpeg until one ends well (or <c>YAPARR_INPROCESS</c> asks for it in so many words). A program
/// that is stopped from the task manager in the middle of a share counts the same, which is why it takes two.
/// </summary>
public static class InProcessGuard
{
    /// <summary>Endings in the middle of an in-process share, one after the other, that turn the default off.</summary>
    public const int Limit = 2;

    private static readonly object Gate = new();
    private static string? _file;
    private static bool _loaded;
    private static int _unclean;

    /// <summary>Where the mark is kept (one for the person on this computer, whichever profile of Yaparr runs).</summary>
    public static string MarkFile => _file ??= Path.Combine(Path.GetDirectoryName(FfmpegInstaller.DefaultDirectory)!, "in-process-guard.txt");

    /// <summary>Uses another file (for tests), and reads it again.</summary>
    public static void UseFile(string? path)
    {
        lock (Gate) { _file = path; _loaded = false; _unclean = 0; }
    }

    /// <summary>Whether shares may go on running here, as far as the endings of earlier ones say.</summary>
    public static bool Allowed { get { Load(); lock (Gate) return _unclean < Limit; } }

    /// <summary>How many endings in the middle of a share, in a row, there have been.</summary>
    public static int UncleanEndings { get { Load(); lock (Gate) return _unclean; } }

    /// <summary>A share is starting here. Call <see cref="End"/> when it ends, however it ends.</summary>
    public static void Begin()
    {
        Load();
        lock (Gate) Save(_unclean, Environment.ProcessId);
    }

    /// <summary>The share ended, with the program still there to say so: whatever happened before is forgiven.</summary>
    public static void End()
    {
        Load();
        lock (Gate) { _unclean = 0; Save(0, 0); }
    }

    private static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var path = MarkFile;
                if (!System.IO.File.Exists(path)) return;
                int unclean = 0, active = 0;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    if (parts[0] == "unclean") int.TryParse(parts[1], out unclean);
                    else if (parts[0] == "active") int.TryParse(parts[1], out active);
                }
                // A mark left by a program that is gone: it ended in the middle of a share. One that is still running (another profile of Yaparr,
                // sharing at this moment) has not ended in the middle of anything.
                if (active != 0 && active != Environment.ProcessId && !Exists(active)) { unclean++; Save(unclean, 0); }
                _unclean = unclean;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Exists(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void Save(int unclean, int active)
    {
        try
        {
            var path = MarkFile;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            System.IO.File.WriteAllText(path, $"unclean={unclean}{Environment.NewLine}active={active}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
