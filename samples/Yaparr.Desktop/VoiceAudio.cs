using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Enums;
using SoundFlow.Structs;
using Yaparr.Voice;

namespace Yaparr.Desktop;

/// <summary>Connects a voice session to the microphone and speakers.</summary>
internal sealed class VoiceAudio : IDisposable
{
    private static MiniAudioEngine? s_engine;
    private static readonly object s_gate = new();

    private static MiniAudioEngine Engine
    {
        get
        {
            lock (s_gate)
            {
                if (s_engine == null) s_engine = new MiniAudioEngine();
                s_engine.UpdateAudioDevicesInfo();
                return s_engine;
            }
        }
    }

    public static IReadOnlyList<string> InputNames() => Engine.CaptureDevices.Select(d => d.Name).ToList();
    public static IReadOnlyList<string> OutputNames() => Engine.PlaybackDevices.Select(d => d.Name).ToList();

    private static AudioFormat Format => new()
    {
        Format = SampleFormat.F32, Channels = 1, SampleRate = VoiceSession.SampleRate, Layout = AudioFormat.GetLayoutFromChannels(1),
    };

    private sealed class Speaker : SoundComponent
    {
        private readonly VoiceSession _session;
        public Speaker(AudioEngine engine, AudioFormat format, VoiceSession session) : base(engine, format) => _session = session;
        protected override void GenerateAudio(Span<float> buffer, int channels) => _session.PullPlayback(buffer);
    }

    // Plays a clip once and then goes silent.
    private sealed class Clip : SoundComponent
    {
        private readonly float[] _samples;
        private int _position;
        public Clip(AudioEngine engine, AudioFormat format, float[] samples) : base(engine, format) => _samples = samples;
        protected override void GenerateAudio(Span<float> buffer, int channels)
        {
            for (int frame = 0; frame + channels <= buffer.Length; frame += channels)
            {
                float value = _position < _samples.Length ? _samples[_position++] : 0;
                for (int c = 0; c < channels; c++) buffer[frame + c] = value;
            }
        }
    }

    /// <summary>Plays a short mono clip (48 kHz) on the chosen speakers, and closes the device when it has finished.</summary>
    public static void PlayOnce(float[] samples, string? output)
    {
        var engine = Engine;
        DeviceInfo? info = engine.PlaybackDevices.Where(d => d.Name == output).Select(d => (DeviceInfo?)d).FirstOrDefault();
        var device = engine.InitializePlaybackDevice(info, Format, new MiniAudioDeviceConfig());
        device.MasterMixer.AddComponent(new Clip(engine, Format, samples));
        device.Start();
        int ms = samples.Length * 1000 / VoiceSession.SampleRate + 250;
        _ = Task.Delay(ms).ContinueWith(_ =>
        {
            try { device.Stop(); } catch (Exception) { }
            device.Dispose();
        });
    }

    private readonly AudioCaptureDevice _capture;
    private readonly AudioPlaybackDevice _playback;

    public VoiceAudio(VoiceSession session, string? input, string? output)
    {
        var engine = Engine;
        DeviceInfo? inInfo = engine.CaptureDevices.Where(d => d.Name == input).Select(d => (DeviceInfo?)d).FirstOrDefault();
        DeviceInfo? outInfo = engine.PlaybackDevices.Where(d => d.Name == output).Select(d => (DeviceInfo?)d).FirstOrDefault();

        _playback = engine.InitializePlaybackDevice(outInfo, Format, new MiniAudioDeviceConfig());
        _playback.MasterMixer.AddComponent(new Speaker(engine, Format, session));
        try
        {
            _capture = engine.InitializeCaptureDevice(inInfo, Format, new MiniAudioDeviceConfig());
        }
        catch
        {
            _playback.Dispose();
            throw;
        }
        _capture.OnAudioProcessed += (samples, _) => session.PushCapture(samples);
        _playback.Start();
        _capture.Start();
    }

    public void Dispose()
    {
        try { _capture.Stop(); } catch (Exception) { }
        try { _playback.Stop(); } catch (Exception) { }
        _capture.Dispose();
        _playback.Dispose();
    }
}
