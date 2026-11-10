using Yaparr.Identity;

namespace Yaparr.Desktop;

/// <summary>
/// What a screen being watched has done since watching began (every count only goes up), so the call quality log can say how the last minute
/// went and the sharing diagnostics how the whole watch went. <paramref name="StartedAt"/> is <see cref="Environment.TickCount64"/> when it began.
/// </summary>
public sealed record WatchFigures(string Name, long StartedAt, long Shown, long FramesIn, long BytesIn, long Lost, long Recovered, long Late, long Dropped,
    int DecodeQueue, bool Attached, PeerId? Parent)
{
    /// <summary>A line about the whole watch up to <paramref name="now"/>; <paramref name="path"/> says how the connection to whoever passes it on runs (or null).</summary>
    public string Describe(long now, string? path = null)
    {
        double seconds = Math.Max(1, (now - StartedAt) / 1000.0);
        return $"{Name}: watched {seconds:0} s; {Shown} pictures drawn ({Shown / seconds:0.0}/s on average), {FramesIn} frames arrived ({FramesIn / seconds:0.0}/s, "
             + $"{BytesIn * 8 / 1000.0 / seconds:0} kbit/s on average), {Lost} frames lost, {Recovered} packets recovered, {Late} too late to show, "
             + $"{Dropped} dropped by the decoder, decoder queue {DecodeQueue}; "
             + (Attached ? $"attached {(Parent is { } p ? "under " + p.Short : "to the sharer")}{(string.IsNullOrEmpty(path) ? "" : " (" + path + ")")}" : "not attached to the sharer");
    }
}
