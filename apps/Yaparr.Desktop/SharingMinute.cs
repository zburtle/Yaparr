using Yaparr.Screen;

namespace Yaparr.Desktop;

/// <summary>
/// What a share did in the last minute, as the one line the log gets (see <see cref="CallQualityLog"/>). It is kept apart from the log so that
/// the words can be checked without a window or a clock: this line is what a test run is judged by, so every figure in it has to be there.
/// </summary>
public sealed record SharingMinute(
    double Fps, double WantedFps, double Kbps, string Encoder, bool InProcess, (int Width, int Height)? Picture,
    ShareFigures Interval, ShareMaxima Maxima, double FrameBudgetMs, long LongestGapMs, long Resent, long Dropped,
    int Watching, IReadOnlyList<string> DirectPaths, double AppPercent, double CapturePercent, long MemoryMb,
    (int Gen0, int Gen1, int Gen2) Collections, double PausedMs, string? Card, bool Bad, double PreviewPercent = -1, string? PreviewKind = null, bool CaptureInProcess = false, bool CaptureOnCard = false)
{
    public string Describe()
    {
        string encoding = Interval.DescribeEncoding(FrameBudgetMs, Maxima);
        return $"call quality: sharing{(Bad ? " (a problem)" : "")}: {Fps:0.0} of {WantedFps:0} fps, {Kbps:0} kbit/s, {Encoder}"
               + $"{(InProcess ? ", encoded in this program" : ", encoded by a separate FFmpeg")}{(Picture is { } size ? $", picture {size.Width}x{size.Height}" : "")}; "
               + $"{Interval.DescribeKeys(Maxima)}; {(encoding.Length > 0 ? encoding + "; " : "")}longest wait for a frame {LongestGapMs} ms; "
               + $"{Resent} packets sent again, {Dropped} dropped, {Watching} watching, {DirectPaths.Count} of them directly"
               + (DirectPaths.Count > 0 ? " (" + string.Join(", ", DirectPaths) + ")" : "") + "; "
               + $"processor: this program {AppPercent:0}% of a core, {(CaptureInProcess ? $"capture in this program, pictures {(CaptureOnCard ? "kept on the card" : "copied to memory")} (no capture FFmpeg)" : $"capture FFmpeg {CapturePercent:0}%")}{(PreviewPercent >= 0 ? $", preview decoder {PreviewPercent:0}%{(PreviewKind != null ? " (" + PreviewKind + ")" : "")}" : "")}, memory {MemoryMb} MB, "
               + $"garbage collections {Collections.Gen0}/{Collections.Gen1}/{Collections.Gen2} (paused {PausedMs:0} ms), capture restarted {Interval.Restarts}x"
               + (Card != null ? $"; graphics card: {Card}" : "");
    }
}
