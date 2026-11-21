using Yaparr.Chat.Files;

namespace Yaparr.AppCore;

/// <summary>What the apps say about a shared file, so the computer and the phone say the same things (see <c>docs/file-sharing.md</c>).</summary>
public static class FileTexts
{
    public static string Size(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1024; unit++; }
        if (unit == 0) return $"{(long)value} B";
        return value >= 100 ? $"{value:0} {units[unit]}" : value >= 10 ? $"{value:0.#} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    public static string Speed(long bytesPerSecond) => Size(bytesPerSecond) + "/s";

    /// <summary>How long is left, as people say it ("about 3 min"), or "" when it cannot be told.</summary>
    public static string Remaining(long bytesLeft, long bytesPerSecond)
    {
        if (bytesPerSecond < 1024 || bytesLeft <= 0) return "";
        double seconds = bytesLeft / (double)bytesPerSecond;
        return seconds switch
        {
            < 10 => "a few seconds left",
            < 90 => $"about {Math.Round(seconds / 10) * 10:0} s left",
            < 5400 => $"about {Math.Round(seconds / 60):0} min left",
            < 172800 => $"about {Math.Round(seconds / 3600):0.#} h left".Replace(".0", ""),
            _ => $"about {Math.Round(seconds / 86400):0} days left",
        };
    }

    public static int Percent(FileProgress p) => p.Size <= 0 ? 0 : (int)Math.Min(100, p.BytesDone * 100 / p.Size);

    /// <summary>The line under a file while it is being fetched, waiting, paused or done.</summary>
    public static string Status(FileProgress p)
    {
        var size = Size(p.Size);
        switch (p.State)
        {
            case FileState.Completed: return $"{size} · saved";
            case FileState.Failed: return "Could not be saved. The disk may be full or the folder gone.";
            case FileState.Canceled: return $"{size} · canceled";
            case FileState.Paused: return $"Paused at {Percent(p)}% · {Size(p.BytesDone)} of {size}";
            case FileState.Waiting:
                return p.BytesDone > 0
                    ? $"Waiting for someone who has it · {Percent(p)}% · {Size(p.BytesDone)} of {size}"
                    : $"Waiting for someone who has it · {size}";
            default:
                var parts = new List<string> { $"{Percent(p)}% · {Size(p.BytesDone)} of {size}" };
                if (p.BytesPerSecond >= 1024) parts.Add(Speed(p.BytesPerSecond));
                if (Remaining(p.Size - p.BytesDone, p.BytesPerSecond) is { Length: > 0 } left) parts.Add(left);
                if (p.Holders > 1) parts.Add($"from {p.Holders} people");
                return string.Join(" · ", parts);
        }
    }
}
