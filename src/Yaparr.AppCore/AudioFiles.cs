using NLayer;
using NVorbis;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>Turns a WAV, MP3 or OGG file into samples for a soundboard sound. The library only ever receives samples, never a stranger's file.</summary>
internal static class AudioFiles
{
    /// <summary>Longest file read in full.</summary>
    public const int MaxFileSeconds = 180;

    /// <summary>Samples (interleaved when there are several channels) and their format. Throws <see cref="ArgumentException"/> with a message for the person.</summary>
    public static (float[] Samples, int Rate, int Channels) Decode(byte[] file)
    {
        try
        {
            if (file.Length >= 12 && file.AsSpan(0, 4).SequenceEqual("RIFF"u8))
            {
                if (SoundClip.TryReadWav(file, out var wav, out int wavRate, out int wavChannels, MaxFileSeconds)) return (wav, wavRate, wavChannels);
                throw new ArgumentException("That WAV file is in a format Yaparr cannot read.");
            }
            if (file.Length >= 4 && file.AsSpan(0, 4).SequenceEqual("OggS"u8)) return DecodeOgg(file);
            if (LooksLikeMp3(file)) return DecodeMp3(file);
        }
        catch (Exception ex) when (ex is not ArgumentException and not OutOfMemoryException)
        {
            throw new ArgumentException("That file could not be read as a sound.", ex);
        }
        throw new ArgumentException("Yaparr can read WAV, MP3 and OGG sound files.");
    }

    /// <summary>A sound from the first <see cref="SoundClip.MaxSeconds"/> seconds of a file.</summary>
    public static SoundClip ToClip(byte[] file)
    {
        var (samples, rate, channels) = Decode(file);
        long keep = (long)SoundClip.MaxSeconds * rate * channels;
        if (samples.Length > keep) samples = samples.AsSpan(0, (int)keep).ToArray();
        return SoundClip.FromPcm(samples, rate, channels);
    }

    private static bool LooksLikeMp3(byte[] d) =>
        d.Length >= 3 && ((d[0] == 'I' && d[1] == 'D' && d[2] == '3') || (d[0] == 0xFF && (d[1] & 0xE0) == 0xE0));

    private static (float[], int, int) DecodeOgg(byte[] file)
    {
        using var reader = new VorbisReader(new MemoryStream(file));
        return Read(reader.SampleRate, reader.Channels, (buffer, count) => reader.ReadSamples(buffer, 0, count));
    }

    private static (float[], int, int) DecodeMp3(byte[] file)
    {
        using var mpeg = new MpegFile(new MemoryStream(file));
        return Read(mpeg.SampleRate, mpeg.Channels, (buffer, count) => mpeg.ReadSamples(buffer, 0, count));
    }

    private static (float[], int, int) Read(int rate, int channels, Func<float[], int, int> read)
    {
        if (rate < 8000 || channels < 1) throw new ArgumentException("That file could not be read as a sound.");
        long limit = (long)MaxFileSeconds * rate * channels;
        var result = new List<float>();
        var buffer = new float[16384];
        int n;
        while ((n = read(buffer, buffer.Length)) > 0)
        {
            if (result.Count + n > limit) throw new ArgumentException($"That file is longer than {MaxFileSeconds} seconds.");
            result.AddRange(buffer.AsSpan(0, n));
        }
        return (result.ToArray(), rate, channels);
    }
}
