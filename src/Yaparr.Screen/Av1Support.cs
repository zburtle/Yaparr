using System.Diagnostics;

namespace Yaparr.Screen;

/// <summary>What this computer can do with AV1: which graphics cards encode it (so it can be offered when sharing) and whether FFmpeg here can decode it (so a preview, or a watched share, shows).</summary>
public sealed record Av1Report(IReadOnlyList<HardwareEncoder> Encoders, IReadOnlyDictionary<HardwareEncoder, string> Refused, bool CanDecode, string? DecoderName)
{
    public bool CanEncode => Encoders.Count > 0;

    /// <summary>One line for the diagnostics and the log.</summary>
    public string Describe() =>
        $"encoding: {(CanEncode ? string.Join(", ", Encoders.Select(HardwareEncoders.Maker)) : "no graphics card here does it")}"
        + (Refused.Count > 0 ? $" (not usable: {string.Join("; ", Refused.Select(r => $"{HardwareEncoders.Maker(r.Key)}: {r.Value}"))})" : "")
        + $"; decoding: {(CanDecode ? DecoderName : "FFmpeg here has no AV1 decoder")}";
}

/// <summary>
/// The AV1 check, run once in the background after the app starts (it takes a second or two when a card refuses, and is not repeated). Asking FFmpeg for one
/// tiny AV1 picture from each card encoder is the only reliable test (it fails for a card that lacks the hardware, an old driver, or an encoder that FFmpeg
/// here was built without), and reading what comes out tests the one thing a viewer depends on: that the first unit is a key frame that carries a sequence header.
/// </summary>
public static class Av1Support
{
    private static volatile Av1Report? _latest;

    /// <summary>The result of the last check, or null while it has not finished or FFmpeg is not on this computer yet (then AV1 is not ruled out).</summary>
    public static Av1Report? Latest => _latest;

    /// <summary>Raised on another thread when a check finishes.</summary>
    public static event Action<Av1Report>? Checked;

    private static int _started;

    /// <summary>Runs the check once in the background. Does nothing if it was already started or FFmpeg is not there.</summary>
    public static void StartCheck(string? ffmpegPath = null, Action<string>? log = null)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        if ((ffmpegPath ?? FfmpegLocator.Find()) is not { } ffmpeg) { Interlocked.Exchange(ref _started, 0); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var report = await CheckAsync(ffmpeg).ConfigureAwait(false);
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

    public static async Task<Av1Report> CheckAsync(string ffmpeg, CancellationToken ct = default)
    {
        var working = new List<HardwareEncoder>();
        var refused = new Dictionary<HardwareEncoder, string>();
        var encoderList = await FfmpegLocator.RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, ct).ConfigureAwait(false);
        foreach (var card in HardwareEncoders.InOrder)
        {
            string name = HardwareEncoders.Name(ScreenCodec.Av1, card);
            if (!encoderList.Contains(" " + name + " ", StringComparison.Ordinal)) { refused[card] = "this FFmpeg was built without " + name; continue; }
            var why = await TryEncodeAsync(ffmpeg, card, ct).ConfigureAwait(false);
            if (why == null) working.Add(card); else refused[card] = why;
        }
        var decoderList = await FfmpegLocator.RunAsync(ffmpeg, new[] { "-hide_banner", "-decoders" }, ct).ConfigureAwait(false);
        // FFmpeg's own AV1 decoder only works through a graphics card's decoding hardware; these two decode in software.
        string? decoder = new[] { "libdav1d", "libaom-av1" }.FirstOrDefault(d => decoderList.Contains(" " + d + " ", StringComparison.Ordinal));
        return new Av1Report(working, refused, decoder != null, decoder);
    }

    /// <summary>Null if the card made an AV1 key frame that a viewer could start from; otherwise why not.</summary>
    private static async Task<string?> TryEncodeAsync(string ffmpeg, HardwareEncoder card, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in FfmpegArgs.ProbeEncoder(ScreenCodec.Av1, card)) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await Ivf.ReadHeaderAsync(process.StandardOutput.BaseStream, limit.Token).ConfigureAwait(false);
            var first = await Ivf.ReadFrameAsync(process.StandardOutput.BaseStream, limit.Token).ConfigureAwait(false);
            if (first is not { } unit) return "it made no picture";
            if (!Av1.IsKeyFrame(unit.Data)) return "its first picture has no sequence header, which a viewer cannot start from";
            return Av1.TryGetSize(unit.Data, out _, out _) ? null : "its sequence header could not be read";
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException)
        {
            string said = "";
            try { said = (await errors.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)).Trim(); } catch (Exception inner) when (inner is TimeoutException or OperationCanceledException) { }
            return said.Length > 0 ? said.Split('\n')[^1].Trim() : "it did not start";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "it took too long"; }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
}
