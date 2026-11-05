using Concentus;
using Yaparr.Identity;

namespace Yaparr.Voice;

internal static class VoicePacket
{
    public const int HeaderSize = 11;
    public const byte Version = 1;
    private const byte MarkerFlag = 0x80;

    public static int Write(Span<byte> dst, uint roomTag, ushort seq, uint timestamp, bool marker, ReadOnlySpan<byte> opus)
    {
        dst[0] = (byte)(Version | (marker ? MarkerFlag : 0));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dst[1..], roomTag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(dst[5..], seq);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dst[7..], timestamp);
        opus.CopyTo(dst[HeaderSize..]);
        return HeaderSize + opus.Length;
    }

    public static bool TryParse(ReadOnlySpan<byte> src, out uint roomTag, out ushort seq, out bool marker, out ReadOnlySpan<byte> opus)
    {
        roomTag = 0; seq = 0; marker = false; opus = default;
        if (src.Length <= HeaderSize || (src[0] & 0x7F) != Version) return false;
        marker = (src[0] & MarkerFlag) != 0;
        roomTag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(src[1..]);
        seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(src[5..]);
        opus = src[HeaderSize..];
        return true;
    }
}

/// <summary>
/// What we hear from one person: reorders their packets, waits a few frames to smooth out delivery,
/// asks Opus to cover for lost ones, and hands out one 20 ms frame at a time.
/// </summary>
internal sealed class RemoteVoice : IDisposable
{
    public const int FrameSamples = 960;
    private const int MaxBuffered = 40, SilentFramesBeforeStop = 4;

    private readonly int _minTarget, _maxTarget;
    private readonly object _gate = new();
    private readonly IOpusDecoder _decoder;
    private readonly Dictionary<ushort, byte[]> _packets = new();
    private bool _playing;
    private ushort _first, _expected;
    private int _target;
    private int _silent;
    private long _lastUnderrunAt;
    private long _lastRaisedAt;
    private double _loudness;
    private long _lastPacketAt;
    private bool _decoderStale;
    private readonly SpeechLeveler? _leveler;

    /// <param name="channels">1 for a voice; 2 for the sound of a shared screen, whose frames then hold <see cref="FrameSamples"/> x 2 interleaved samples.</param>
    /// <param name="minTarget">How many 20 ms packets to hold back before playing, which is also the least delay.</param>
    public RemoteVoice(PeerId id, int channels = 1, int minTarget = 2)
    {
        Id = id;
        _decoder = OpusCodecFactory.CreateDecoder(48000, channels);
        if (channels == 1) _leveler = new SpeechLeveler();
        _minTarget = minTarget;
        _maxTarget = Math.Max(8, minTarget + 6);
        _target = minTarget;
    }

    public PeerId Id { get; }
    public float Gain { get; set; } = 1f;
    public bool LocalMute { get; set; }

    /// <summary>Brings the frame just read to the loudness of a normal voice (see <see cref="SpeechLeveler"/>); on the mixing thread only. Nothing for the sound of a screen.</summary>
    public void Level(Span<float> frame) => _leveler?.Process(frame);

    public void ResetLevel() => _leveler?.Reset();

    /// <summary>How much the leveller is turning this person up (or down, negative) now, in dB.</summary>
    public float LevelGainDb => _leveler?.GainDb ?? 0f;
    public bool Speaking { get; private set; }
    public int Lost { get; private set; }
    public int Underruns { get; private set; }
    public int Received { get; private set; }

    /// <summary>How many 20 ms frames were decoded (and so cost something); frames that were skipped are not counted.</summary>
    public int Decoded { get; private set; }

    public event Action<bool>? SpeakingChanged;

    // Signed distance from b to a in sequence space, so 0 follows 65535.
    private static int Diff(ushort a, ushort b) => (short)(a - b);

    public void Enqueue(ushort seq, ReadOnlySpan<byte> opus)
    {
        lock (_gate)
        {
            Received++;
            // Opus spends few bytes on silence and background noise and many on speech, so the size of a packet says how much is being said.
            _loudness = _loudness * 0.9 + opus.Length * 0.1;
            _lastPacketAt = Environment.TickCount64;
            if (_playing && Diff(seq, _expected) < 0) return; // too late to use
            if (!_playing)
            {
                if (_packets.Count == 0 || Diff(seq, _first) < 0) _first = seq;
            }
            _packets[seq] = opus.ToArray();
            if (_packets.Count > MaxBuffered) DropOldestLocked(MaxBuffered / 2);
        }
    }

