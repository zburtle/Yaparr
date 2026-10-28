using System.Text.Json;

namespace Yaparr.Chat;

/// <summary>
/// Message times are the sender's own clock, and clocks differ. For each author this learns how far their clock is
/// from ours by comparing a freshly pushed message's time with when it arrived, so every message of theirs (including
/// history and catch-up) can be shown on our clock and fall in the right place among everyone else's.
/// </summary>
internal sealed class ClockOffsets
{
    // Beyond this the difference is more likely a replayed old message than a wrong clock.
    private static readonly long MaxSkewMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    private const int MaxAuthors = 2000;

    private readonly string? _path;
    private readonly object _gate = new();
    private Dictionary<string, long> _offsets = new();
    private bool _dirty;

    public ClockOffsets(string? path)
    {
        _path = path;
        if (path == null || !File.Exists(path)) return;
        try
        {
            _offsets = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Notes a message that was pushed to us as it was written: it arrived at <paramref name="arrivedMs"/> by our clock.</summary>
    public void Observe(string author, long sentMs, long arrivedMs)
    {
        long sample = arrivedMs - sentMs;
        if (Math.Abs(sample) > MaxSkewMs) return;
        lock (_gate)
        {
            if (!_offsets.TryGetValue(author, out var current)) { if (_offsets.Count >= MaxAuthors) return; current = sample; }
            // Delivery time only ever adds to the difference, so the smallest sample is the best one; larger ones pull the
            // estimate up slowly, which lets it follow a clock that drifts.
            long next = sample < current ? sample : current + (sample - current) / 16;
            if (_offsets.TryGetValue(author, out _) && Math.Abs(next - current) < 250) return;
            _offsets[author] = next;
            _dirty = true;
        }
        Save();
    }

    /// <summary>The time of a message on our clock.</summary>
    public long Correct(string author, long sentMs)
    {
        lock (_gate) return _offsets.TryGetValue(author, out var offset) ? sentMs + offset : sentMs;
    }

    private void Save()
    {
        if (_path == null) return;
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(_offsets);
        }
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
