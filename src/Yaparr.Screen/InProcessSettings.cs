using System.Diagnostics;

namespace Yaparr.Screen;

/// <summary>Where the pictures of a share go between the capture and the encoder, both of which run inside this program (see <see cref="InProcessCapture"/> and <see cref="InProcessEncoder"/>).</summary>
public enum InProcessMode
{
    /// <summary>The pictures are copied to memory and converted by the processor.</summary>
    CaptureOnCpu,
    /// <summary>The pictures stay on the graphics card from the capture to the encoder (Windows, NVIDIA's and AMD's encoders).</summary>
    CaptureOnCard,
}

/// <summary>
/// Which <see cref="InProcessMode"/> a share starts with. On Windows the pictures stay on the graphics card wherever that can be done (an encoder that reads them
/// there, a size known up front), and the environment variable YAPARR_INPROCESS changes it: <c>capture-cpu</c> for the pictures copied to memory, <c>capture</c>
/// for the default spelled out (which <see cref="InProcessGuard"/> does not then turn off). On Linux the pictures are in memory: the X11 capture is <c>x11grab</c>
/// through the libraries, and on Wayland GStreamer reads the desktop's stream. There is no separate FFmpeg program: without the libraries there is no share.
/// </summary>
public static class InProcessSettings
{
    /// <summary>The mode and whether it was asked for in so many words (a person who asks is not stopped by <see cref="InProcessGuard"/>).</summary>
    public static (InProcessMode Mode, bool Asked) Parse(string? value, bool windows)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "capture-cpu": return (InProcessMode.CaptureOnCpu, true);
            case "capture" or "on" or "auto" or "true": return (windows ? InProcessMode.CaptureOnCard : InProcessMode.CaptureOnCpu, true);
            default: return (windows ? InProcessMode.CaptureOnCard : InProcessMode.CaptureOnCpu, false);
        }
    }

    /// <summary>
    /// Whether decoding on the graphics card was asked for in so many words (<c>YAPARR_CARD_DECODE=1</c>): then <see cref="InProcessDecodeGuard"/> does not keep a
    /// viewer on the processor after the program ended in the middle of decoding.
    /// </summary>
    public static bool CardDecodeAsked => Environment.GetEnvironmentVariable("YAPARR_CARD_DECODE")?.Trim() is "1" or "on" or "yes" or "true";

    /// <summary>The mode from the environment of this program.</summary>
    public static (InProcessMode Mode, bool Asked) FromEnvironment() => Parse(Environment.GetEnvironmentVariable("YAPARR_INPROCESS"), OperatingSystem.IsWindows());
}

/// <summary>
/// Sharing and watching run inside this program, without what a separate program gives: when native code crashes, the program goes with it. So that a computer
/// where it does (a driver, a card, a window of a kind nobody tried) does not lose the program every time, a share (or a decoder) that starts leaves a mark and
/// takes it away when it ends; a mark found at the next start means the program ended in the middle of one, and after <see cref="Limit"/> of those in a row the
/// graphics card is left out (the pictures of a share are copied to memory; a viewer decodes on the processor), a driver being the likeliest cause, until one ends
/// well. A program that is stopped from the task manager in the middle of a share counts the same, which is why it takes two.
/// </summary>
public sealed class NativeGuard
{
    /// <summary>Endings in the middle of an in-process share, one after the other, that turn the default off.</summary>
    public const int Limit = 2;

    public NativeGuard(string fileName) => _fileName = fileName;

    private readonly object Gate = new();
    private string? _file;
    private readonly string _fileName;
    private int _running;   // shares or decoders that are going on at this moment in this program
    private bool _loaded;
    private int _unclean;

    /// <summary>Where the mark is kept (one for the person on this computer, whichever profile of Yaparr runs).</summary>
    public string MarkFile => _file ??= Path.Combine(Path.GetDirectoryName(FfmpegInstaller.DefaultDirectory)!, _fileName);

    /// <summary>Uses another file (for tests), and reads it again.</summary>
    public void UseFile(string? path)
    {
        lock (Gate) { _file = path; _loaded = false; _unclean = 0; _running = 0; }
    }

    /// <summary>Whether shares may go on running here, as far as the endings of earlier ones say.</summary>
    public bool Allowed { get { Load(); lock (Gate) return _unclean < Limit; } }

    /// <summary>How many endings in the middle of a share, in a row, there have been.</summary>
    public int UncleanEndings { get { Load(); lock (Gate) return _unclean; } }

    /// <summary>A share (or a decoder) is starting here. Call <see cref="End"/> when it ends, however it ends. Any number can be going on at once.</summary>
    public void Begin()
    {
        Load();
        lock (Gate) { if (_running++ == 0) Save(_unclean, Environment.ProcessId); }
    }

    /// <summary>One ended, with the program still there to say so: when it was the last, whatever happened before is forgiven.</summary>
    public void End()
    {
        Load();
        lock (Gate)
        {
            if (_running > 0 && --_running > 0) return;   // another is still going on: the mark stays
            _unclean = 0; Save(0, 0);
        }
    }

    private void Load()
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

    private void Save(int unclean, int active)
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


/// <summary>The guard of shares (see <see cref="NativeGuard"/>): when it is closed, the pictures are not kept on the graphics card (unless <c>YAPARR_INPROCESS=capture</c> asks for it).</summary>
public static class InProcessGuard
{
    private static readonly NativeGuard Guard = new("in-process-guard.txt");
    public const int Limit = NativeGuard.Limit;
    public static string MarkFile => Guard.MarkFile;
    public static void UseFile(string? path) => Guard.UseFile(path);
    public static bool Allowed => Guard.Allowed;
    public static int UncleanEndings => Guard.UncleanEndings;
    public static void Begin() => Guard.Begin();
    public static void End() => Guard.End();
}

/// <summary>
/// The guard of viewers (<see cref="InProcessDecoder"/>), kept apart from the shares': a program that ends in the middle of decoding does not turn off sharing,
/// and the other way round. A viewer, a preview or a share's own preview counts while its decoder runs; after <see cref="NativeGuard.Limit"/> endings in the middle
/// of decoding in a row, viewers decode on the processor, not the graphics card (a driver being the likeliest cause), until a decoder ends well (or
/// <c>YAPARR_CARD_DECODE=1</c> asks for the card in so many words). There is no separate program to fall back to: decoding is always done here.
/// </summary>
public static class InProcessDecodeGuard
{
    private static readonly NativeGuard Guard = new("in-process-decode-guard.txt");
    public static string MarkFile => Guard.MarkFile;
    public static void UseFile(string? path) => Guard.UseFile(path);
    public static bool Allowed => Guard.Allowed;
    public static int UncleanEndings => Guard.UncleanEndings;
    public static void Begin() => Guard.Begin();
    public static void End() => Guard.End();
}
