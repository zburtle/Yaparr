// Shares this computer's screen to a second Yaparr node in the same process and reports what it cost: how many pictures
// arrived, the bitrate, and the processor time used by FFmpeg and by this program. Windows only (screen capture).
//
//   Yaparr.ScreenDemo [--seconds 20] [--fps 10] [--kbps 800] [--width 1280] [--height 720] [--monitor 0|1|..|all] [--window "title"] [--window-index N | --list-windows] [--save picture.bmp]
//   Without --monitor or --window the primary monitor is shared.
//
// FFmpeg is found through YAPARR_FFMPEG, an 'ffmpeg' folder next to this program, or the PATH.

using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Screen;
using Yaparr.Transport;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
int Int(string name, int fallback) => int.TryParse(Opt(name), out var v) ? v : fallback;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Sharing a screen works on Windows only for now."); return 2; }
var ffmpeg = FfmpegLocator.Find();
if (ffmpeg == null) { Console.Error.WriteLine("FFmpeg was not found. Set YAPARR_FFMPEG to ffmpeg.exe, or put it in an 'ffmpeg' folder next to this program."); return 2; }
Console.WriteLine($"FFmpeg: {ffmpeg}");

int seconds = Int("--seconds", 20);
var screen = new ScreenOptions { Fps = Int("--fps", 10), Kbps = Int("--kbps", 800), MaxWidth = Int("--width", 1280), MaxHeight = Int("--height", 720), WindowTitle = Opt("--window") };
string? savePath = Opt("--save");
var displays = Displays.List();
Console.WriteLine("Monitors: " + string.Join(", ", displays.Select(d => $"{d.Index}: {d.Width}x{d.Height}{(d.Primary ? " (primary)" : "")}")));
var windows = ShareWindows.List(Environment.ProcessId);
if (args.Contains("--list-windows"))
{
    for (int i = 0; i < windows.Count; i++) Console.WriteLine($"{i}: {windows[i].Label}");
    return 0;
}
if (Opt("--window-index") is { } wi && int.TryParse(wi, out int windowIndex))
{
    var w = windows.ElementAtOrDefault(windowIndex) ?? throw new InvalidOperationException("No such window; use --list-windows.");
    Console.WriteLine("Sharing the window: " + w.Label);
    screen = new ScreenOptions { Fps = screen.Fps, Kbps = screen.Kbps, MaxWidth = screen.MaxWidth, MaxHeight = screen.MaxHeight, WindowHandle = w.Handle };
}
else if (Opt("--monitor") is { } m)
{
    if (m == "all") screen = new ScreenOptions { Fps = screen.Fps, Kbps = screen.Kbps, MaxWidth = screen.MaxWidth, MaxHeight = screen.MaxHeight, WindowTitle = screen.WindowTitle };
    else screen = new ScreenOptions { Fps = screen.Fps, Kbps = screen.Kbps, MaxWidth = screen.MaxWidth, MaxHeight = screen.MaxHeight, WindowTitle = screen.WindowTitle,
        Display = displays.FirstOrDefault(d => d.Index.ToString() == m) ?? throw new InvalidOperationException("No such monitor.") };
}
else if (displays.Count > 0 && screen.WindowTitle == null && screen.WindowHandle == null)
    screen = new ScreenOptions { Fps = screen.Fps, Kbps = screen.Kbps, MaxWidth = screen.MaxWidth, MaxHeight = screen.MaxHeight, Display = displays[0] };

ChatNode Make(string name) => new(new ChatNodeOptions
{
    DisplayName = name,
    EnableLocalDiscovery = false,
    DiscoveryFactory = mux => new LocalFileDiscovery(mux.LocalPort),
    DiscoveryInterval = TimeSpan.FromSeconds(2),
    Traversal = new TraversalOptions { Enabled = false, PortMapping = false, UseDefaultRelay = false },
});

