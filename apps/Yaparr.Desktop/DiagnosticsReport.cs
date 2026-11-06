using System.Runtime.InteropServices;
using System.Text;
using Yaparr.Chat;
using Yaparr.Diagnostics;

namespace Yaparr.Desktop;

/// <summary>
/// Everything a bug report needs in one block of text: the version, the system, how the network looks, how each person is reached
/// and the end of the log. Names of people and rooms are left out, ids and addresses are shortened and keys and passwords removed.
/// </summary>
public static class DiagnosticsReport
{
    private const int LogLines = 300;

    public static async Task<string> BuildAsync(ChatNode node, AppSettings settings, FileLog log, string dataDirectory, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.AppendLine(line);

        Line($"Yaparr {Updater.CurrentVersion}");
        Line($"When: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line($"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}");
        Line($"Running for: {FormatSpan(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime)}");
        Line();

        // The check looks at the network again, so it runs first and the lines above it are read afterwards.
        NetworkCheckReport? report = null;
        string? checkProblem = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            report = await node.CheckNetworkAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { checkProblem = "The check did not finish in 20 seconds."; }
        catch (Exception ex) when (ex is not OperationCanceledException) { checkProblem = "The check failed: " + ex.Message; }

        var traversal = TraversalOptions.Load(Path.Combine(dataDirectory, "network.json"));
        var status = node.NetworkStatus;
        Line("Network");
        Line($"  Local UDP port: {node.LocalPort}");
        Line($"  Reachable by others: {(status.Reachable ? "yes" : "no")}");
        Line($"  {Scrub(status.Describe())}");
        Line($"  VPN: {node.VpnState}");
        Line($"  Settings: network checks {OnOff(traversal.Enabled)}, port mapping {OnOff(traversal.PortMapping)}, local discovery {OnOff(traversal.LocalDiscovery)}, "
             + $"TURN relay {(traversal.Turn is { Server.Length: > 0 } turn ? (turn.Enabled ? "on" : "set up but off") : "not set up")}");
        var relay = node.RelayStats;
        Line($"  Upload speed: {(node.MeasuredUploadBytesPerSecond is { } up ? $"about {up * 8 / 1_000_000.0:0.0} Mbit/s (passing on {node.RelayForwardKilobytes} KB/s of streams)" : "not measured yet")}");
        var paths = node.GetConnectionPaths();
        Line($"  Reached now: {paths.Describe()}; made since start: {paths.MadeDirect} direct, {paths.MadeViaMember} through members, {paths.MadeViaServer} through the shared server; "
             + $"{paths.MovedOffServer} moved off the server, standby took over {paths.StandbyTookOver}x");
        Line($"  Helps others connect: {(node.IsRelayCapable ? "yes" : "no")}; forwarded {relay.ForwardedFrames} frames ({relay.ForwardedBytes / 1024} KB), dropped {relay.DroppedFrames}, {relay.ActivePairs} active");
        Line();

        Line("Connection check");
        if (report != null)
        {
            foreach (var item in report.Items) Line($"  [{item.Level}] {item.Title}: {Scrub(item.Detail)}");
            Line($"  {report.Summary}");
        }
        else Line("  " + checkProblem);
        Line();

        var peers = node.ConnectedPeers.OrderBy(p => p.Id.Value).ToList();
        Line($"People connected: {peers.Count}");
        foreach (var peer in peers) Line($"  {peer.Id.Short}  {node.GetLink(peer.Id)}");
        Line($"Rooms: {node.Rooms.Count}");
        Line();

        Line("Voice");
        Line($"  Microphone: {settings.VoiceInput ?? "default"}; speakers: {settings.VoiceOutput ?? "default"}");
        Line($"  Echo cancellation {OnOff(settings.VoiceEchoCancellation)}, noise suppression {settings.VoiceNoiseSuppression}, automatic volume {OnOff(settings.VoiceAutomaticVolume)}, leveling people {OnOff(settings.VoiceLevelSpeakers)}, {(settings.VoiceAlwaysOn ? "always sending" : "sending when talking")}");
        Line();

        var tail = log.Tail(LogLines);
        Line($"Log (last {tail.Count} lines, addresses shortened)");
        foreach (var line in tail) Line("  " + Scrub(line));

        return HidePerson(text.ToString());
    }

    private static string Scrub(string line) => LogScrubber.Scrub(line);

    // The Windows user name is in every path the log mentions.
    private static string HidePerson(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length >= 3) text = text.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{Math.Max(0, (int)span.TotalMinutes)} min";
}
