// Shares this computer's screen to a second Yaparr node in the same process and reports what it cost: how many pictures
// arrived, the bitrate, and the processor time this program used (the capture, encoding and decoding all run in it). Windows, and Linux under X11 (monitors only).
//
//   Yaparr.ScreenDemo [--seconds 20] [--fps 10] [--kbps 800] [--width 1280] [--height 720] [--monitor N | --window N] [--codec auto|vp8|h264] [--encoder nvenc|amf|qsv] [--save picture.bmp]
//   Yaparr.ScreenDemo --list     shows the monitors and windows and their numbers
//   On Wayland the desktop asks what to share; --portal-window offers windows instead of monitors.
//   Yaparr.ScreenDemo --install-ffmpeg <folder>     downloads the pinned FFmpeg libraries into the folder (the installer build bundles them)
//   --gpu-decode makes the viewer decode H.264 on the graphics card (Direct3D 11) where it can; by default it decodes on the processor.
//   Without --monitor or --window the primary monitor is shared.
//
// FFmpeg's libraries are found through YAPARR_FFMPEG_LIBS, an 'ffmpeg' folder next to this program, or the package the app downloaded.

using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Screen;
using Yaparr.Transport;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
int Int(string name, int fallback) => int.TryParse(Opt(name), out var v) ? v : fallback;

// Used by the installer build: fetches the pinned FFmpeg libraries (hash checked) into the folder, as <folder>\<version>\avcodec-63.dll and the others.
if (Opt("--install-ffmpeg") is { } installDirectory)
{
    var installed = await FfmpegInstaller.InstallAsync(FfmpegPackage.ForThisSystem ?? throw new PlatformNotSupportedException("There is no FFmpeg download for this system."), directory: installDirectory);
    Console.WriteLine("Installed " + installed);
    return 0;
}

if (ScreenCapture.WhyNotSupported is { } unsupported) { Console.Error.WriteLine(unsupported); return 2; }
if (!FfmpegLibraries.TryLoad(out var noLibraries)) { Console.Error.WriteLine(noLibraries); return 2; }
Console.WriteLine($"FFmpeg's libraries: {FfmpegLibraries.Describe()}");

int seconds = Int("--seconds", 20);
var displays = Displays.List();
var windows = ShareWindows.List(Environment.ProcessId);
if (args.Contains("--list") && ScreenCapture.UsesPortal)
{
    Console.WriteLine("On Wayland the desktop shows its own picker, so there is no list here. Share a monitor with no option, or a window with --portal-window.");
    return 0;
}
if (args.Contains("--list"))
{
    Console.WriteLine("Monitors:");
    foreach (var d in displays) Console.WriteLine($"  --monitor {d.Index}: {d.Width}x{d.Height}{(d.Primary ? " (primary)" : "")}");
    Console.WriteLine("Windows:");
    for (int i = 0; i < windows.Count; i++) Console.WriteLine($"  --window {i}: {windows[i].Label}");
    return 0;
}

Display? display = null;
long? windowHandle = null;
PortalScreenCast? portal = null;
if (ScreenCapture.UsesPortal)
{
    // Wayland: the desktop asks what to share (a monitor, or a window with --portal-window) and hands the picture over.
    Console.WriteLine("Asking the desktop what to share...");
    portal = await PortalScreenCast.StartAsync(window: args.Contains("--portal-window"), showCursor: true);
    Console.WriteLine($"Sharing {(portal.IsWindow ? "a window" : "a monitor")} from the desktop: {portal.Width}x{portal.Height}");
}
else if (Opt("--window") is { } wi)
{
    var w = int.TryParse(wi, out int windowIndex) ? windows.ElementAtOrDefault(windowIndex) : null;
    if (w == null) { Console.Error.WriteLine("No such window; use --list."); return 2; }
    Console.WriteLine("Sharing the window: " + w.Label);
    windowHandle = w.Handle;
}
else
{
    display = displays.FirstOrDefault(d => d.Index.ToString() == (Opt("--monitor") ?? "0"));
    if (display == null) { Console.Error.WriteLine("No such monitor; use --list."); return 2; }
    Console.WriteLine($"Sharing monitor {display.Index}: {display.Width}x{display.Height}");
}
var codec = (Opt("--codec") ?? "auto").ToLowerInvariant() switch { "vp8" => ScreenCodec.Vp8, "h264" => ScreenCodec.H264, "av1" => ScreenCodec.Av1, _ => ScreenCodec.Auto };
var screen = new ScreenOptions { Fps = Int("--fps", 10), Kbps = Int("--kbps", 800), MaxWidth = Int("--width", 1280), MaxHeight = Int("--height", 720), Display = display, WindowHandle = windowHandle, Portal = portal, Codec = codec, Encoder = HardwareEncoders.TryParse(Opt("--encoder"), out var card) ? card : null };
string? savePath = Opt("--save");

ChatNode Make(string name) => new(new ChatNodeOptions
{
    DisplayName = name,
    EnableLocalDiscovery = false,
    DiscoveryFactory = mux => new LocalFileDiscovery(mux.LocalPort),
    DiscoveryInterval = TimeSpan.FromSeconds(2),
    Traversal = new TraversalOptions { Enabled = false, PortMapping = false },
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

using var sharer = ScreenSharer.Start(sharerNode, sharerRoom, "Demo screen", screen);
sharer.Ended += ex => { if (ex != null) Console.Error.WriteLine("Sharing stopped: " + ex.Message); };

wait.Restart();
MediaGroupInfo? group = null;
while ((group = viewerRoom.MediaGroups.FirstOrDefault()) == null && wait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(200);
if (group == null) { Console.Error.WriteLine("The viewer never saw the share."); return 1; }

using var view = viewerNode.WatchMedia(viewerRoom, group.Id);
using var viewer = new ScreenViewer(view, gpuDecode: args.Contains("--gpu-decode"));
Picture? last = null;
long firstPictureMs = -1;
var clock = Stopwatch.StartNew();
viewer.PictureReceived += p => { if (firstPictureMs < 0) firstPictureMs = clock.ElapsedMilliseconds; last = p; };
viewer.Failed += ex => Console.Error.WriteLine("Viewing stopped: " + ex.Message);

TimeSpan Cpu() => Process.GetCurrentProcess().TotalProcessorTime;
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
Console.WriteLine($"Viewer decoding: {viewer.DecoderKind}");
Console.WriteLine($"Codec: {ScreenCodecs.Describe(sharer.Codec, sharer.Encoder)}{(sharer.FallbackReason is { } why ? $" (not used: {why})" : "")}");
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