using var sharerNode = Make("sharer");
using var viewerNode = Make("viewer");
sharerNode.Start();
viewerNode.Start();
var key = RoomKey.Parse(System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
var sharerRoom = await sharerNode.JoinRoomAsync(key, "demo");
var viewerRoom = await viewerNode.JoinRoomAsync(key, "demo");

Console.Write("Waiting for the two nodes to find each other");
var wait = Stopwatch.StartNew();
while (viewerNode.ConnectedPeers.Count == 0 && wait.Elapsed < TimeSpan.FromSeconds(30)) { Console.Write('.'); await Task.Delay(500); }
Console.WriteLine();
if (viewerNode.ConnectedPeers.Count == 0) { Console.Error.WriteLine("They did not connect."); return 1; }

using var sharer = ScreenSharer.Start(sharerNode, sharerRoom, "Demo screen", screen, ffmpeg);
sharer.Ended += ex => { if (ex != null) Console.Error.WriteLine("Sharing stopped: " + ex.Message); };

wait.Restart();
MediaGroupInfo? group = null;
while ((group = viewerRoom.MediaGroups.FirstOrDefault()) == null && wait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(200);
if (group == null) { Console.Error.WriteLine("The viewer never saw the share."); return 1; }

using var view = viewerNode.WatchMedia(viewerRoom, group.Id);
using var viewer = new ScreenViewer(view, ffmpeg);
Picture? last = null;
long firstPictureMs = -1;
var clock = Stopwatch.StartNew();
viewer.PictureReceived += p => { if (firstPictureMs < 0) firstPictureMs = clock.ElapsedMilliseconds; last = p; };
viewer.Failed += ex => Console.Error.WriteLine("Viewing stopped: " + ex.Message);

TimeSpan Cpu() => Process.GetProcessesByName("ffmpeg").Aggregate(TimeSpan.Zero, (t, p) => { try { return t + p.TotalProcessorTime; } catch (Exception) { return t; } }) + Process.GetCurrentProcess().TotalProcessorTime;
var cpu0 = Cpu();
long picturesBefore = 0, bytesBefore = sharer.Bytes;
Console.WriteLine($"Sharing {screen.Fps} fps, {screen.Kbps} kbit/s, up to {screen.MaxWidth}x{screen.MaxHeight} for {seconds} s...");
for (int s = 1; s <= seconds; s++)
{
    await Task.Delay(1000);
    long pics = viewer.Pictures;
    Console.WriteLine($"  {s,3} s: sent {sharer.Frames} frames ({sharer.KeyFrames} key), {(sharer.Bytes - bytesBefore) * 8 / 1000} kbit in the last second, viewer has {pics} pictures (+{pics - picturesBefore})");
    picturesBefore = pics;
    bytesBefore = sharer.Bytes;
}
var cpu = Cpu() - cpu0;

Console.WriteLine();
Console.WriteLine($"First picture after {firstPictureMs} ms");
Console.WriteLine($"Sent {sharer.Frames} frames, {sharer.Bytes * 8 / 1000 / seconds} kbit/s on average ({sharer.KeyFrames} key frames)");
Console.WriteLine($"Viewer got {viewer.Pictures} pictures ({viewer.Pictures / (double)seconds:0.0} per second), dropped {viewer.Dropped}, lost {view.FramesLost}");
Console.WriteLine($"Processor time used by FFmpeg and this program: {cpu.TotalSeconds:0.0} s over {seconds} s = {cpu.TotalSeconds / seconds * 100:0}% of one core");
if (last is { } pic && savePath != null)
{
    BmpWriter.Save(savePath, pic);
    Console.WriteLine($"Last picture ({pic.Width}x{pic.Height}) saved to {savePath}");
}
return viewer.Pictures > 0 ? 0 : 1;

internal static class BmpWriter
{
    public static void Save(string path, Picture p)
    {
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        int size = p.Width * p.Height * 4;
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + size); w.Write(0); w.Write(54);
        w.Write(40); w.Write(p.Width); w.Write(-p.Height); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(size); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        w.Write(p.Bgra);
    }
}
