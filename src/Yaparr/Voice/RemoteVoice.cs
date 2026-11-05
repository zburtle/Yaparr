using Concentus;
using Yaparr.Identity;

namespace Yaparr.Voice;

/// <summary>
/// The header of a voice packet: version and marker (1 byte), room tag (4), sequence (2) and a timestamp (4), then the Opus frame.
/// Receivers do not use the timestamp, and senders have always counted it in steps of 960 from 0, so its low six bits are always
/// zero. The low byte now carries what RFC 6464 calls the client-to-mixer audio level, so that whoever passes a packet on, or has to
/// choose whose voices to decode, can tell who is talking and how loudly without decoding: bit 0 set (never so in the old
/// timestamps), bits 1 to 6 the level in steps of 2 dB below full scale (0 to 126 dB), bit 7 set when the sender takes the frame for
/// speech. Older versions ignore it, and packets from older versions simply have none.
/// </summary>
internal static class VoicePacket
{
    public const int HeaderSize = 11;
    public const byte Version = 1;
    private const byte MarkerFlag = 0x80;

    /// <param name="level">The byte made by <see cref="EncodeLevel"/>, or 0 for none.</param>
    public static int Write(Span<byte> dst, uint roomTag, ushort seq, uint timestamp, bool marker, ReadOnlySpan<byte> opus, byte level = 0)
    {
        dst[0] = (byte)(Version | (marker ? MarkerFlag : 0));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dst[1..], roomTag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(dst[5..], seq);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dst[7..], level != 0 ? (timestamp & ~0xFFu) | level : timestamp);
        opus.CopyTo(dst[HeaderSize..]);
        return HeaderSize + opus.Length;
    }

    public static bool TryParse(ReadOnlySpan<byte> src, out uint roomTag, out ushort seq, out bool marker, out ReadOnlySpan<byte> opus) =>
        TryParse(src, out roomTag, out seq, out marker, out _, out opus);

    public static bool TryParse(ReadOnlySpan<byte> src, out uint roomTag, out ushort seq, out bool marker, out uint timestamp, out ReadOnlySpan<byte> opus)
    {
        roomTag = 0; seq = 0; marker = false; timestamp = 0; opus = default;
        if (src.Length <= HeaderSize || (src[0] & 0x7F) != Version) return false;
        marker = (src[0] & MarkerFlag) != 0;
        roomTag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(src[1..]);
        seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(src[5..]);
        timestamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(src[7..]);
        opus = src[HeaderSize..];
        return true;
    }

    /// <summary>The level byte for a frame: its loudness (RMS, dB relative to full scale, 0 or below) and whether it is speech.</summary>
    public static byte EncodeLevel(float dbfs, bool speech)
    {
        int steps = float.IsNaN(dbfs) ? 63 : Math.Clamp((int)MathF.Round(-dbfs / 2f), 0, 63);
        return (byte)((speech ? 0x80 : 0) | (steps << 1) | 1);
    }

    /// <summary>
    /// True when the sender says the frame in a voice packet is not speech (a pause or noise). A packet from an older version, or one that
    /// does not parse, says nothing and is not taken for silence. A node that passes voice on can let these go first when it is short of room.
    /// </summary>
    public static bool SaysSilence(ReadOnlySpan<byte> packet) =>
        TryParse(packet, out _, out _, out _, out uint timestamp, out _) && TryDecodeLevel(timestamp, out _, out bool speech) && !speech;

    /// <summary>Reads the level from a timestamp; false if the sender did not put one there.</summary>
    public static bool TryDecodeLevel(uint timestamp, out float dbfs, out bool speech)
    {
        byte b = (byte)timestamp;
        dbfs = 0; speech = false;
        if ((b & 1) == 0) return false;
        dbfs = -((b >> 1) & 0x3F) * 2f;
        speech = (b & 0x80) != 0;
        return true;
    }
}

/// <summary>What a sender says about their own audio in their packets: how loud it is and whether they take it for speech.</summary>
public readonly record struct SenderSignal(float Dbfs, bool Speech);

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
    private bool _hasSignal;
    private double _speechShare, _senderDbfs;

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

    public void Enqueue(ushort seq, ReadOnlySpan<byte> opus, bool hasSignal = false, float senderDbfs = 0, bool senderSpeech = false)
    {
        lock (_gate)
        {
            Received++;
            if (hasSignal)
            {
                // A sender that tells us: how much of the time they say they are talking, and how loud they are then.
                if (!_hasSignal) { _hasSignal = true; _speechShare = senderSpeech ? 1 : 0; _senderDbfs = senderDbfs; }
                else
                {
                    _speechShare = _speechShare * 0.8 + (senderSpeech ? 0.2 : 0);
                    if (senderSpeech) _senderDbfs += (senderDbfs - _senderDbfs) * 0.2;
                }
            }
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
        lock (_gate)
        {
            if (nowMs - _lastPacketAt > 200) return 0;
            if (!_hasSignal) return _loudness;
            // Someone who says they are talking beats everyone who does not say so (the size of a packet is no judge of that when a
            // microphone is noisy), and the louder of those comes first; someone who says they are not talking comes last.
            return _speechShare > 0.5 ? 1000 + Math.Max(0, 100 + _senderDbfs) * 10 : _loudness * 0.1;
        }
    }

    /// <summary>What this person's own packets say about how loud they are and whether they are talking; null for a sender that does not say (an older version).</summary>
    public SenderSignal? Signal
    {
        get { lock (_gate) return _hasSignal ? new SenderSignal((float)_senderDbfs, _speechShare > 0.5) : null; }
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
