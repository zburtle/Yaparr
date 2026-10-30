using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>--key value pairs; a --flag with no value counts as true.</summary>
internal sealed class AgentArgs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static AgentArgs Parse(string[] args)
    {
        var a = new AgentArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            a._values[key] = hasValue ? args[++i] : "true";
        }
        return a;
    }

    public string Str(string key, string fallback = "") => _values.TryGetValue(key, out var v) ? v : fallback;
    public int Int(string key, int fallback) => _values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public double Dbl(string key, double fallback) => _values.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public bool Flag(string key) => _values.TryGetValue(key, out var v) && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
    public bool Has(string key) => _values.ContainsKey(key);
}

/// <summary>One JSON object per line on standard output. The orchestrator reads these; people can too.</summary>
internal static class Out
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Since = Stopwatch.StartNew();

    public static string Node = "";

    /// <summary>Milliseconds since the agent started.</summary>
    public static long Ms => Since.ElapsedMilliseconds;

    public static void Write(string type, params (string Key, object? Value)[] fields)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            w.WriteNumber("up", Ms);
            w.WriteString("node", Node);
            w.WriteString("type", type);
            foreach (var (key, value) in fields) Field(w, key, value);
            w.WriteEndObject();
        }
        var line = Encoding.UTF8.GetString(stream.ToArray());
        lock (Gate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private static void Field(Utf8JsonWriter w, string key, object? value)
    {
        switch (value)
        {
            case null: w.WriteNull(key); break;
            case string s: w.WriteString(key, s); break;
            case bool b: w.WriteBoolean(key, b); break;
            case int i: w.WriteNumber(key, i); break;
            case long l: w.WriteNumber(key, l); break;
            case double d: w.WriteNumber(key, double.IsFinite(d) ? Math.Round(d, 3) : 0); break;
            case float f: w.WriteNumber(key, double.IsFinite(f) ? Math.Round(f, 3) : 0); break;
            case IDictionary<string, object?> dict:
                w.WriteStartObject(key);
                foreach (var kv in dict) Field(w, kv.Key, kv.Value);
                w.WriteEndObject();
                break;
            default: w.WriteString(key, value.ToString()); break;
        }
    }
}

/// <summary>What the agent learned about finding the others, reported in every summary.</summary>
internal static class Found
{
    /// <summary>Milliseconds since start when the first connection to another member opened, or -1.</summary>
    public static long FirstPeerAtMs = -1;
    public static int PeersNow;
    public static int PeersMost;

    public static void Connected()
    {
        if (Interlocked.CompareExchange(ref FirstPeerAtMs, Out.Ms, -1) == -1) { }
        int now = Interlocked.Increment(ref PeersNow);
        int most;
        while (now > (most = Volatile.Read(ref PeersMost))) Interlocked.CompareExchange(ref PeersMost, now, most);
    }

    public static void Disconnected() => Interlocked.Decrement(ref PeersNow);
}

internal static class Maths
{
    public static double Percentile(List<double> sortedOrNot, double p)
    {
        if (sortedOrNot.Count == 0) return 0;
        var sorted = sortedOrNot.OrderBy(x => x).ToList();
        int index = (int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}
