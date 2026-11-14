namespace Yaparr.Screen;

/// <summary>What this computer can do with AV1: which graphics cards encode it (so it can be offered when sharing) and whether it can be decoded here (so a preview, or a watched share, shows).</summary>
public sealed record Av1Report(IReadOnlyList<HardwareEncoder> Encoders, IReadOnlyDictionary<HardwareEncoder, string> Refused, bool CanDecode, string? DecoderName)
{
    public bool CanEncode => Encoders.Count > 0;

    /// <summary>One line for the diagnostics and the log.</summary>
    public string Describe() =>
        $"encoding: {(CanEncode ? string.Join(", ", Encoders.Select(HardwareEncoders.Maker)) : "no graphics card here does it")}"
        + (Refused.Count > 0 ? $" (not usable: {string.Join("; ", Refused.Select(r => $"{HardwareEncoders.Maker(r.Key)}: {r.Value}"))})" : "")
        + $"; decoding: {(CanDecode ? DecoderName : "FFmpeg's libraries here have no AV1 decoder")}";
}

/// <summary>
/// The AV1 check, run once in the background after the app starts (it takes a second or two when a card refuses, and is not repeated). Asking each card's encoder
/// for a few tiny AV1 pictures, inside this program with FFmpeg's libraries (<see cref="InProcessEncoder.Probe"/>), is the only reliable test (it fails for a card that
/// lacks the hardware, an old driver, or libraries built without the encoder), and reading what comes out tests the one thing a viewer depends on: that the first
/// unit is a key frame that carries a sequence header.
/// </summary>
public static class Av1Support
{
    private static volatile Av1Report? _latest;

    /// <summary>The result of the last check, or null while it has not finished or FFmpeg's libraries are not on this computer yet (then AV1 is not ruled out).</summary>
    public static Av1Report? Latest => _latest;

    /// <summary>Raised on another thread when a check finishes.</summary>
    public static event Action<Av1Report>? Checked;

    private static int _started;

    /// <summary>Runs the check once in the background. Does nothing if it was already started or the libraries are not there.</summary>
    public static void StartCheck(Action<string>? log = null)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        if (!FfmpegLibraries.IsAvailable) { Interlocked.Exchange(ref _started, 0); return; }
        _ = Task.Run(() =>
        {
            try
            {
                var report = Check();
                _latest = report;
                log?.Invoke("AV1 check: " + report.Describe());
                Checked?.Invoke(report);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log?.Invoke("AV1 check failed: " + ex.Message);
                Interlocked.Exchange(ref _started, 0);
            }
        });
    }

    /// <summary>The check itself, on the calling thread.</summary>
    public static Av1Report Check()
    {
        var working = new List<HardwareEncoder>();
        var refused = new Dictionary<HardwareEncoder, string>();
        foreach (var card in HardwareEncoders.InOrder)
        {
            if (InProcessEncoder.Probe(ScreenCodec.Av1, card, out var why)) working.Add(card);
            else refused[card] = why;
        }
        bool decodes = InProcessDecoder.IsAvailable(ScreenCodec.Av1);
        return new Av1Report(working, refused, decodes, decodes ? InProcessDecoder.DecoderName(ScreenCodec.Av1) : null);
    }
}
