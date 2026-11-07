using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>
/// What a phone's screen share can be sent at: the longer side of the picture, pictures a second and the bitrate. The computer's share dialog has the
/// same kind of choice (720p, 1080p); a phone's screen is tall and narrow, so the size is for the longer side.
/// </summary>
internal sealed record ShareQuality(string Label, int MaxLongSide, int Fps, int Kbps)
{
    public static readonly IReadOnlyList<ShareQuality> All = new[]
    {
        new ShareQuality("Light: 540p, 15 pictures a second", 960, 15, 700),
        new ShareQuality("Standard: 720p, 30 pictures a second", 1280, 30, 1800),
        new ShareQuality("High: 1080p, 30 pictures a second", 1920, 30, 3500),
    };

    /// <summary>The one the dialog starts at.</summary>
    public static ShareQuality Standard => All[1];

    /// <summary>
    /// The best quality this connection can send to three people at once (<see cref="ChatNode.DirectViewersAt"/>), or the lightest when none can, or
    /// when the upload has not been measured yet: the standard one then, which suits most connections.
    /// </summary>
    public static ShareQuality BestFor(long? measuredUpload)
    {
        if (measuredUpload is not > 0) return Standard;
        var best = All[0];
        foreach (var quality in All)
            if (ChatNode.DirectViewersAt(measuredUpload, quality.Kbps) >= 3) best = quality;
        return best;
    }

    /// <summary>The line under the choice: how many people this connection can send the picture to directly, the rest watching through them.</summary>
    public string Hint(long? measuredUpload)
    {
        if (measuredUpload is not > 0) return "";
        int viewers = ChatNode.DirectViewersAt(measuredUpload, Kbps);
        string speed = $"{measuredUpload.Value * 8 / 1_000_000.0:0} Mbit/s";
        return viewers >= 3
            ? $"Your connection sends about {speed}, enough for this directly to {(viewers >= 6 ? "six" : viewers.ToString())} people at once; anyone else watches through them."
            : $"Your connection sends about {speed}, which suits sending this to {viewers} {(viewers == 1 ? "person" : "people")} at once, and the rest watch through them. A lighter choice keeps the picture and the voice chat steadier for everyone.";
    }
}
