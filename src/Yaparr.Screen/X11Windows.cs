using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Yaparr.Screen;

/// <summary>
/// Lists the windows of an X11 session with the standard tools (xprop and xwininfo, from the x11-utils package) so that one of
/// them can be shared. The parts that read their output are public and take plain text, so they can be checked on any system.
/// </summary>
public static class X11Windows
{
    private static readonly Regex Hex = new(@"0x[0-9a-fA-F]+", RegexOptions.CultureInvariant);
    private static readonly Regex Quoted = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);

    /// <summary>The window ids in the output of <c>xprop -root _NET_CLIENT_LIST</c>, as numbers.</summary>
    public static IReadOnlyList<long> ParseClientList(string text)
    {
        int at = text.IndexOf('#');
        var ids = new List<long>();
        if (at < 0) return ids;
        foreach (Match m in Hex.Matches(text[at..]))
            if (long.TryParse(m.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long id)) ids.Add(id);
        return ids;
    }

    /// <summary>What <c>xprop -id ID _NET_WM_NAME WM_NAME _NET_WM_PID _NET_WM_WINDOW_TYPE _NET_WM_STATE</c> says about a window.</summary>
    public sealed record Properties(string Title, int Pid, bool Shareable, IReadOnlyList<string> Classes);

    public static Properties ParseProperties(string text)
    {
        string title = "";
        int pid = 0;
        string type = "", state = "";
        var classes = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..].Trim();
            if (key.StartsWith("_NET_WM_NAME", StringComparison.Ordinal) || (title.Length == 0 && key.StartsWith("WM_NAME", StringComparison.Ordinal)))
            {
                if (Quoted.Match(value) is { Success: true } q) title = q.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();
            }
            else if (key.StartsWith("_NET_WM_PID", StringComparison.Ordinal)) int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
            else if (key.StartsWith("WM_CLASS", StringComparison.Ordinal)) classes.AddRange(Quoted.Matches(value).Select(m => m.Groups[1].Value).Where(c => c.Length > 0));
            else if (key.StartsWith("_NET_WM_WINDOW_TYPE", StringComparison.Ordinal)) type = value;
            else if (key.StartsWith("_NET_WM_STATE", StringComparison.Ordinal)) state = value;
        }
        // Panels, docks, menus and the desktop itself are windows too, but not ones to share; a minimised window stops updating.
        bool ordinary = type.Length == 0 || type.Contains("_NORMAL", StringComparison.Ordinal) || type.Contains("_DIALOG", StringComparison.Ordinal);
        bool hidden = state.Contains("_HIDDEN", StringComparison.Ordinal);
        return new Properties(title, pid, title.Length > 0 && ordinary && !hidden, classes);
    }

    /// <summary>The size, position and whether the window is on screen, from <c>xwininfo -id ID</c>; null if the output is not that.</summary>
    public sealed record Geometry(int X, int Y, int Width, int Height, bool Viewable);

    public static Geometry? ParseGeometry(string text)
    {
        int? Number(string label) =>
            Regex.Match(text, @"^\s*" + Regex.Escape(label) + @"\s*(-?\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant) is { Success: true } m
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        if (Number("Width:") is not { } width || Number("Height:") is not { } height) return null;
        return new Geometry(Number("Absolute upper-left X:") ?? 0, Number("Absolute upper-left Y:") ?? 0, width, height, text.Contains("Map State: IsViewable", StringComparison.Ordinal));
    }

    /// <summary>The windows you could share, the biggest first. Empty if xprop or xwininfo is not installed.</summary>
    public static IReadOnlyList<ShareWindow> List(int? exceptProcessId = null)
    {
        var found = new List<ShareWindow>();
        if (Run("xprop", "-root _NET_CLIENT_LIST") is not { } clients) return found;
        foreach (long id in ParseClientList(clients))
        {
            string hex = "0x" + id.ToString("x", CultureInfo.InvariantCulture);
            if (Run("xprop", $"-id {hex} _NET_WM_NAME WM_NAME _NET_WM_PID _NET_WM_WINDOW_TYPE _NET_WM_STATE") is not { } props) continue;
            var p = ParseProperties(props);
            if (!p.Shareable || (exceptProcessId != null && p.Pid == exceptProcessId)) continue;
            if (Run("xwininfo", $"-id {hex}") is not { } info || ParseGeometry(info) is not { Viewable: true } g || g.Width < 120 || g.Height < 80) continue;
            found.Add(new ShareWindow(id, p.Title, ProgramOf(p.Pid), g.Width, g.Height));
        }
        return found.OrderByDescending(w => (long)w.Width * w.Height).ToList();
    }

    /// <summary>Whether the window is still open.</summary>
    public static bool Exists(long handle) => Run("xwininfo", $"-id 0x{handle.ToString("x", CultureInfo.InvariantCulture)}") != null;

    /// <summary>The program that owns the window, or 0.</summary>
    public static int ProcessId(long handle) =>
        Run("xprop", $"-id 0x{handle.ToString("x", CultureInfo.InvariantCulture)} _NET_WM_PID") is { } text ? ParseProperties(text).Pid : 0;

    /// <summary>The names the window's program gave it (WM_CLASS: for a browser, "firefox" and "Firefox"), another way to find its sound.</summary>
    public static IReadOnlyList<string> Classes(long handle) =>
        Run("xprop", $"-id 0x{handle.ToString("x", CultureInfo.InvariantCulture)} WM_CLASS") is { } text ? ParseProperties(text).Classes : Array.Empty<string>();

    /// <summary>What is missing for listing monitors and windows (the x11-xserver-utils and x11-utils packages), as a sentence; null if all is there.</summary>
    public static string? MissingTools()
    {
        var missing = new List<string>();
        if (PortalPipeline.Find("xrandr") == null) missing.Add("xrandr (package x11-xserver-utils), without which only one screen is offered");
        if (PortalPipeline.Find("xprop") == null || PortalPipeline.Find("xwininfo") == null) missing.Add("xprop and xwininfo (package x11-utils), without which no windows are listed");
        return missing.Count == 0 ? null : "Install " + string.Join(" and ", missing) + ".";
    }

    private static string ProgramOf(int pid)
    {
        if (pid <= 0) return "";
        try { return File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/comm").Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // The output of the tool, or null if it is not installed, failed or took too long.
    private static string? Run(string program, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}
