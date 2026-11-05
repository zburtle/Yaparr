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

    /// <summary>When this session started, on the tick clock; two of your devices that join at the same moment use it to settle which one stays.</summary>
    internal long JoinedAt { get; } = Environment.TickCount64;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<CapturedFrame> _capture = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly IOpusEncoder _encoder;
    private readonly ConcurrentDictionary<PeerId, RemoteVoice> _remotes = new();
    private readonly ConcurrentDictionary<PeerId, CancellationTokenSource> _dials = new();
    private readonly float[] _frame = new float[FrameSamples];
    private int _frameFill;
    private readonly Queue<CapturedFrame> _preRoll = new();
    private readonly SpeechDetector _speechDetector = new();
    private byte _levelByte;

    // A microphone frame with what the packet will say about it (see VoicePacket).
    private readonly record struct CapturedFrame(float[] Samples, byte Level);
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

    internal VoiceSession(ChatNode node, ChatRoom room, string channel = RoomChannels.DefaultVoice)
    {
        _node = node;
        _room = room;
        ChannelId = channel;
        // The first voice channel keeps the tag it always had; another one gets its own, so a packet for one never plays in the other.
        _tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(channel == RoomChannels.DefaultVoice
            ? Convert.FromHexString(room.Key)
            : System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(room.Key + channel)));
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 32000;
        _encoder.Complexity = 6;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _ = Task.Run(EncodeLoopAsync);
    }

    public ChatRoom Room => _room;

    /// <summary>The voice channel of the room this session is in.</summary>
    public string ChannelId { get; }

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

    private volatile bool _automaticVolume, _levelSpeakers;
    private readonly SpeechLeveler _micLeveler = new();

    /// <summary>
    /// Automatic volume for your microphone: a quiet one is raised and a loud one lowered towards a normal speaking level (see
    /// <see cref="SpeechLeveler"/>). The gain changes only while you talk and is held in pauses, so it is never raised on the noise of the room. Off by default.
    /// </summary>
    public bool AutomaticVolume
    {
        get => _automaticVolume;
        set { if (!value) _micLeveler.Reset(); _automaticVolume = value; }
    }

    /// <summary>
    /// Brings everyone you hear to a similar loudness, on top of each person's own volume, and keeps the mix of several people
    /// talking from clipping. Off by default.
    /// </summary>
    public bool LevelSpeakers
    {
        get => _levelSpeakers;
        set { _levelSpeakers = value; if (!value) foreach (var r in _remotes.Values) r.ResetLevel(); }
    }

    /// <summary>What a person's packets say about how loud they are and whether they are talking; null for someone on an older version, who does not say.</summary>
    public SenderSignal? SpeakerSignal(PeerId peer) => _remotes.TryGetValue(peer, out var r) ? r.Signal : null;

    /// <summary>How much the leveller is turning a person up (or down, negative), in dB; 0 when it is off.</summary>
    public float SpeakerLevelGainDb(PeerId peer) => _remotes.TryGetValue(peer, out var r) ? r.LevelGainDb : 0f;

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

    /// <summary>How many frames of a person were decoded; one that was not among the loudest, or was muted, costs nothing.</summary>
    internal int DecodedFrames(PeerId peer) => _remotes.TryGetValue(peer, out var r) ? r.Decoded : 0;

    /// <summary>The tag at the start of this channel's packets (for tests that make packets of their own).</summary>
    internal uint Tag => _tag;

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
                _capture.Writer.TryWrite(new CapturedFrame((float[])_frame.Clone(), _levelByte));
                _frameFill = 0;
            }
        }
    }

    // A processor that breaks must not stop the microphone: switch it off and carry on.
    private void CleanCapture(float[] frame)
    {
        if (_processor is { } processor)
        {
            try { processor.ProcessCapture(frame); }
            catch (Exception ex) { Fail(processor, ex); }
        }
        // After the cleaning, so that the noise that was taken out is not what the level is judged by.
        if (_automaticVolume) _micLeveler.Process(frame);
        // What goes in the packet: how loud this frame is and whether it is speech, judged on every frame in order (also those that are not sent).
        _levelByte = VoicePacket.EncodeLevel(_speechDetector.Update(frame, out bool speech), speech);
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
            await foreach (var captured in _capture.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var frame = captured.Samples;
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
                    _preRoll.Enqueue(captured);
                    while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
                    continue;
                }

                if (!_sending)
                {
                    _sending = true;
                    SpeakingChanged?.Invoke(_node.Id, true);
                    bool first = true;
                    while (_preRoll.Count > 0) { Send(_preRoll.Dequeue(), first, buffer, opus); first = false; }
                    Send(captured, first, buffer, opus);
                }
                else Send(captured, false, buffer, opus);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Send(CapturedFrame captured, bool marker, byte[] buffer, byte[] opus)
    {
        int length = _encoder.Encode(captured.Samples, FrameSamples, opus, opus.Length);
        int total = VoicePacket.Write(buffer, _tag, _seq++, _timestamp, marker, opus.AsSpan(0, length), captured.Level);
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
        if (_disposed || !VoicePacket.TryParse(packet, out var tag, out var seq, out _, out var timestamp, out var opus) || tag != _tag) return;
        bool signal = VoicePacket.TryDecodeLevel(timestamp, out float dbfs, out bool speech);
        Remote(from).Enqueue(seq, opus, signal, dbfs, speech);
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

    /// <summary>In a big call only this many people are decoded and mixed at a time; more than that talking at once is noise anyway.</summary>
    internal const int MaxDecoded = 8;

    private void MixFrame()
    {
        Array.Clear(_mix);
        long now = Environment.TickCount64;
        // With many people in the channel, the ones who are saying the most are the ones worth decoding.
        HashSet<RemoteVoice>? loudest = null;
        if (_remotes.Count > MaxDecoded)
            loudest = _remotes.Values.OrderByDescending(r => r.LoudnessAt(now)).Take(MaxDecoded).ToHashSet();
        foreach (var remote in _remotes.Values)
        {
            // Not heard, so not worth decoding.
            if (_deafened || remote.LocalMute || (loudest != null && !loudest.Contains(remote))) { remote.Skip(now); continue; }
            if (!remote.ReadFrame(_tmp, now)) continue;
            if (_levelSpeakers) remote.Level(_tmp);
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
        bool levelled = _levelSpeakers;
        for (int i = 0; i < FrameSamples; i++)
        {
            float s = _mix[i] * master;
            // A boosted voice that goes past full scale is bent smoothly into the last 0.25, which sounds less harsh than a hard cut;
            // so are several leveled voices that add up past it.
            if (master > 1f || levelled)
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
