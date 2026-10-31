using System.Collections.Concurrent;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Voice;

/// <summary>How the microphone decides when to send.</summary>
public enum VoiceActivation
{
    /// <summary>Sends while you are talking, judged by the input level.</summary>
    VoiceActivity,
    /// <summary>Sends only while <see cref="VoiceSession.PushToTalkHeld"/> is true.</summary>
    PushToTalk,
    /// <summary>Always sends.</summary>
    Open,
}

/// <summary>
/// Your presence in a room's voice channel. Audio devices are not part of the library: feed microphone samples to
/// <see cref="PushCapture"/> and play what <see cref="PullPlayback"/> returns (48 kHz, mono, float).
/// </summary>
public sealed class VoiceSession : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = RemoteVoice.FrameSamples;
    private const int PreRollFrames = 2, HangoverFrames = 10;

    private readonly ChatNode _node;
    private readonly ChatRoom _room;
    private readonly uint _tag;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<float[]> _capture = Channel.CreateBounded<float[]>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly IOpusEncoder _encoder;
    private readonly ConcurrentDictionary<PeerId, RemoteVoice> _remotes = new();
    private readonly ConcurrentDictionary<PeerId, CancellationTokenSource> _dials = new();
    private readonly float[] _frame = new float[FrameSamples];
    private int _frameFill;
    private readonly Queue<float[]> _preRoll = new();
    private int _hangover;
    private ushort _seq;
    private uint _timestamp;
    private bool _sending;
    private float _level;
    private readonly float[] _mix = new float[FrameSamples];
    private readonly float[] _tmp = new float[FrameSamples];
    private readonly float[] _ring = new float[FrameSamples * 4];
    private int _ringStart, _ringCount;
    private readonly object _playLock = new();
    private volatile bool _disposed;

    internal VoiceSession(ChatNode node, ChatRoom room)
    {
        _node = node;
        _room = room;
        _tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Convert.FromHexString(room.Key));
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 32000;
        _encoder.Complexity = 6;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _ = Task.Run(EncodeLoopAsync);
    }

    public ChatRoom Room => _room;

    public VoiceActivation Activation { get; set; } = VoiceActivation.VoiceActivity;

    /// <summary>The loudness (0 to 1, as RMS) above which voice activation starts sending.</summary>
    public float Sensitivity { get; set; } = 0.012f;

    public bool PushToTalkHeld { get; set; }

    private volatile IVoiceProcessor? _processor;

    /// <summary>
    /// Cleans the microphone before it is sent, and is told what the speakers play. The session does not own it:
    /// whoever created it disposes it. Set to null to send the microphone as it is.
    /// </summary>
    public IVoiceProcessor? Processor
    {
        get => _processor;
        set => _processor = value;
    }

    /// <summary>The processor threw an error and was switched off; the microphone carries on unprocessed.</summary>
    public event Action<Exception>? ProcessorFailed;

    private bool _muted, _deafened;

    /// <summary>Stops sending your microphone. Others see the muted state.</summary>
    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; _node.PublishVoiceState(_room, active: true, _muted, _deafened); }
    }

    /// <summary>Stops playing other people (and mutes you too, as in most voice chats).</summary>
    public bool Deafened
    {
        get => _deafened;
        set { if (_deafened == value) return; _deafened = value; _node.PublishVoiceState(_room, active: true, _muted, _deafened); }
    }

    /// <summary>Smoothed loudness of your microphone, 0 to 1.</summary>
    public float InputLevel => _level;

    /// <summary>True while your own microphone is being sent.</summary>
    public bool IsSending => _sending;

    /// <summary>Raised when someone (possibly you, with your own id) starts or stops talking.</summary>
    public event Action<PeerId, bool>? SpeakingChanged;

    public IReadOnlyCollection<PeerId> Speaking => _remotes.Values.Where(r => r.Speaking).Select(r => r.Id).ToList();

    private volatile float _outputGain = 1f;

    /// <summary>How loud everything you hear is, 0 to 4 (1 is as received). Loud peaks are rounded off rather than cut.</summary>
    public float OutputGain
    {
        get => _outputGain;
        set => _outputGain = Math.Clamp(value, 0f, 4f);
    }

    public void SetVolume(PeerId peer, float gain) => Remote(peer).Gain = Math.Clamp(gain, 0f, 2f);

    public void SetLocalMute(PeerId peer, bool muted) => Remote(peer).LocalMute = muted;

    private readonly ConcurrentDictionary<PeerId, bool> _soundboardMuted = new();
    private readonly List<SoundPlayback> _sounds = new();
    private volatile bool _allSoundboardsMuted;

    /// <summary>Silences one person's soundboard sounds without muting their voice.</summary>
    public void SetSoundboardMute(PeerId peer, bool muted)
    {
        if (muted) _soundboardMuted[peer] = true; else _soundboardMuted.TryRemove(peer, out _);
        if (muted) lock (_playLock) _sounds.RemoveAll(s => s.By == peer);
    }

    public bool IsSoundboardMuted(PeerId peer) => _soundboardMuted.ContainsKey(peer);

    /// <summary>Silences everybody's soundboard sounds (and yours) but not voices.</summary>
    public bool SoundboardsMuted
    {
        get => _allSoundboardsMuted;
        set { _allSoundboardsMuted = value; if (value) lock (_playLock) _sounds.Clear(); }
    }

    /// <summary>How loud soundboard sounds are against voices, 0 to 2.</summary>
    public float SoundboardVolume { get; set; } = 0.8f;

    internal void PlaySound(PeerId by, SoundClip clip)
    {
        if (_disposed || _deafened || _allSoundboardsMuted || _soundboardMuted.ContainsKey(by)) return;
        lock (_playLock) _sounds.Add(new SoundPlayback(by, clip));
    }

    /// <summary>How many packets we got from a person, how many had to be concealed and how often we ran dry.</summary>
    public (int Received, int Lost, int Underruns) Stats(PeerId peer) =>
        _remotes.TryGetValue(peer, out var r) ? (r.Received, r.Lost, r.Underruns) : (0, 0, 0);

    private RemoteVoice Remote(PeerId peer) => _remotes.GetOrAdd(peer, id =>
    {
        var remote = new RemoteVoice(id);
        remote.SpeakingChanged += speaking => SpeakingChanged?.Invoke(id, speaking);
        return remote;
    });

    // ---- microphone side --------------------------------------------------------------------

    /// <summary>Gives the session microphone samples (mono, 48 kHz, -1 to 1) in chunks of any size.</summary>
    public void PushCapture(ReadOnlySpan<float> samples)
    {
        if (_disposed) return;
        while (!samples.IsEmpty)
        {
            int n = Math.Min(samples.Length, FrameSamples - _frameFill);
            samples[..n].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += n;
            samples = samples[n..];
            if (_frameFill == FrameSamples)
            {
                CleanCapture(_frame);
                _capture.Writer.TryWrite((float[])_frame.Clone());
                _frameFill = 0;
            }
        }
    }

    // A processor that breaks must not stop the microphone: switch it off and carry on.
    private void CleanCapture(float[] frame)
    {
        if (_processor is not { } processor) return;
        try { processor.ProcessCapture(frame); }
        catch (Exception ex) { Fail(processor, ex); }
    }

    private void Fail(IVoiceProcessor processor, Exception ex)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _processor, null, processor), processor)) return;
        ProcessorFailed?.Invoke(ex);
    }

    private async Task EncodeLoopAsync()
    {
        var buffer = new byte[VoicePacket.HeaderSize + 400];
        var opus = new byte[400];
        try
        {
            await foreach (var frame in _capture.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                float level = Rms(frame);
                _level = Math.Max(level, _level * 0.9f);

                bool gate = !_muted && !_deafened && Activation switch
                {
                    VoiceActivation.Open => true,
                    VoiceActivation.PushToTalk => PushToTalkHeld,
                    _ => level >= Sensitivity,
                };
                if (gate) _hangover = HangoverFrames;
                bool transmit = gate || (_hangover > 0 && !_muted && !_deafened);
                if (!gate && _hangover > 0) _hangover--;

                if (!transmit)
                {
                    if (_sending) { _sending = false; SpeakingChanged?.Invoke(_node.Id, false); }
                    _preRoll.Enqueue(frame);
                    while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
                    continue;
                }

                if (!_sending)
                {
                    _sending = true;
                    SpeakingChanged?.Invoke(_node.Id, true);
                    bool first = true;
                    while (_preRoll.Count > 0) { Send(_preRoll.Dequeue(), first, buffer, opus); first = false; }
                    Send(frame, first, buffer, opus);
                }
                else Send(frame, false, buffer, opus);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Send(float[] frame, bool marker, byte[] buffer, byte[] opus)
    {
        int length = _encoder.Encode(frame, FrameSamples, opus, opus.Length);
        int total = VoicePacket.Write(buffer, _tag, _seq++, _timestamp, marker, opus.AsSpan(0, length));
        _timestamp += FrameSamples;
        _node.SendVoicePacket(_room, buffer.AsSpan(0, total));
    }

    private static float Rms(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        return (float)Math.Sqrt(sum / frame.Length);
    }

    // ---- speaker side -----------------------------------------------------------------------

    internal void OnPacket(PeerId from, ReadOnlySpan<byte> packet)
    {
        if (_disposed || !VoicePacket.TryParse(packet, out var tag, out var seq, out _, out var opus) || tag != _tag) return;
        Remote(from).Enqueue(seq, opus);
    }

    /// <summary>Fills <paramref name="output"/> with what everyone else is saying (mono, 48 kHz). Silence when nobody is.</summary>
    public void PullPlayback(Span<float> output)
    {
        var whole = output;
        lock (_playLock)
        {
            while (!output.IsEmpty)
            {
                if (_ringCount == 0) MixFrame();
                int n = Math.Min(output.Length, _ringCount);
                for (int i = 0; i < n; i++) output[i] = _ring[(_ringStart + i) % _ring.Length];
                _ringStart = (_ringStart + n) % _ring.Length;
                _ringCount -= n;
                output = output[n..];
            }
        }
        if (_processor is { } processor)
        {
            try { processor.ProcessPlayback(whole); }
            catch (Exception ex) { Fail(processor, ex); }
        }
    }

    private void MixFrame()
    {
        Array.Clear(_mix);
        long now = Environment.TickCount64;
        foreach (var remote in _remotes.Values)
        {
            if (!remote.ReadFrame(_tmp, now)) continue;
            if (remote.LocalMute || _deafened) continue;
            float gain = remote.Gain;
            for (int i = 0; i < FrameSamples; i++) _mix[i] += _tmp[i] * gain;
        }
        float soundGain = _deafened || _allSoundboardsMuted ? 0f : SoundboardVolume;
        for (int k = _sounds.Count - 1; k >= 0; k--)
        {
            var sound = _sounds[k];
            if (soundGain == 0f || !sound.ReadFrame(_tmp)) { _sounds.RemoveAt(k); continue; }
            for (int i = 0; i < FrameSamples; i++) _mix[i] += _tmp[i] * soundGain;
            if (sound.Finished) _sounds.RemoveAt(k);
        }
        float master = OutputGain;
        for (int i = 0; i < FrameSamples; i++)
        {
            float s = _mix[i] * master;
            // A boosted voice that goes past full scale is bent smoothly into the last 0.25, which sounds less harsh than a hard cut.
            if (master > 1f)
            {
                if (s > 0.75f) s = 0.75f + 0.25f * MathF.Tanh((s - 0.75f) / 0.25f);
                else if (s < -0.75f) s = -0.75f - 0.25f * MathF.Tanh((-s - 0.75f) / 0.25f);
            }
            else s = s > 1f ? 1f : s < -1f ? -1f : s;
            _ring[(_ringStart + i) % _ring.Length] = s;
        }
        _ringCount = FrameSamples;
    }

    // ---- who to connect to ------------------------------------------------------------------

    /// <summary>Called when the list of people in the channel changes: reach everyone we are not yet connected to.</summary>
    internal void RosterChanged(IReadOnlyCollection<PeerId> others)
    {
        if (_disposed) return;
        // In a big channel the audio goes through a tree, so there is no need to reach everyone.
        bool tree = _node.VoiceTreeActive(_room);
        foreach (var gone in _dials.Keys.Where(id => tree || !others.Contains(id)).ToList())
            if (_dials.TryRemove(gone, out var cts)) cts.Cancel();
        if (tree) return;
        foreach (var id in others)
        {
            if (_node.IsConnected(id) || _dials.ContainsKey(id)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            if (!_dials.TryAdd(id, cts)) continue;
            _ = Task.Run(async () =>
            {
                try { await _node.OpenDirectChatAsync(id, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                finally { _dials.TryRemove(id, out _); }
            });
        }
    }

    internal IEnumerable<PeerId> Heard => _remotes.Keys;

    public void Leave() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _capture.Writer.TryComplete();
        foreach (var cts in _dials.Values) cts.Cancel();
        _node.EndVoice(_room, this);
        if (_sending) SpeakingChanged?.Invoke(_node.Id, false);
        foreach (var r in _remotes.Values) r.Dispose();
    }
}
