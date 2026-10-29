using SoundFlow.Extensions.WebRtc.Apm;

namespace Yaparr.Voice.WebRtc;

public enum NoiseSuppression { Off, Low, Moderate, High }

/// <summary>What to clean from the microphone.</summary>
public sealed record VoiceProcessing(bool EchoCancellation = true, NoiseSuppression Noise = NoiseSuppression.Moderate)
{
    /// <summary>True when there would be nothing to do, so no processor is needed.</summary>
    public bool IsOff => !EchoCancellation && Noise == NoiseSuppression.Off;
}

/// <summary>
/// Echo cancellation and noise suppression from Google's WebRTC audio processing, for <see cref="VoiceSession.Processor"/>.
/// It works on 10 ms pieces at 48 kHz mono and is safe to call from the microphone and speaker threads at the same time.
/// Loading can fail (the native library is missing, or the system is too old for it): use <see cref="TryCreate"/>.
/// </summary>
public sealed class WebRtcVoiceProcessor : IVoiceProcessor
{
    private const int Rate = 48000, Chunk = 480;

    private static readonly object s_probeGate = new();
    private static bool s_probed;
    private static string? s_unavailable;

    private readonly AudioProcessingModule _apm;
    private readonly StreamConfig _stream = new(Rate, 1);
    private readonly ReaderWriterLockSlim _life = new();
    private bool _disposed;
    private volatile bool _echo;

    // Reused for every piece, so nothing is allocated while talking. Each pair belongs to one thread.
    private readonly float[][] _captureIn = { new float[Chunk] }, _captureOut = { new float[Chunk] };
    private readonly float[][] _playIn = { new float[Chunk] }, _playOut = { new float[Chunk] };
    private int _playFill;

    private WebRtcVoiceProcessor(VoiceProcessing settings)
    {
        _apm = new AudioProcessingModule();
        try { Reconfigure(settings); }
        catch { _apm.Dispose(); throw; }
    }

    /// <summary>Whether the native library can be loaded here; if not, why. The first answer is remembered.</summary>
    public static bool IsAvailable(out string? reason)
    {
        lock (s_probeGate)
        {
            if (!s_probed)
            {
                s_probed = true;
                try { using var probe = new WebRtcVoiceProcessor(new VoiceProcessing()); }
                catch (Exception ex) { s_unavailable = Describe(ex); }
            }
            reason = s_unavailable;
            return s_unavailable == null;
        }
    }

    public static bool TryCreate(VoiceProcessing settings, out WebRtcVoiceProcessor? processor, out string? error)
    {
        processor = null;
        if (!IsAvailable(out error)) return false;
        try
        {
            processor = new WebRtcVoiceProcessor(settings);
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return false;
        }
    }

    // Loading a native library fails in several ways, none of them worth telling apart to the person using the app.
    private static string Describe(Exception ex) => ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException
        ? "the audio processing library could not be loaded on this system (" + (ex.InnerException ?? ex).Message + ")"
        : ex.Message;

    /// <summary>Changes what is cleaned while running.</summary>
    public void Reconfigure(VoiceProcessing settings)
    {
        using var config = new ApmConfig();
        config.SetEchoCanceller(settings.EchoCancellation, false);
        config.SetNoiseSuppression(settings.Noise != NoiseSuppression.Off, settings.Noise switch
        {
            NoiseSuppression.Low => NoiseSuppressionLevel.Low,
            NoiseSuppression.High => NoiseSuppressionLevel.High,
            _ => NoiseSuppressionLevel.Moderate,
        });
        // Automatic volume is left off: with open microphones it makes quiet breathing swell between words.
        config.SetGainController1(false, GainControlMode.AdaptiveDigital, 3, 9, true);
        config.SetGainController2(false);
        config.SetHighPassFilter(true);

        _life.EnterReadLock();
        try
        {
            if (_disposed) return;
            var error = _apm.ApplyConfig(config);
            if (error != ApmError.NoError) throw new InvalidOperationException("Audio processing refused its settings: " + error);
            _echo = settings.EchoCancellation;
        }
        finally { _life.ExitReadLock(); }
    }

    public void ProcessCapture(Span<float> frame)
    {
        if (frame.Length % Chunk != 0) throw new ArgumentException($"A frame must be a multiple of {Chunk} samples (10 ms).", nameof(frame));
        _life.EnterReadLock();
        try
        {
            if (_disposed) return;
            for (int at = 0; at < frame.Length; at += Chunk)
            {
                var piece = frame.Slice(at, Chunk);
                piece.CopyTo(_captureIn[0]);
                var error = _apm.ProcessStream(_captureIn, _stream, _stream, _captureOut);
                if (error != ApmError.NoError) throw new InvalidOperationException("Audio processing failed: " + error);
                _captureOut[0].CopyTo(piece);
            }
        }
        finally { _life.ExitReadLock(); }
    }

    public void ProcessPlayback(ReadOnlySpan<float> played)
    {
        if (!_echo) return;
        _life.EnterReadLock();
        try
        {
            if (_disposed) return;
            while (!played.IsEmpty)
            {
                int n = Math.Min(played.Length, Chunk - _playFill);
                played[..n].CopyTo(_playIn[0].AsSpan(_playFill));
                _playFill += n;
                played = played[n..];
                if (_playFill < Chunk) continue;
                _playFill = 0;
                var error = _apm.ProcessReverseStream(_playIn, _stream, _stream, _playOut);
                if (error != ApmError.NoError) throw new InvalidOperationException("Audio processing failed: " + error);
            }
        }
        finally { _life.ExitReadLock(); }
    }

    public void Dispose()
    {
        _life.EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _apm.Dispose();
            _stream.Dispose();
        }
        finally { _life.ExitWriteLock(); }
    }
}
