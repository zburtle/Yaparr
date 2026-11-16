namespace Yaparr.Screen;

/// <summary>What this computer can do with H.265: which graphics cards encode it (so it can be offered when sharing) and whether it can be decoded here (so a watched share shows).</summary>
public sealed record H265Report(IReadOnlyList<HardwareEncoder> Encoders, IReadOnlyDictionary<HardwareEncoder, string> Refused, bool CanDecode, string? DecoderName)
{
    public bool CanEncode => Encoders.Count > 0;

    /// <summary>One line for the diagnostics and the log.</summary>
    public string Describe() =>
        $"encoding: {(CanEncode ? string.Join(", ", Encoders.Select(HardwareEncoders.Maker)) : "no graphics card here does it")}"
        + (Refused.Count > 0 ? $" (not usable: {string.Join("; ", Refused.Select(r => $"{HardwareEncoders.Maker(r.Key)}: {r.Value}"))})" : "")
        + $"; decoding: {(CanDecode ? DecoderName : "FFmpeg's libraries here have no H.265 decoder")}";
}

/// <summary>
/// The H.265 check, run once in the background after the app starts, as <see cref="Av1Support"/> does for AV1: each graphics card's encoder is asked for a few tiny
/// pictures inside this program (<see cref="InProcessEncoder.Probe"/>), which fails for a card that lacks the hardware, an old driver, or libraries built without the encoder.
/// Only graphics cards encode H.265 here (a processor does not make it live). Where <see cref="ScreenFeatures.H265"/> is off, nothing is checked and nothing works.
/// </summary>
public static class H265Support
{
    private static volatile H265Report? _latest;

    /// <summary>The result of the last check, or null while it has not finished or FFmpeg's libraries are not on this computer yet (then H.265 is not ruled out).</summary>
    public static H265Report? Latest => _latest;

    /// <summary>Raised on another thread when a check finishes.</summary>
    public static event Action<H265Report>? Checked;

    private static int _started;

    /// <summary>Runs the check once in the background. Does nothing if it was already started, H.265 is switched off, or the libraries are not there.</summary>
    public static void StartCheck(Action<string>? log = null)
    {
        if (!ScreenFeatures.H265) return;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        if (!FfmpegLibraries.IsAvailable) { Interlocked.Exchange(ref _started, 0); return; }
        _ = Task.Run(() =>
        {
            try
            {
                var report = Check();
                _latest = report;
                log?.Invoke("H.265 check: " + report.Describe());
                Checked?.Invoke(report);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log?.Invoke("H.265 check failed: " + ex.Message);
                Interlocked.Exchange(ref _started, 0);
            }
        });
    }

    /// <summary>The check itself, on the calling thread.</summary>
    public static H265Report Check()
    {
        var working = new List<HardwareEncoder>();
        var refused = new Dictionary<HardwareEncoder, string>();
        if (!ScreenFeatures.H265) return new H265Report(working, refused, false, null);
        // Only the encoders this computer can have hardware for (see GraphicsAdapters): the others would only each take their half second to fail.
        foreach (var card in GraphicsAdapters.Installed(GraphicsAdapters.Vendors))
        {
            if (InProcessEncoder.Probe(ScreenCodec.H265, card, out var why)) working.Add(card);
            else refused[card] = why;
        }
        bool decodes = InProcessDecoder.IsAvailable(ScreenCodec.H265);
        return new H265Report(working, refused, decodes, decodes ? InProcessDecoder.DecoderName(ScreenCodec.H265) : null);
    }
}
