using System.Text;

namespace Yaparr.Discovery.Dht;

public sealed class BDict : SortedDictionary<string, object>
{
    public BDict() : base(StringComparer.Ordinal) { }

    public byte[]? Bytes(string key) => TryGetValue(key, out var v) ? v as byte[] : null;
    public long? Int(string key) => TryGetValue(key, out var v) && v is long l ? l : null;
    public BDict? Dict(string key) => TryGetValue(key, out var v) ? v as BDict : null;
    public List<object>? List(string key) => TryGetValue(key, out var v) ? v as List<object> : null;
    public string? Str(string key) => Bytes(key) is { } b ? Encoding.Latin1.GetString(b) : null;
}

public static class Bencode
{
    private const int MaxDepth = 32;

    public static byte[] Encode(object value)
    {
        using var ms = new MemoryStream();
        Write(ms, value);
        return ms.ToArray();
    }

    public static object Decode(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        var value = Read(data, ref pos, 0);
        if (pos != data.Length) throw new FormatException("Trailing data after bencoded value.");
        return value;
    }

    private static void Write(Stream s, object value)
    {
        switch (value)
        {
            case long or int:
                s.WriteByte((byte)'i');
                WriteAscii(s, Convert.ToInt64(value).ToString());
                s.WriteByte((byte)'e');
                break;
            case byte[] b:
                WriteAscii(s, b.Length.ToString());
                s.WriteByte((byte)':');
                s.Write(b);
                break;
            case string str:
                Write(s, Encoding.UTF8.GetBytes(str));
                break;
            case IDictionary<string, object> d:
                s.WriteByte((byte)'d');
                foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    Write(s, Encoding.Latin1.GetBytes(kv.Key));
                    Write(s, kv.Value);
                }
                s.WriteByte((byte)'e');
                break;
            case IEnumerable<object> list:
                s.WriteByte((byte)'l');
                foreach (var item in list) Write(s, item);
                s.WriteByte((byte)'e');
                break;
            default:
                throw new ArgumentException($"Cannot bencode {value.GetType()}.");
        }
    }

    private static void WriteAscii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));

    private static object Read(ReadOnlySpan<byte> d, ref int pos, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("Bencode nesting too deep.");
        if (pos >= d.Length) throw new FormatException("Unexpected end of bencode data.");

        byte c = d[pos];
        if (c == 'i')
        {
            pos++;
            int end = d[pos..].IndexOf((byte)'e');
            if (end < 0) throw new FormatException("Unterminated integer.");
            var text = Encoding.ASCII.GetString(d.Slice(pos, end));
            if (!long.TryParse(text, out var n)) throw new FormatException("Bad integer.");
            pos += end + 1;
            return n;
        }
        if (c == 'l')
        {
            pos++;
            var list = new List<object>();
            while (true)
            {
                if (pos >= d.Length) throw new FormatException("Unterminated list.");
                if (d[pos] == 'e') { pos++; return list; }
                list.Add(Read(d, ref pos, depth + 1));
            }
        }
        if (c == 'd')
        {
            pos++;
            var dict = new BDict();
            while (true)
            {
                if (pos >= d.Length) throw new FormatException("Unterminated dictionary.");
                if (d[pos] == 'e') { pos++; return dict; }
                if (Read(d, ref pos, depth + 1) is not byte[] key) throw new FormatException("Dictionary key must be a string.");
                dict[Encoding.Latin1.GetString(key)] = Read(d, ref pos, depth + 1);
            }
        }
        if (c is >= (byte)'0' and <= (byte)'9')
        {
            int colon = d[pos..].IndexOf((byte)':');
            if (colon < 0) throw new FormatException("Bad string length.");
            if (!int.TryParse(Encoding.ASCII.GetString(d.Slice(pos, colon)), out var len) || len < 0)
                throw new FormatException("Bad string length.");
            pos += colon + 1;
            if (len > d.Length - pos) throw new FormatException("String exceeds data.");
            var bytes = d.Slice(pos, len).ToArray();
            pos += len;
            return bytes;
        }
        throw new FormatException($"Unexpected byte 0x{c:X2} in bencode data.");
    }
}
