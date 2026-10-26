using System.Net;
using System.Security.Cryptography;

namespace Yaparr.Discovery.Dht;

public readonly struct DhtId : IEquatable<DhtId>
{
    public const int Length = 20;
    private readonly byte[]? _bytes;

    public DhtId(byte[] bytes)
    {
        if (bytes.Length != Length) throw new ArgumentException("DHT ids are 20 bytes.", nameof(bytes));
        _bytes = bytes;
    }

    public byte[] Bytes => _bytes ?? new byte[Length];

    public static DhtId Random() => new(RandomNumberGenerator.GetBytes(Length));

    public static int CompareDistance(DhtId target, DhtId a, DhtId b)
    {
        var t = target.Bytes; var x = a.Bytes; var y = b.Bytes;
        for (int i = 0; i < Length; i++)
        {
            int c = (t[i] ^ x[i]).CompareTo(t[i] ^ y[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    public bool Equals(DhtId other) => Bytes.AsSpan().SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is DhtId o && Equals(o);
    public override int GetHashCode() => BitConverter.ToInt32(Bytes, 0);
    public override string ToString() => Convert.ToHexString(Bytes).ToLowerInvariant();
}

public readonly record struct DhtContact(DhtId Id, IPEndPoint EndPoint);

internal sealed class RoutingTable
{
    private readonly object _gate = new();
    private readonly Dictionary<IPEndPoint, (DhtContact Contact, long Seen)> _nodes = new();
    private readonly int _capacity;

    public RoutingTable(int capacity = 1024) => _capacity = capacity;

    public int Count { get { lock (_gate) return _nodes.Count; } }

    public void Add(DhtContact contact)
    {
        lock (_gate)
        {
            _nodes[contact.EndPoint] = (contact, Environment.TickCount64);
            if (_nodes.Count > _capacity)
            {
                var oldest = _nodes.MinBy(kv => kv.Value.Seen).Key;
                _nodes.Remove(oldest);
            }
        }
    }

    public void Remove(IPEndPoint endPoint)
    {
        lock (_gate) _nodes.Remove(endPoint);
    }

    public List<DhtContact> Closest(DhtId target, int count)
    {
        lock (_gate)
        {
            return _nodes.Values.Select(v => v.Contact)
                .OrderBy(c => c.Id, Comparer<DhtId>.Create((a, b) => DhtId.CompareDistance(target, a, b)))
                .Take(count)
                .ToList();
        }
    }
}
