using System.Diagnostics;
using System.Globalization;

namespace Yaparr.Screen;

/// <summary>
/// What NVIDIA's own tool says about the graphics card right now (how busy the card and its video encoder are, memory, temperature, clock,
/// power, and the encoder sessions it is running), for the log: the one way to tell an encoder that is slow because the card is busy
/// from one that is slow for another reason. Where the tool is not installed there is nothing to say.
/// </summary>
public static class NvidiaSmi
{
    private const string Fields = "utilization.gpu,utilization.encoder,utilization.decoder,memory.used,temperature.gpu,clocks.sm,power.draw,encoder.stats.sessionCount,encoder.stats.averageFps,encoder.stats.averageLatency";
    private static volatile bool _missing;

    /// <summary>One line about the first graphics card, or null when the tool is missing, fails or takes more than five seconds.</summary>
    public static async Task<string?> SampleAsync(CancellationToken ct = default)
    {
        if (_missing) return null;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", $"--query-gpu={Fields} --format=csv,noheader,nounits") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process == null) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? Describe(output) : null;
        }
        catch (System.ComponentModel.Win32Exception) { _missing = true; return null; }   // not installed
        catch (OperationCanceledException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private const string InfoFields = "name,driver_version,memory.total,pcie.link.gen.current,pcie.link.width.current";

    /// <summary>Which card and driver this is (for the log at the start of a share), or null when the tool is missing or fails.</summary>
    public static async Task<string?> InfoAsync(CancellationToken ct = default)
    {
        if (_missing) return null;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", $"--query-gpu={InfoFields} --format=csv,noheader,nounits") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process == null) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? DescribeInfo(output) : null;
        }
        catch (System.ComponentModel.Win32Exception) { _missing = true; return null; }
        catch (OperationCanceledException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Turns the tool's line about the card (in the order of <see cref="InfoFields"/>) into words.</summary>
    public static string? DescribeInfo(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var v = line?.Split(',', StringSplitOptions.TrimEntries);
        if (v == null || v.Length < 5 || v[0].Length == 0) return null;
        static bool Known(string s) => s.Length > 0 && !s.StartsWith('[');
        return $"{v[0]}{(Known(v[1]) ? ", driver " + v[1] : "")}{(Known(v[2]) ? ", " + v[2] + " MB of video memory" : "")}{(Known(v[3]) && Known(v[4]) ? $", PCIe generation {v[3]} x{v[4]}" : "")}";
    }

    /// <summary>Turns the tool's one line of figures (in the order of <see cref="Fields"/>) into words. Unknown figures are left out.</summary>
    public static string? Describe(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line == null) return null;
        var v = line.Split(',', StringSplitOptions.TrimEntries);
        if (v.Length < 10) return null;
        static string? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? s : null;
        var parts = new List<string>();
        void Add(string? value, string format) { if (value != null) parts.Add(string.Format(CultureInfo.InvariantCulture, format, value)); }
        Add(Num(v[0]), "card {0}% busy");
        Add(Num(v[1]), "video encoder {0}% busy");
        Add(Num(v[2]), "video decoder {0}% busy");
        Add(Num(v[3]), "{0} MB of video memory used");
        Add(Num(v[4]), "{0} °C");
        Add(Num(v[5]), "{0} MHz");
        Add(Num(v[6]), "{0} W");
        if (Num(v[7]) is { } sessions)
        {
            parts.Add($"{sessions} encoder sessions");
            if (Num(v[8]) is { } fps && Num(v[9]) is { } latency) parts.Add($"averaging {fps} fps and {latency} µs a picture");
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