    /// <summary>How much this person has been saying lately (the smoothed size of their packets), or 0 once they have gone quiet.</summary>
    public double LoudnessAt(long nowMs)
    {
        lock (_gate) return nowMs - _lastPacketAt > 200 ? 0 : _loudness;
    }

    /// <summary>
    /// Moves on by one frame without decoding it: for someone who is not heard (muted, or not among the loudest, or we are deafened).
    /// They still count as speaking while their packets keep coming, so the indicator works; if they are decoded again later the decoder starts afresh.
    /// </summary>
    public void Skip(long nowMs)
    {
        bool? change = null;
        lock (_gate)
        {
            if (!_playing)
            {
                if (_packets.Count >= _target) { _playing = true; _expected = _first; _silent = 0; }
            }
            else
            {
                if (_packets.Count > _target + 6) DropOldestLocked(_target + 2);
                if (_packets.Remove(_expected)) { _expected++; _silent = 0; }
                else if (_packets.Keys.Any(s => Diff(s, _expected) > 0)) _expected++;
                else if (++_silent > SilentFramesBeforeStop) { _playing = false; _packets.Clear(); }
            }
            _decoderStale = true;
            if (_playing != Speaking) { Speaking = _playing; change = _playing; }
        }
        if (change != null) SpeakingChanged?.Invoke(change.Value);
    }

    // Fills one frame; returns false (and zeroes it) when this person is not being heard right now.
    public bool ReadFrame(Span<float> frame, long nowMs)
    {
        bool? change = null;
        bool produced;
        lock (_gate)
        {
            produced = Decode(frame, nowMs);
            if (produced != Speaking) { Speaking = produced; change = produced; }
        }
        if (change != null) SpeakingChanged?.Invoke(change.Value);
        if (!produced) frame.Clear();
        return produced;
    }

    private bool Decode(Span<float> frame, long nowMs)
    {
        if (!_playing)
        {
            if (_packets.Count < _target) return false;
            _playing = true;
            _expected = _first;
            _silent = 0;
        }

        // Frames were skipped since the last one decoded, so what the decoder remembers no longer follows from what it is given now.
        if (_decoderStale) { _decoder.ResetState(); _decoderStale = false; }

        // Latency crept up (a burst after a stall): skip ahead rather than stay behind forever.
        if (_packets.Count > _target + 6) DropOldestLocked(_target + 2);

        if (_packets.Remove(_expected, out var data))
        {
            _decoder.Decode(data, frame, FrameSamples, false);
            Decoded++;
            _expected++;
            _silent = 0;
            RelaxTarget(nowMs);
            return true;
        }

        bool newer = _packets.Keys.Any(s => Diff(s, _expected) > 0);
        if (newer)
        {
            // This one is lost but later ones are here: recover it from the next packet's forward error correction if
            // there is one, otherwise let Opus conceal it.
            Lost++;
            if (_packets.TryGetValue((ushort)(_expected + 1), out var next)) _decoder.Decode(next, frame, FrameSamples, true);
            else _decoder.Decode(ReadOnlySpan<byte>.Empty, frame, FrameSamples, false);
            _expected++;
            return true;
        }

        // Nothing newer: either the talker stopped, or we are waiting on a late packet.
        if (++_silent > SilentFramesBeforeStop)
        {
            _playing = false;
            _packets.Clear();
            _decoder.ResetState();
            return false;
        }
        if (_silent == 1) { Underruns++; GrowTarget(nowMs); }
        _decoder.Decode(ReadOnlySpan<byte>.Empty, frame, FrameSamples, false);
        return true;
    }

    private void GrowTarget(long nowMs)
    {
        _lastUnderrunAt = nowMs;
        if (_target < _maxTarget) _target++;
    }

    private void RelaxTarget(long nowMs)
    {
        if (_target > _minTarget && nowMs - _lastUnderrunAt > 8000 && nowMs - _lastRaisedAt > 8000)
        {
            _target--;
            _lastRaisedAt = nowMs;
        }
    }

    private void DropOldestLocked(int keep)
    {
        var ordered = _packets.Keys.OrderBy(s => Diff(s, _packets.Keys.First())).ToList();
        foreach (var s in ordered.Take(Math.Max(0, ordered.Count - keep))) _packets.Remove(s);
        if (_playing && ordered.Count > keep) _expected = ordered[^keep];
        else if (!_playing && _packets.Count > 0) _first = _packets.Keys.OrderBy(s => Diff(s, _packets.Keys.First())).First();
    }

    public void Dispose() { }
}
