using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Concentus;
using Concentus.Enums;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// A short sound for a room's soundboard, kept in Yaparr's own small format: 20 ms Opus frames (48 kHz, mono) one after
/// the other. Whoever adds a sound converts it once; everyone else only ever reads this format, never an MP3 or WAV
/// that a stranger made.
/// </summary>
public sealed class SoundClip
{
    public const int SampleRate = 48000, FrameSamples = 960, MaxSeconds = 20, MaxFrames = MaxSeconds * 1000 / 20;
    private const int HeaderBytes = 7, MaxFrameBytes = 255;
    private const float TargetLevel = 0.1f, PeakLimit = 0.9f;
    private static readonly byte[] Magic = "YSND"u8.ToArray();

    private readonly int[] _starts;

    private SoundClip(byte[] data, int[] starts)
    {
        Data = data;
        _starts = starts;
    }

    /// <summary>The sound as stored and sent.</summary>
    public byte[] Data { get; }

    public int Frames => _starts.Length;

    public TimeSpan Duration => TimeSpan.FromMilliseconds(Frames * 20);

    /// <summary>One Opus frame (20 ms).</summary>
    public ReadOnlySpan<byte> Frame(int index)
    {
        int start = _starts[index];
        return Data.AsSpan(start + 1, Data[start]);
    }

    /// <summary>
    /// Makes a sound from samples (any rate and channel count, -1 to 1). The sound is made equally loud as other sounds, so
    /// one loud file does not blast everybody. Throws <see cref="ArgumentException"/> if it is empty, silent or longer than
    /// <see cref="MaxSeconds"/> seconds.
    /// </summary>
    public static SoundClip FromPcm(ReadOnlySpan<float> samples, int sampleRate = SampleRate, int channels = 1)
    {
        if (channels < 1 || sampleRate < 8000) throw new ArgumentException("That sound has an unusable format.");
        var mono = ToMono48k(samples, sampleRate, channels);
        if (mono.Length == 0) throw new ArgumentException("That sound is empty.");
        if (mono.Length > MaxFrames * FrameSamples) throw new ArgumentException($"A sound can be at most {MaxSeconds} seconds long.");

        double sum = 0;
        float peak = 0;
        foreach (var s in mono) { sum += s * s; peak = Math.Max(peak, Math.Abs(s)); }
        float rms = (float)Math.Sqrt(sum / mono.Length);
        if (peak < 0.001f || rms < 0.0003f) throw new ArgumentException("That sound is silent.");
        float gain = Math.Min(TargetLevel / rms, PeakLimit / peak);
        for (int i = 0; i < mono.Length; i++) mono[i] *= gain;

        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 32000;
        encoder.Complexity = 8;
        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.WriteByte(1);
        int frames = (mono.Length + FrameSamples - 1) / FrameSamples;
        ms.Write(new[] { (byte)(frames >> 8), (byte)frames });
        var frame = new float[FrameSamples];
        var packet = new byte[MaxFrameBytes];
        for (int f = 0; f < frames; f++)
        {
            Array.Clear(frame);
            mono.AsSpan(f * FrameSamples, Math.Min(FrameSamples, mono.Length - f * FrameSamples)).CopyTo(frame);
            int length = encoder.Encode(frame, FrameSamples, packet, packet.Length);
            ms.WriteByte((byte)length);
            ms.Write(packet, 0, length);
        }
        return TryParse(ms.ToArray(), out var clip) ? clip : throw new InvalidOperationException("The encoded sound could not be read back.");
    }

