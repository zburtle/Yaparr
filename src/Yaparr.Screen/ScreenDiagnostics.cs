using System.Diagnostics;
using System.Text;

namespace Yaparr.Screen;

/// <summary>
/// What a report about screen sharing needs to know about this computer: the kind of desktop, which way the picture is taken, and which
/// programs and encoders are there. Nothing personal: no names, no addresses, no paths beyond where the programs are.
/// </summary>
public static class ScreenDiagnostics
{
    public static async Task<string> BuildAsync(string? ffmpegPath = null, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.AppendLine(line);
        static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "(not set)";

        Line("Screen sharing");
        Line($"  Can share: {ScreenCapture.WhyNotSupported ?? "yes"}");
        Line($"  Way of capture here: {(ScreenCapture.UsesPortal ? "the desktop's portal (Wayland)" : OperatingSystem.IsWindows() ? "Windows Graphics Capture" : "X11 screen copy (x11grab)")}");
        if (OperatingSystem.IsLinux())
        {
            Line($"  Session: XDG_SESSION_TYPE={Env("XDG_SESSION_TYPE")}, WAYLAND_DISPLAY={Env("WAYLAND_DISPLAY")}, DISPLAY={Env("DISPLAY")}, XDG_CURRENT_DESKTOP={Env("XDG_CURRENT_DESKTOP")}");
            Line($"  Packaged as: {(File.Exists("/.flatpak-info") ? "Flatpak (sandbox)" : Environment.GetEnvironmentVariable("SNAP") != null ? "Snap" : "a normal install")}");
            if (ScreenCapture.UsesPortal)
                Line($"  GStreamer: {PortalPipeline.Missing() ?? "gst-launch-1.0 and its PipeWire source are there"}");
            else
                Line($"  A window chosen in the desktop's portal (X11): {(ScreenCapture.OffersPortalWindowOnX11 ? "offered in the share dialog" : "not offered (no portal on the session bus, or GStreamer's PipeWire source is missing)")}");
            Line($"  Programs: xrandr {Where("xrandr")}, xprop {Where("xprop")}, pactl {Where("pactl")}, parec {Where("parec")}, gst-launch-1.0 {Where("gst-launch-1.0")}");
            Line($"  Graphics devices: {Dri()}");
        }

        ffmpegPath ??= FfmpegLocator.Find();
        if (ffmpegPath == null) { Line("  FFmpeg: not found"); return text.ToString(); }
        Line($"  FFmpeg: {ffmpegPath}");
        var version = await RunAsync(ffmpegPath, "-hide_banner -version", ct).ConfigureAwait(false);
        Line($"  FFmpeg version: {version.Split('\n').FirstOrDefault()?.Trim() ?? "unknown"}");
        var encoders = await RunAsync(ffmpegPath, "-hide_banner -encoders", ct).ConfigureAwait(false);
        var wanted = encoders.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("vp8") || l.Contains("264") || l.Contains("av1") || l.Contains("vaapi") || l.Contains("nvenc")).ToList();
        Line($"  Encoders it has: {(wanted.Count == 0 ? "none of the ones Yaparr uses" : string.Join("; ", wanted.Select(l => l.Replace("  ", " "))))}");
        Line($"  AV1: {(Av1Support.Latest?.Describe() ?? "not checked yet")}");
        var inputs = await RunAsync(ffmpegPath, "-hide_banner -devices", ct).ConfigureAwait(false);
        Line($"  Capture inputs it has: {string.Join(", ", inputs.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("D") && (l.Contains("x11grab") || l.Contains("gdigrab") || l.Contains("lavfi"))).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? l))}");
        if (OperatingSystem.IsLinux() && Where("xrandr") != "missing")
        {
            var monitors = await RunAsync("xrandr", "--listmonitors", ct).ConfigureAwait(false);
            Line("  Monitors (xrandr): " + string.Join(" | ", monitors.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)));
        }
        return text.ToString();
    }

    private static string Where(string program) => PortalPipeline.Find(program) is { } path ? "at " + path : "missing";

    private static string Dri()
    {
        try
        {
            var devices = Directory.Exists("/dev/dri") ? Directory.GetFiles("/dev/dri").Select(Path.GetFileName).Order().ToList() : new List<string?>();
            return devices.Count == 0 ? "none (/dev/dri is empty or missing, so no VA-API)" : string.Join(", ", devices);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "could not be listed: " + ex.Message; }
    }

    // Runs a program and returns what it printed (standard output, then error output), or why it could not run. Gives up after 10 seconds.
    private static async Task<string> RunAsync(string program, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("did not start");
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { try { process.Kill(true); } catch (InvalidOperationException) { } return "(did not answer in 10 seconds)"; }
            return await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return "(could not run: " + ex.Message + ")"; }
    }
}
