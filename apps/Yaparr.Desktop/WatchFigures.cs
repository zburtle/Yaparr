using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

/// <summary>
/// What a screen being watched has done since watching began (every count only goes up), so the call quality log can say how the last minute
/// went and the sharing diagnostics how the whole watch went. <paramref name="StartedAt"/> is <see cref="Environment.TickCount64"/> when it began.
/// </summary>
public sealed record WatchFigures(string Name, long StartedAt, long Shown, long FramesIn, long BytesIn, long Lost, long Recovered, long Late, long Dropped,
    int DecodeQueue, bool Attached, PeerId? Parent, long Missed = 0, string? PictureKind = null,
    long PacketsSeen = 0, long GapsNoticed = 0, long BigGaps = 0, long ArrivedLate = 0, long RepairedAfterAsking = 0, long NacksSent = 0, long PacketsAsked = 0, long GaveUp = 0,
    ViewerQualitySample Judged = default, long Duplicates = 0)
{
    /// <summary>
    /// How this viewer judges its own picture, which is what it tells the sharer about (see <see cref="ViewerQualityMeter"/>): "in trouble, 22 pictures a second",
    /// "fine, 58 pictures a second", or that there is not enough to go by yet (the first five seconds, or no pictures coming). Put in the log beside the figures
    /// it is made from, so that one viewer's log shows what that viewer reported.
    /// </summary>
    public string DescribeJudgement() => Judged.Known
        ? $"judged {(Judged.Troubled ? "in trouble" : "fine")}, {Judged.Fps:0} pictures a second over the last ten seconds{(Judged.Lossy ? ", losing packets for good" : "")}, and told the sharer so"
        : "not judged yet, so nothing was told to the sharer";

    /// <summary>
    /// What became of the packets, as words: how many came, how many were missing when a later one arrived, and what then happened to those (they turned up by
    /// themselves a moment later, were repaired after being asked for, or never came), and how much was asked for.
    /// </summary>
    public string DescribePackets() =>
        $"{PacketsSeen} packets came, {GapsNoticed} were missing when a later one arrived ({ArrivedLate} turned up by themselves, {RepairedAfterAsking} after being asked for, {GaveUp} never), "
        + $"{NacksSent} resend requests for {PacketsAsked} packets, {BigGaps} gaps too big to ask about, {Duplicates} copies refused (a packet already taken, or one from too far back)";

    /// <summary>Frames thrown away while waiting for a key frame after a loss (everything missed, less the frames given up on).</summary>
    public long Skipped => Math.Max(0, Missed - Lost);

    /// <summary>A line about the whole watch up to <paramref name="now"/>; <paramref name="path"/> says how the connection to whoever passes it on runs (or null).</summary>
    public string Describe(long now, string? path = null)
    {
        double seconds = Math.Max(1, (now - StartedAt) / 1000.0);
        return $"{Name}: watched {seconds:0} s{(PictureKind != null ? ", " + PictureKind : "")}; {Shown} pictures drawn ({Shown / seconds:0.0}/s on average), {FramesIn} frames arrived ({FramesIn / seconds:0.0}/s, "
             + $"{BytesIn * 8 / 1000.0 / seconds:0} kbit/s on average), {Lost} frames lost, {Skipped} skipped waiting for a key frame, {Recovered} packets recovered, {Late} too late to show, "
             + $"{Dropped} dropped by the decoder, decoder queue {DecodeQueue}; {DescribePackets()}; {DescribeJudgement()}; "
             + (Attached ? $"attached {(Parent is { } p ? "under " + p.Short : "to the sharer")}{(string.IsNullOrEmpty(path) ? "" : " (" + path + ")")}" : "not attached to the sharer");
    }
}