    /// <summary>Reads a sound from its stored form. False if it is not one, is damaged or is too long.</summary>
    public static bool TryParse(byte[] data, out SoundClip clip)
    {
        clip = null!;
        if (data.Length < HeaderBytes || !data.AsSpan(0, 4).SequenceEqual(Magic) || data[4] != 1) return false;
        int frames = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(5));
        if (frames is < 1 or > MaxFrames) return false;
        var starts = new int[frames];
        int at = HeaderBytes;
        for (int f = 0; f < frames; f++)
        {
            if (at >= data.Length) return false;
            int length = data[at];
            if (length == 0 || at + 1 + length > data.Length) return false;
            starts[f] = at;
            at += 1 + length;
        }
        if (at != data.Length) return false;
        clip = new SoundClip(data, starts);
        return true;
    }

    /// <summary>The largest a stored sound can be.</summary>
    public const int MaxBytes = HeaderBytes + MaxFrames * (1 + MaxFrameBytes);

    /// <summary>Makes a sound from a WAV file (8, 16, 24 or 32 bit integers or 32 bit floats, any rate or channels).</summary>
    public static SoundClip FromWav(byte[] file)
    {
        if (!TryReadWav(file, out var samples, out int rate, out int channels))
            throw new ArgumentException("That is not a WAV file Yaparr can read.");
        return FromPcm(samples, rate, channels);
    }

    /// <summary>Reads a WAV file into samples (interleaved when it has several channels). A file longer than <paramref name="maxSeconds"/> throws <see cref="ArgumentException"/>.</summary>
    public static bool TryReadWav(byte[] d, out float[] samples, out int rate, out int channels, int maxSeconds = MaxSeconds)
    {
        samples = Array.Empty<float>();
        rate = channels = 0;
        if (d.Length < 44 || !d.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !d.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return false;
        int format = 0, bits = 0, dataAt = -1, dataLength = 0;
        for (int at = 12; at + 8 <= d.Length;)
        {
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 4)), int.MaxValue);
            var id = d.AsSpan(at, 4);
            int body = at + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= d.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(body + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(body + 14));
                if (format == 0xFFFE && size >= 26 && body + 26 <= d.Length) format = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(body + 24));
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataAt = body;
                dataLength = Math.Min(size, d.Length - body);
                break;
            }
            long next = (long)body + size + (size & 1);
            if (next > d.Length) break;
            at = (int)next;
        }
        if (dataAt < 0 || channels < 1 || channels > 8 || rate < 8000 || rate > 192000) return false;
        bool pcm = format == 1 && bits is 8 or 16 or 24 or 32, floating = format == 3 && bits == 32;
        if (!pcm && !floating) return false;
        int bytes = bits / 8, count = dataLength / bytes;
        // A WAV file that would be far too long is refused before it is turned into floats.
        if ((long)count / channels / rate > maxSeconds)
            throw new ArgumentException(maxSeconds == MaxSeconds ? $"A sound can be at most {MaxSeconds} seconds long." : $"That file is longer than {maxSeconds} seconds.");
        samples = new float[count];
        var s = d.AsSpan(dataAt, count * bytes);
        for (int i = 0; i < count; i++)
        {
            samples[i] = bits switch
            {
                8 => (s[i] - 128) / 128f,
                16 => BinaryPrimitives.ReadInt16LittleEndian(s[(i * 2)..]) / 32768f,
                24 => ((s[i * 3] << 8 | s[i * 3 + 1] << 16 | s[i * 3 + 2] << 24) >> 8) / 8388608f,
                _ when floating => BinaryPrimitives.ReadSingleLittleEndian(s[(i * 4)..]),
                _ => BinaryPrimitives.ReadInt32LittleEndian(s[(i * 4)..]) / 2147483648f,
            };
            if (float.IsNaN(samples[i])) samples[i] = 0;
        }
        return true;
    }

    private static float[] ToMono48k(ReadOnlySpan<float> samples, int rate, int channels)
    {
        int count = samples.Length / channels;
        var mono = new float[count];
        for (int i = 0; i < count; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += samples[i * channels + c];
            mono[i] = sum / channels;
        }
        if (rate == SampleRate) return mono;
        // Linear interpolation is enough for a short effect.
        long outCount = (long)count * SampleRate / rate;
        if (outCount > MaxFrames * FrameSamples) throw new ArgumentException($"A sound can be at most {MaxSeconds} seconds long.");
        var result = new float[outCount];
        double step = (double)rate / SampleRate;
        for (int i = 0; i < result.Length; i++)
        {
            double pos = i * step;
            int a = (int)pos;
            int b = Math.Min(a + 1, count - 1);
            float t = (float)(pos - a);
            result[i] = mono[Math.Min(a, count - 1)] * (1 - t) + mono[b] * t;
        }
        return result;
    }

    /// <summary>The whole sound as samples (48 kHz, mono), for previews and tests.</summary>
    public float[] ToSamples()
    {
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
        var result = new float[Frames * FrameSamples];
        for (int f = 0; f < Frames; f++) decoder.Decode(Frame(f), result.AsSpan(f * FrameSamples, FrameSamples), FrameSamples, false);
        return result;
    }

    public static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

