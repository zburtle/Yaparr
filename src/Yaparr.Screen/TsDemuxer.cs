namespace Yaparr.Screen;

/// <summary>One picture (an H.264 access unit in Annex B form) or one frame of sound (AAC with its ADTS header) out of a transport stream, with its time stamp in 90 kHz ticks.</summary>
public readonly record struct TsUnit(bool IsVideo, long Pts, byte[] Data);

/// <summary>
/// Takes an MPEG transport stream apart as it arrives (what a Jellyfin server sends for a film: H.264 and AAC, see <see cref="JellyfinSharer"/>), and hands over every picture and
/// every frame of sound with its time stamp, in the order they come. Nothing is decoded here. A picture is handed over when the next one begins (the stream does not say how long it is),
/// so the last one comes with <see cref="Finish"/>.
/// </summary>
public sealed class TsDemuxer
{
    private readonly Action<TsUnit> _onUnit;
    private readonly Dictionary<int, int> _types = new();   // pid -> stream type
    private readonly Dictionary<int, MemoryStream> _pes = new();
    private readonly List<int> _pmtPids = new();
    private readonly byte[] _carry = new byte[188];
    private int _carried;

    public TsDemuxer(Action<TsUnit> onUnit) => _onUnit = onUnit;

    /// <summary>Packets that did not start with the sync byte (the demuxer found its way back to the next one).</summary>
    public long SyncLosses { get; private set; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_carried > 0)
            {
                int take = Math.Min(188 - _carried, data.Length);
                data[..take].CopyTo(_carry.AsSpan(_carried));
                _carried += take;
                data = data[take..];
                if (_carried < 188) return;
                Packet(_carry);
                _carried = 0;
                continue;
            }
            if (data[0] != 0x47)
            {
                SyncLosses++;
                int next = data.IndexOf((byte)0x47);
                if (next < 0) return;
                data = data[next..];
                continue;
            }
            if (data.Length < 188)
            {
                data.CopyTo(_carry);
                _carried = data.Length;
                return;
            }
            Packet(data[..188]);
            data = data[188..];
        }
    }

    /// <summary>The stream has ended: the last unit of each stream is handed over.</summary>
    public void Finish()
    {
        foreach (var pid in _pes.Keys.ToList()) Flush(pid);
    }

    private void Packet(ReadOnlySpan<byte> p)
    {
        bool start = (p[1] & 0x40) != 0;
        int pid = ((p[1] & 0x1F) << 8) | p[2];
        int adaptation = (p[3] >> 4) & 3;
        int offset = 4;
        if ((adaptation & 2) != 0) offset += 1 + p[4];
        if ((adaptation & 1) == 0 || offset >= 188) return;
        var payload = p[offset..];

        if (pid == 0) { if (start) Pat(payload); return; }
        if (_pmtPids.Contains(pid)) { if (start) Pmt(payload); return; }
        if (!_types.TryGetValue(pid, out var type) || type is not (0x1B or 0x0F)) return;   // only H.264 and ADTS AAC are read

        if (start)
        {
            Flush(pid);
            _pes[pid] = new MemoryStream();
        }
        if (_pes.TryGetValue(pid, out var buffer)) buffer.Write(payload);
    }

    private void Pat(ReadOnlySpan<byte> payload)
    {
        var t = payload[(1 + payload[0])..];
        if (t.Length < 12) return;
        int length = ((t[1] & 0x0F) << 8) | t[2];
        for (int i = 8; i + 4 <= Math.Min(t.Length, 3 + length - 4); i += 4)
        {
            int program = (t[i] << 8) | t[i + 1];
            int pmt = ((t[i + 2] & 0x1F) << 8) | t[i + 3];
            if (program != 0 && !_pmtPids.Contains(pmt)) _pmtPids.Add(pmt);
        }
    }

    private void Pmt(ReadOnlySpan<byte> payload)
    {
        var t = payload[(1 + payload[0])..];
        if (t.Length < 12) return;
        int length = ((t[1] & 0x0F) << 8) | t[2];
        int infoLength = ((t[10] & 0x0F) << 8) | t[11];
        int i = 12 + infoLength;
        while (i + 5 <= Math.Min(t.Length, 3 + length - 4))
        {
            int type = t[i];
            int pid = ((t[i + 1] & 0x1F) << 8) | t[i + 2];
            int es = ((t[i + 3] & 0x0F) << 8) | t[i + 4];
            _types.TryAdd(pid, type);
            i += 5 + es;
        }
    }

    private void Flush(int pid)
    {
        if (!_pes.Remove(pid, out var buffer) || buffer.Length < 9) return;
        var pes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1) return;
        int headerLength = pes[8];
        if ((pes[7] & 0x80) == 0 || pes.Length < 14 || pes.Length <= 9 + headerLength) return;   // no time stamp, or nothing in it
        long pts = ((long)(pes[9] >> 1 & 7) << 30) | ((long)pes[10] << 22) | ((long)(pes[11] >> 1) << 15) | ((long)pes[12] << 7) | (long)(pes[13] >> 1);
        var data = pes[(9 + headerLength)..];
        if (_types[pid] == 0x1B) _onUnit(new TsUnit(true, pts, data.ToArray()));
        else Adts(data, pts);
    }

    private static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

    // A sound packet holds several ADTS frames of 1024 samples; each is handed over on its own, with the time stamp it has by its place in the packet.
    private void Adts(ReadOnlySpan<byte> data, long pts)
    {
        int i = 0, n = 0;
        while (i + 7 <= data.Length)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xF0) != 0xF0) { i++; continue; }
            int rateIndex = (data[i + 2] >> 2) & 0xF;
            int length = ((data[i + 3] & 3) << 11) | (data[i + 4] << 3) | (data[i + 5] >> 5);
            if (length < 7 || i + length > data.Length || rateIndex >= Rates.Length) { i++; continue; }
            _onUnit(new TsUnit(false, pts + n * 1024L * 90000 / Rates[rateIndex], data.Slice(i, length).ToArray()));
            n++;
            i += length;
        }
    }
}
