using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yaparr.Screen;

/// <summary>
/// How the picture of a Wayland share reaches FFmpeg. The desktop's portal gives a PipeWire stream (see
/// <see cref="PortalScreenCast"/>), which FFmpeg cannot read, so GStreamer's <c>gst-launch-1.0</c> reads it, scales it to the
/// size to send, steadies the frame rate and writes raw pictures to a pipe, and FFmpeg encodes those like any other capture.
/// </summary>
public static class PortalPipeline
{
    /// <summary>The size of the pictures that are sent: the portal's size scaled down into the box in the options, in even numbers, never up.</summary>
    public static (int Width, int Height) FrameSize(PortalStream stream, ScreenOptions o)
    {
        if (stream.Width <= 0 || stream.Height <= 0) return (Even(o.MaxWidth), Even(o.MaxHeight));
        double factor = Math.Min(1.0, Math.Min((double)o.MaxWidth / stream.Width, (double)o.MaxHeight / stream.Height));
        return (Even((int)(stream.Width * factor)), Even((int)(stream.Height * factor)));
    }

    private static int Even(int v) => Math.Max(2, v / 2 * 2);

    /// <summary>
    /// The command line for <c>gst-launch-1.0</c>: the stream <paramref name="fd"/> names (the portal's PipeWire connection, which the
    /// child program inherits), converted to planar YUV at the frame size and rate, on standard output. A screen that does not change
    /// sends no pictures, so the last one is repeated to keep the rate steady (a still screen must still give a picture to a viewer
    /// that joins). <paramref name="nv12"/>: NV12 pictures, which the encoder in this program takes as they are; otherwise I420, which FFmpeg was given.
    /// </summary>
    public static List<string> GstArguments(PortalStream stream, int fd, ScreenOptions o, bool nv12 = false)
    {
        var (width, height) = FrameSize(stream, o);
        int keepAlive = Math.Max(20, 1000 / Math.Max(1, o.Fps));
        string number(int v) => v.ToString(CultureInfo.InvariantCulture);
        return new List<string>
        {
            "-q",
            "pipewiresrc", "fd=" + number(fd), "path=" + stream.NodeId.ToString(CultureInfo.InvariantCulture), "keepalive-time=" + number(keepAlive), "do-timestamp=true",
            // videorate starts its output at time zero, so when the first picture of the stream is stamped much later (some desktops stamp with their own
            // clock) it makes up every picture in between at once: a flood of copies of the first picture, a minute or two of video in a few seconds, at
            // many times the stream's bitrate. skip-to-first starts at the first picture, and max-duplication-time stops it filling a long gap.
            "!", "videoconvert", "!", "videoscale", "!", "videorate", "skip-to-first=true", "max-duplication-time=500000000", "!",
            $"video/x-raw,format={(nv12 ? "NV12" : "I420")},width={number(width)},height={number(height)},framerate={number(o.Fps)}/1",
            "!", "fdsink", "fd=1", "sync=false",
        };
    }

    /// <summary>Null when GStreamer and its PipeWire plugin are installed; otherwise what to install.</summary>
    public static string? Missing()
    {
        if (!OperatingSystem.IsLinux()) return "Sharing through the desktop's portal works on Linux only.";
        if (Find("gst-launch-1.0") == null || Find("gst-inspect-1.0") is not { } inspect)
            return "Sharing the screen on Wayland needs GStreamer: install gstreamer1.0-tools and gstreamer1.0-pipewire (or your distribution's equivalent).";
        try
        {
            var psi = new ProcessStartInfo(inspect, "pipewiresrc") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process == null) return "GStreamer did not start.";
            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000)) { try { process.Kill(); } catch (InvalidOperationException) { } return "GStreamer did not answer."; }
            if (process.ExitCode != 0) return "Sharing the screen on Wayland needs GStreamer's PipeWire plugin: install gstreamer1.0-pipewire (or your distribution's equivalent).";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return "GStreamer could not be run: " + ex.Message; }
        return null;
    }

    /// <summary>The first program of this name on the PATH, or null.</summary>
    public static string? Find(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => { try { return Path.Combine(dir, program); } catch (ArgumentException) { return ""; } })
            .FirstOrDefault(path => path.Length > 0 && File.Exists(path));

    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int command, int argument);
    private const int F_GETFD = 1, F_SETFD = 2, FD_CLOEXEC = 1;

    /// <summary>
    /// Lets a program started from now on inherit the descriptor: .NET opens its own with close-on-exec set, so without this the
    /// child (gst-launch-1.0) would not have the portal's PipeWire connection. Returns the descriptor's number, which is the same
    /// number in the child.
    /// </summary>
    public static int AllowInherit(SafeFileHandle handle)
    {
        int fd = (int)handle.DangerousGetHandle();
        int flags = fcntl(fd, F_GETFD, 0);
        if (flags < 0 || fcntl(fd, F_SETFD, flags & ~FD_CLOEXEC) < 0) throw new IOException("The connection to the desktop's screen share could not be handed to GStreamer (error " + Marshal.GetLastWin32Error() + ").");
        return fd;
    }
}