/// <summary>A sound on a room's soundboard.</summary>
/// <param name="Available">The sound itself is on this computer, so it can be played.</param>
public sealed record SoundInfo(string Id, string Name, PeerId Owner, string OwnerName, string Hash, int Size, TimeSpan Duration, bool Available);

/// <summary>A signed statement by a room member that a sound is on the room's soundboard (or has been removed from it).</summary>
public sealed record StoredSound(string From, string Pub, string Id, string Name, string OwnerName, string Hash, int Size, int Ms, long Ts, bool Deleted, string Sig);

/// <summary>A signed statement that someone played a sound.</summary>
public sealed record StoredPlay(string From, string Pub, string Id, string Sound, string Hash, long Ts, string Sig);

internal static class SignedSound
{
    private const int MaxName = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static StoredSound Create(PeerIdentity identity, string roomKey, string id, string name, string ownerName, string hash, int size, int ms, long timestamp, bool deleted)
    {
        var unsigned = new StoredSound(identity.Id.Value, Convert.ToBase64String(identity.PublicKey), id, Clip(name, MaxName), Clip(ownerName, MaxName), hash, size, ms, timestamp, deleted, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredSound s, string roomKey)
    {
        if (!IsHex(s.Id, 16) || !IsHex(s.Hash, 64) || s.Name.Length > MaxName || s.OwnerName.Length > MaxName) return false;
        if (!s.Deleted && (s.Name.Trim().Length == 0 || s.Size is < 1 or > SoundClip.MaxBytes || s.Ms is < 20 or > SoundClip.MaxSeconds * 1000)) return false;
        if (s.Ts > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(s.Pub);
            return PeerId.FromPublicKey(pub).Value == s.From && PeerIdentity.Verify(pub, Payload(roomKey, s), Convert.FromBase64String(s.Sig));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Payload(string roomKey, StoredSound s)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-sound-v1"); w.Write(roomKey); w.Write(s.From); w.Write(s.Id); w.Write(s.Name); w.Write(s.OwnerName);
            w.Write(s.Hash); w.Write(s.Size); w.Write(s.Ms); w.Write(s.Ts); w.Write(s.Deleted);
        }
        return ms.ToArray();
    }

    public static StoredPlay CreatePlay(PeerIdentity identity, string roomKey, string sound, string hash, long timestamp)
    {
        var unsigned = new StoredPlay(identity.Id.Value, Convert.ToBase64String(identity.PublicKey), NewId(), sound, hash, timestamp, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(PlayPayload(roomKey, unsigned))) };
    }

    public static bool VerifyPlay(StoredPlay p, string roomKey, TimeSpan maxAge)
    {
        if (!IsHex(p.Id, 16) || !IsHex(p.Sound, 16) || !IsHex(p.Hash, 64)) return false;
        var now = DateTimeOffset.UtcNow;
        if (p.Ts > now.Add(MaxFutureSkew).ToUnixTimeMilliseconds() || p.Ts < now.Subtract(maxAge).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(p.Pub);
            return PeerId.FromPublicKey(pub).Value == p.From && PeerIdentity.Verify(pub, PlayPayload(roomKey, p), Convert.FromBase64String(p.Sig));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] PlayPayload(string roomKey, StoredPlay p)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-play-v1"); w.Write(roomKey); w.Write(p.From); w.Write(p.Id); w.Write(p.Sound); w.Write(p.Hash); w.Write(p.Ts);
        }
        return ms.ToArray();
    }

    private static bool IsHex(string s, int length) => s.Length == length && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
