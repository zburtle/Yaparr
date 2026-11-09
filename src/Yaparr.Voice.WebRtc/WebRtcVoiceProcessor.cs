using RNNoise.NET;
using SoundFlow.Extensions.WebRtc.Apm;

namespace Yaparr.Voice.WebRtc;

public enum NoiseSuppression { Off, Low, Moderate, High }

/// <summary>Which noise suppressor cleans the microphone. More can be added to compare them.</summary>
public enum NoiseSuppressor
{
    /// <summary>What Yaparr has always used: WebRTC's noise suppression at the chosen <see cref="NoiseSuppression"/> level (Off, Low, Moderate or High).</summary>
    YaparrCustom,

    /// <summary>RNNoise, a small neural network trained on speech. It has no levels: <see cref="NoiseSuppression"/> does not apply to it.</summary>
    RNNoise,
}

/// <summary>What to clean from the microphone.</summary>
public sealed record VoiceProcessing(bool EchoCancellation = true, NoiseSuppression Noise = NoiseSuppression.Moderate, NoiseSuppressor Suppressor = NoiseSuppressor.YaparrCustom)
{
    /// <summary>True when there would be nothing to do, so no processor is needed.</summary>
    public bool IsOff => !EchoCancellation && Suppressor == NoiseSuppressor.YaparrCustom && Noise == NoiseSuppression.Off;
}

/// <summary>
/// Echo cancellation from Google's WebRTC audio processing, and noise suppression from either that (<see cref="NoiseSuppressor.YaparrCustom"/>) or RNNoise
/// (<see cref="NoiseSuppressor.RNNoise"/>, which runs on what is left after the echo is taken out), for <see cref="VoiceSession.Processor"/>.
/// It works on 10 ms pieces at 48 kHz mono and is safe to call from the microphone and speaker threads at the same time.
/// Loading can fail (the native library is missing, or the system is too old for it): use <see cref="TryCreate"/>. RNNoise has a library of its own;
/// when it cannot load, <see cref="RnnoiseProblem"/> says why and the WebRTC noise suppression is used in its place.
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

    // RNNoise. The denoiser is made when it is asked for and let go when it is not; _denoiseGate keeps a change from landing in the middle of a frame.
    private readonly object _denoiseGate = new();
    private Denoiser? _denoiser;
    private static readonly object s_rnnoiseGate = new();
    private static bool s_rnnoiseProbed;
    private static string? s_rnnoiseUnavailable;

    /// <summary>When RNNoise was asked for and could not be loaded, why (and noise suppression is the WebRTC one); otherwise null.</summary>
    public string? RnnoiseProblem { get; private set; }

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

    /// <summary>Whether RNNoise's native library can be loaded here; if not, why. The first answer is remembered.</summary>
    public static bool IsRnnoiseAvailable(out string? reason)
    {
        lock (s_rnnoiseGate)
        {
            if (!s_rnnoiseProbed)
            {
                s_rnnoiseProbed = true;
                try { using var probe = new Denoiser(); }
                catch (Exception ex) { s_rnnoiseUnavailable = Describe(ex); }
            }
            reason = s_rnnoiseUnavailable;
            return s_rnnoiseUnavailable == null;
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
        bool rnnoise = settings.Suppressor == NoiseSuppressor.RNNoise && IsRnnoiseAvailable(out _);
        RnnoiseProblem = settings.Suppressor == NoiseSuppressor.RNNoise && !rnnoise ? s_rnnoiseUnavailable : null;
        // RNNoise works on what the echo canceller leaves, in place of the WebRTC noise suppression (the two are not stacked).
        var noise = settings.Suppressor == NoiseSuppressor.RNNoise && rnnoise ? NoiseSuppression.Off : settings.Noise;
        lock (_denoiseGate)
        {
            if (rnnoise && _denoiser == null) _denoiser = new Denoiser();
            else if (!rnnoise && _denoiser != null) { _denoiser.Dispose(); _denoiser = null; }
        }

        using var config = new ApmConfig();
        config.SetEchoCanceller(settings.EchoCancellation, false);
        config.SetNoiseSuppression(noise != NoiseSuppression.Off, noise switch
        {
            NoiseSuppression.Low => NoiseSuppressionLevel.Low,
            NoiseSuppression.High => NoiseSuppressionLevel.High,
            _ => NoiseSuppressionLevel.Moderate,
        });
        // Automatic volume is not done here: the old gain controller made quiet breathing swell between words with open microphones,
        // and the newer one, switched on through this wrapper, was measured to change nothing at all (the wrapper offers no settings
        // for it). The library's own SpeechLeveler does it instead, for the microphone and for each person heard.
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
                lock (_denoiseGate) _denoiser?.Denoise(piece);
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
            lock (_denoiseGate) { _denoiser?.Dispose(); _denoiser = null; }
            _apm.Dispose();
            _stream.Dispose();
        }
        finally { _life.ExitWriteLock(); }
    }
}
