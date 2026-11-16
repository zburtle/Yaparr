using System.Globalization;

namespace Yaparr.AppCore;

/// <summary>
/// The clock that orders writes of synced records (see the Yaparr.Cloud README, "Synced state"): a hybrid logical clock, made so that its text sorts in write order and
/// that a device which hears of a newer write makes its next one newer still, even when its own clock is behind. It reads
/// <c>wall time in milliseconds (12 hex digits) | counter (4 hex digits) | device id</c>: the time orders writes, the counter orders those within one millisecond, and the
/// device id makes two devices' clocks differ and settles a tie the same way on every device. The server compares the text byte for byte, which is this order.
/// </summary>
public sealed class SyncClock
{
    private const int WallDigits = 12, CounterDigits = 4, PrefixLength = WallDigits + CounterDigits;

    /// <summary>A write heard of from further ahead than this does not move this clock (a device with a wrong clock would otherwise push every other one into its future).</summary>
    public static readonly TimeSpan MaxDrift = TimeSpan.FromDays(1);

    private readonly string _device;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private long _wall;
    private int _counter;

    public SyncClock(string deviceId) : this(deviceId, () => DateTimeOffset.UtcNow) { }

    public SyncClock(string deviceId, Func<DateTimeOffset> now)
    {
        if (string.IsNullOrEmpty(deviceId) || deviceId.Length > 64 || !deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("The device id is letters, digits and - _ . (up to 64).", nameof(deviceId));
        _device = deviceId;
        _now = now;
    }

    /// <summary>The clock of a write this device makes now: newer than every clock it has made or heard of.</summary>
    public string Next()
    {
        lock (_gate)
        {
            long wall = Math.Max(_now().ToUnixTimeMilliseconds(), _wall);
            if (wall == _wall) _counter++;
            else { _wall = wall; _counter = 0; }
            if (_counter > 0xFFFF) { _wall++; _counter = 0; }   // 65536 writes in a millisecond: the next one takes the next millisecond
            return Format(_wall, _counter, _device);
        }
    }

    /// <summary>The clock of a write that came from another device, so that the next one made here is newer than it.</summary>
    public void Receive(string? clock)
    {
        if (!TryParse(clock, out var wall, out var counter)) return;
        lock (_gate)
        {
            if (wall > _now().ToUnixTimeMilliseconds() + (long)MaxDrift.TotalMilliseconds) return;
            if (wall > _wall || wall == _wall && counter > _counter) { _wall = wall; _counter = counter; }
        }
    }

    private static string Format(long wall, int counter, string device) =>
        wall.ToString("x12", CultureInfo.InvariantCulture) + counter.ToString("x4", CultureInfo.InvariantCulture) + device;

    /// <summary>Reads the time and counter at the start of a clock; false if it is not one of ours.</summary>
    public static bool TryParse(string? clock, out long wall, out int counter)
    {
        wall = 0;
        counter = 0;
        if (clock is null || clock.Length <= PrefixLength) return false;
        // Lower case only: that is what sorts as the number, byte for byte.
        if (!clock.AsSpan(0, PrefixLength).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return long.TryParse(clock.AsSpan(0, WallDigits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out wall)
            && int.TryParse(clock.AsSpan(WallDigits, CounterDigits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out counter);
    }

    /// <summary>True if <paramref name="a"/> is a later write than <paramref name="b"/>: plain text order, as the server compares.</summary>
    public static bool IsNewer(string a, string b) => string.CompareOrdinal(a, b) > 0;
}
