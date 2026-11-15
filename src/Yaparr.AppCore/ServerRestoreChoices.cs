using System.Globalization;

namespace Yaparr.AppCore;

/// <summary>One line of the list a person chooses a backup from when restoring from a server.</summary>
public sealed record VersionChoice(int Version, string Label, bool Newest);

/// <summary>
/// What to show, and what to pre-select, when someone restoring from a backup server may choose which saved copy to bring back. The newest is the
/// usual one; an older one is for when the newest is wrong (made from a broken profile, or locked with a different recovery code).
/// </summary>
public static class ServerRestoreChoices
{
    /// <summary>The copies, newest first, each with a line to show: its number, when it was saved (in <paramref name="zone"/>, the local one by default) and its size.</summary>
    public static IReadOnlyList<VersionChoice> Describe(IReadOnlyList<ServerBackupVersion> versions, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var ordered = versions.OrderByDescending(v => v.Version).ToList();
        return ordered.Select((v, i) => new VersionChoice(v.Version,
            $"Version {v.Version}  ·  {TimeZoneInfo.ConvertTime(v.At, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}  ·  {Size(v.Size)}" + (i == 0 ? "  ·  newest" : ""), i == 0)).ToList();
    }

    /// <summary>
    /// The copy to have selected: the newest to begin with, and after a copy could not be opened the next older one (or, if that was the oldest, the
    /// newest again), so a person who just wants it to work only has to press the button again.
    /// </summary>
    public static int Preselect(IReadOnlyList<ServerBackupVersion> versions, int? failed = null)
    {
        var ordered = versions.OrderByDescending(v => v.Version).ToList();
        if (ordered.Count == 0) return 0;
        if (failed is not { } bad) return ordered[0].Version;
        return ordered.Where(v => v.Version < bad).Select(v => v.Version).DefaultIfEmpty(ordered[0].Version).First();
    }

    private static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
        : Math.Max(1, (bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KiB";
}
