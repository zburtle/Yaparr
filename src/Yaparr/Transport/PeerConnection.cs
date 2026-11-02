using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Transport;

public enum ConnectionState { Connecting, Established, Closed }

public sealed class PeerNetworkOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan HelloInterval { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(35);
    public TimeSpan UnackedTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public bool AcceptIncoming { get; init; } = true;

    /// <summary>
    /// The same settings for a connection that runs through another member: it checks more often, so that when the
    /// member in between disappears without saying goodbye, both ends notice in seconds and move to another path.
    /// </summary>
    internal PeerNetworkOptions ForRelayedPaths() => new()
    {
        ConnectTimeout = ConnectTimeout,
        HelloInterval = HelloInterval,
        KeepAliveInterval = KeepAliveInterval < TimeSpan.FromSeconds(4) ? KeepAliveInterval : TimeSpan.FromSeconds(4),
        IdleTimeout = IdleTimeout < TimeSpan.FromSeconds(15) ? IdleTimeout : TimeSpan.FromSeconds(15),
        UnackedTimeout = UnackedTimeout,
        AcceptIncoming = AcceptIncoming,
        RelayBytesPerSecond = RelayBytesPerSecond,
        RelayBytesPerSender = RelayBytesPerSender,
        MaxRelayedPairs = MaxRelayedPairs,
    };

    /// <summary>The most this node forwards for other members, in bytes per second (the default is about 2 Mbit/s).</summary>
    public int RelayBytesPerSecond { get; init; } = 256 * 1024;

    /// <summary>The most it forwards for any one member (about 770 kbit/s, enough for a dozen voice streams).</summary>
    public int RelayBytesPerSender { get; init; } = 96 * 1024;

    /// <summary>How many pairs of members it relays between at the same time.</summary>
    public int MaxRelayedPairs { get; init; } = 24;
}

public delegate void DataReceivedHandler(PeerConnection connection, byte channel, ReadOnlyMemory<byte> data, bool reliable);

public sealed class PeerConnection : IDisposable
{
    internal const byte Magic = 0xA7;
    private const byte PacketHello = 1, PacketData = 2;
    private const byte FrameUnreliable = 1, FrameReliable = 2, FrameAck = 3, FramePing = 4, FrameClose = 5;
    private const byte ProtocolVersion = 2;
    private static readonly byte[] HelloContext = Encoding.ASCII.GetBytes("yaparr-hello-v2");
    public const int InstanceIdLength = 8;

    public const int MaxUnreliablePayload = 1100;

    // Chat, relaying, files, voice and media use five; a peer asking for more is not playing by the rules.
    private const int MaxChannels = 16;

    private readonly object _gate = new();
    private readonly PeerIdentity _local;
    private readonly PeerId? _expected;
    private readonly Action<byte[], IPEndPoint> _send;
    private readonly PeerNetworkOptions _options;
    private readonly ECDiffieHellman _eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _ephPublic;
    private readonly byte[] _instance;
    private readonly Dictionary<byte, ReliableChannel> _channels = new();
    private readonly TaskCompletionSource<PeerConnection> _established = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SessionCrypto? _crypto;
    private byte[]? _remoteEph;

    // A hello is a signed statement that "this temporary key is mine". It is not proof that the one sending it now holds the key: a
    // hello is public to everyone who ever connected to its author, and can be repeated by anyone. So a hello only makes a
    // candidate session, and the connection counts as made (or the session as replaced) once a frame arrives that is sealed with
    // that session's keys, which only the holder of the temporary key can make.
    private sealed class Candidate
    {
        public required SessionCrypto Crypto { get; init; }
        public required byte[] Eph { get; init; }
        public required byte[] SessionId { get; init; }
        public required byte[] PublicKey { get; init; }
        public required byte[] Instance { get; init; }
        public required PeerId Id { get; init; }
        public required PeerId Account { get; init; }
        public long At { get; set; }
    }

    private readonly List<Candidate> _candidates = new();
    private const int MaxCandidates = 4;

    /// <summary>How long a candidate is kept without hearing the hello again. A real peer keeps repeating its hello until it is answered.</summary>
    internal static readonly TimeSpan CandidateLifetime = TimeSpan.FromSeconds(8);

    /// <summary>The connection was started by a hello from someone else (not dialled by us).</summary>
    internal bool Inbound { get; set; }

    /// <summary>A hello checked out but nothing has been confirmed yet.</summary>
    internal bool HasCandidate { get { lock (_gate) return _candidates.Count > 0; } }

    /// <summary>An inbound connection that has not proved anything yet. These are few and short-lived.</summary>
    internal bool IsProvisional { get { lock (_gate) return Inbound && State == ConnectionState.Connecting && _candidates.Count > 0; } }
    private long _startedAt, _lastHelloSent, _lastSent, _lastReceived, _lastPingReceived;

    internal PeerConnection(PeerIdentity local, IPEndPoint remote, PeerId? expected, Action<byte[], IPEndPoint> send, PeerNetworkOptions options, byte[] instance, PeerId? relayedVia = null)
    {
        RelayedVia = relayedVia;
        _instance = instance;
        _local = local;
        RemoteEndPoint = remote;
        _expected = expected;
        _send = send;
        _options = options;
        _ephPublic = _eph.ExportSubjectPublicKeyInfo();
        _startedAt = _lastReceived = Environment.TickCount64;
    }

    public IPEndPoint RemoteEndPoint { get; }

    public PeerId? RelayedVia { get; }
    public bool IsRelayed => RelayedVia != null;
    internal PeerId? Expected => _expected;
    internal long LastReceivedAt => Volatile.Read(ref _lastReceived);

    /// <summary>
    /// True once two pings have arrived from the other end within four seconds of each other. A version that only pings when it has been quiet for
    /// ten seconds never does, so this says the other end pings often (see <see cref="SendPing"/>) and can be judged by a short silence.
    /// </summary>
    internal bool SendsFastPings { get; private set; }

    /// <summary>Sends a ping now, so the other end hears from us even if there is nothing else to say.</summary>
    internal void SendPing()
    {
        lock (_gate)
        {
            if (State == ConnectionState.Established) SendFrameLocked(new[] { FramePing });
        }
    }

    /// <summary>Random id of the remote app run. A different one from the same peer means it restarted.</summary>
    internal byte[]? RemoteInstance { get; private set; }
    public ConnectionState State { get; private set; } = ConnectionState.Connecting;
    public PeerId? RemoteId { get; private set; }
    public byte[]? RemotePublicKey { get; private set; }

    /// <summary>
    /// The person the remote device belongs to, proved by the certificate in its hello. The same as
    /// <see cref="RemoteId"/> for a device that has no certificate, including every device of an older version.
    /// </summary>
    public PeerId? RemoteAccount { get; private set; }

    public byte[]? SessionId { get; private set; }

    public string? CloseReason { get; private set; }

    public Task<PeerConnection> WhenEstablished => _established.Task;

    public event Action<PeerConnection>? Connected;
    public event Action<PeerConnection>? Closed;
    public event DataReceivedHandler? DataReceived;

    public void SendReliable(byte channel, ReadOnlySpan<byte> message)
    {
        lock (_gate)
        {
            RequireEstablished();
            if (!_channels.TryGetValue(channel, out var ch)) _channels[channel] = ch = new ReliableChannel();
            ch.Enqueue(message);
            PumpLocked(Environment.TickCount64);
        }
    }

    public void SendUnreliable(byte channel, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxUnreliablePayload) throw new ArgumentException("Payload too large for one datagram.", nameof(payload));
        lock (_gate)
        {
            RequireEstablished();
            var frame = new byte[2 + payload.Length];
            frame[0] = FrameUnreliable; frame[1] = channel;
            payload.CopyTo(frame.AsSpan(2));
            SendFrameLocked(frame);
        }
    }

    private void RequireEstablished()
    {
        if (State != ConnectionState.Established) throw new InvalidOperationException($"Connection is {State}.");
    }

    public void Close(bool notifyPeer = true) => CloseCore(notifyPeer, "closed locally");

    private void CloseCore(bool notifyPeer, string reason)
    {
        lock (_gate)
        {
            if (State == ConnectionState.Closed) return;
            if (notifyPeer && State == ConnectionState.Established) SendFrameLocked(new[] { FrameClose });
            State = ConnectionState.Closed;
            CloseReason = reason;
            _crypto?.Dispose();
            _crypto = null;
            foreach (var candidate in _candidates) candidate.Crypto.Dispose();
            _candidates.Clear();
        }
        _established.TrySetException(new IOException($"Connection to {RemoteEndPoint} {reason}."));
        Closed?.Invoke(this);
    }

    public void Dispose()
    {
        Close(notifyPeer: false);
        _eph.Dispose();
    }

    internal void Tick(long now)
    {
        string? timeout = null;
        lock (_gate)
        {
            for (int i = _candidates.Count - 1; i >= 0; i--)
                if (now - _candidates[i].At > CandidateLifetime.TotalMilliseconds) { _candidates[i].Crypto.Dispose(); _candidates.RemoveAt(i); }
            switch (State)
            {
                case ConnectionState.Connecting:
                    if (Inbound && _candidates.Count == 0) timeout = "did not prove it holds its key";
                    else if (now - _startedAt > _options.ConnectTimeout.TotalMilliseconds) timeout = "timed out while punching";
                    else if (now - _lastHelloSent >= _options.HelloInterval.TotalMilliseconds) SendHelloLocked(now);
                    break;
                case ConnectionState.Established:
                    if (now - _lastReceived > _options.IdleTimeout.TotalMilliseconds) { timeout = "went silent"; break; }
                    if (_channels.Values.Any(c => c.OldestUnackedAge(now) > _options.UnackedTimeout.TotalMilliseconds)) { timeout = "did not acknowledge our data"; break; }
                    PumpLocked(now);
                    if (now - _lastSent >= _options.KeepAliveInterval.TotalMilliseconds) SendFrameLocked(new[] { FramePing });
                    break;
            }
        }
        if (timeout != null) CloseCore(notifyPeer: false, timeout);
    }

    internal void OnDatagram(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2 || packet[0] != Magic) return;

        var post = new List<Action>();
        lock (_gate)
        {
            if (State == ConnectionState.Closed) return;
            switch (packet[1])
            {
                case PacketHello: OnHelloLocked(packet, post); break;
                case PacketData: OnDataLocked(packet, post); break;
            }
        }
        foreach (var action in post) action();
    }

    private void OnHelloLocked(ReadOnlySpan<byte> packet, List<Action> post)
    {
        byte[] idKey, ephKey, sig, instance;
        byte[]? accountKey = null, certificate = null;
        try
        {
            int p = 2;
            if (packet[p++] != ProtocolVersion) return;
            idKey = ReadBlock(packet, ref p);
            ephKey = ReadBlock(packet, ref p);
            sig = ReadBlock(packet, ref p);
            instance = ReadBlock(packet, ref p);
            // A device linked to a person's account also says whose it is. Older versions never read past the instance.
            if (p < packet.Length)
            {
                accountKey = ReadBlock(packet, ref p);
                certificate = ReadBlock(packet, ref p);
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { return; }

        if (instance.Length != InstanceIdLength) return;
        if (!PeerIdentity.Verify(idKey, SignedHelloData(ephKey, instance), sig)) return;
        if (accountKey != null && !PeerIdentity.VerifyDevice(accountKey, idKey, certificate!)) return;

        var remoteId = PeerId.FromPublicKey(idKey);
        var remoteAccount = accountKey != null ? PeerId.FromPublicKey(accountKey) : remoteId;
        if (remoteId == _local.Id) { post.Add(() => CloseCore(false, "is ourselves")); return; }
        if (_expected is { } want && want != remoteId && want != remoteAccount) { post.Add(() => CloseCore(false, "presented an unexpected identity")); return; }
        if (State == ConnectionState.Established && RemoteId != remoteId) return;

        long now = Environment.TickCount64;

        // The temporary key of the session in use again: the other end repeating its hello until it hears from us. It says nothing
        // new, so it does not count as hearing from the other end (only a sealed frame does).
        if (State == ConnectionState.Established && _remoteEph != null && _remoteEph.AsSpan().SequenceEqual(ephKey))
        {
            if (now - _lastHelloSent >= 100) SendHelloLocked(now);
            return;
        }

        var candidate = _candidates.Find(c => c.Eph.AsSpan().SequenceEqual(ephKey));
        if (candidate == null)
        {
            var crypto = DeriveSession(ephKey, out var sessionId);
            candidate = new Candidate { Crypto = crypto, Eph = ephKey, SessionId = sessionId, PublicKey = idKey, Instance = instance, Id = remoteId, Account = remoteAccount, At = now };
            _candidates.Add(candidate);
            while (_candidates.Count > MaxCandidates) { _candidates[0].Crypto.Dispose(); _candidates.RemoveAt(0); }
        }
        candidate.At = now;

        // Answer with a frame sealed with the candidate's keys. The real owner of the temporary key can open it, and its own answer is
        // what confirms the session on this side.
        SendFrameWith(candidate.Crypto, new[] { FramePing });
        if (now - _lastHelloSent >= 100) SendHelloLocked(now);
    }

    // A frame opened with a candidate's keys proves the other end holds the temporary key it signed: that session replaces the one in
    // use (a restarted peer), or is the first.
    private void PromoteLocked(Candidate candidate, List<Action> post, long now)
    {
        _crypto?.Dispose();
        _crypto = candidate.Crypto;
        _candidates.Remove(candidate);
        foreach (var other in _candidates) other.Crypto.Dispose();
        _candidates.Clear();
        _remoteEph = candidate.Eph;
        SessionId = candidate.SessionId;
        _channels.Clear();
        RemoteId = candidate.Id;
        RemoteAccount = candidate.Account;
        RemotePublicKey = candidate.PublicKey;
        RemoteInstance = candidate.Instance;
        State = ConnectionState.Established;
        _lastSent = _lastReceived = now;
        // The other end may not have heard from us yet: tell it, so it can confirm on its side without waiting for something to say.
        SendFrameLocked(new[] { FramePing });
        post.Add(() => { _established.TrySetResult(this); Connected?.Invoke(this); });
    }

    private void OnDataLocked(ReadOnlySpan<byte> packet, List<Action> post)
    {
        if (_crypto == null && _candidates.Count == 0) return;
        var frame = _crypto?.Decrypt(packet, headerLength: 2);
        Candidate? via = null;
        if (frame == null)
            foreach (var candidate in _candidates)
                if ((frame = candidate.Crypto.Decrypt(packet, headerLength: 2)) != null) { via = candidate; break; }
        if (frame == null || frame.Length == 0) return;

        long now = Environment.TickCount64;
        if (via != null) PromoteLocked(via, post, now);
        _lastReceived = now;

        switch (frame[0])
        {
            case FrameUnreliable when frame.Length >= 2:
            {
                byte ch = frame[1];
                var data = frame.AsMemory(2);
                post.Add(() => DataReceived?.Invoke(this, ch, data, false));
                break;
            }
            case FrameReliable when frame.Length >= 7:
            {
                byte ch = frame[1];
                uint seq = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(2));
                bool last = (frame[6] & 1) != 0;
                if (!_channels.TryGetValue(ch, out var channel))
                {
                    if (_channels.Count >= MaxChannels) { post.Add(() => CloseCore(notifyPeer: false, "used too many channels")); break; }
                    _channels[ch] = channel = new ReliableChannel();
                }

                var delivered = new List<byte[]>();
                if (!channel.OnSegment(seq, last, frame[7..], delivered))
                {
                    post.Add(() => CloseCore(notifyPeer: false, "sent a message that is too large"));
                    break;
                }
                SendAckLocked(ch, channel.Expected);
                foreach (var msg in delivered) post.Add(() => DataReceived?.Invoke(this, ch, msg, true));
                break;
            }
            case FrameAck when frame.Length >= 6:
                if (_channels.TryGetValue(frame[1], out var acked))
                {
                    acked.OnAck(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(2)));
                    PumpLocked(now);
                }
                break;
            case FramePing:
                if (_lastPingReceived != 0 && now - _lastPingReceived <= 4000) SendsFastPings = true;
                _lastPingReceived = now;
                break;
            case FrameClose:
                post.Add(() => CloseCore(notifyPeer: false, "was closed by the remote peer"));
                break;
        }
    }

    private void PumpLocked(long now)
    {
        foreach (var (id, ch) in _channels)
        {
            byte channelId = id;
            ch.Pump(now, (seq, last, payload) =>
            {
                var frame = new byte[7 + payload.Length];
                frame[0] = FrameReliable; frame[1] = channelId;
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2), seq);
                frame[6] = (byte)(last ? 1 : 0);
                payload.CopyTo(frame.AsSpan(7));
                SendFrameLocked(frame);
            });
        }
    }

    private void SendAckLocked(byte channel, uint next)
    {
        var frame = new byte[6];
        frame[0] = FrameAck; frame[1] = channel;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2), next);
        SendFrameLocked(frame);
    }

    private void SendFrameLocked(byte[] frame)
    {
        if (_crypto == null) return;
        SendFrameWith(_crypto, frame);
        _lastSent = Environment.TickCount64;
    }

    private void SendFrameWith(SessionCrypto crypto, byte[] frame) => _send(crypto.Encrypt(new[] { Magic, PacketData }, frame), RemoteEndPoint);

    private void SendHelloLocked(long now)
    {
        var sig = _local.Sign(SignedHelloData(_ephPublic, _instance));
        using var ms = new MemoryStream();
        ms.WriteByte(Magic); ms.WriteByte(PacketHello); ms.WriteByte(ProtocolVersion);
        WriteBlock(ms, _local.PublicKey);
        WriteBlock(ms, _ephPublic);
        WriteBlock(ms, sig);
        WriteBlock(ms, _instance);
        if (_local.DeviceCertificate is { } certificate)
        {
            WriteBlock(ms, _local.AccountPublicKey);
            WriteBlock(ms, certificate);
        }
        _send(ms.ToArray(), RemoteEndPoint);
        _lastHelloSent = now;
    }

    private SessionCrypto DeriveSession(byte[] remoteEph, out byte[] sessionId)
    {
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(remoteEph, out _);
        var secret = _eph.DeriveRawSecretAgreement(theirs.PublicKey);

        bool low = _ephPublic.AsSpan().SequenceCompareTo(remoteEph) < 0;
        var salt = SHA256.HashData(low ? _ephPublic.Concat(remoteEph).ToArray() : remoteEph.Concat(_ephPublic).ToArray());
        sessionId = salt;
        var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, Encoding.ASCII.GetBytes("yaparr/v1/transport"));
        var lowToHigh = keys[..32];
        var highToLow = keys[32..];
        return low ? new SessionCrypto(lowToHigh, highToLow) : new SessionCrypto(highToLow, lowToHigh);
    }

    private static byte[] SignedHelloData(byte[] ephPublic, byte[] instance) => HelloContext.Concat(ephPublic).Concat(instance).ToArray();

    private static void WriteBlock(Stream s, byte[] data)
    {
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)data.Length);
        s.Write(len);
        s.Write(data);
    }

    private static byte[] ReadBlock(ReadOnlySpan<byte> packet, ref int pos)
    {
        int len = BinaryPrimitives.ReadUInt16BigEndian(packet[pos..]);
        pos += 2;
        var block = packet.Slice(pos, len).ToArray();
        pos += len;
        return block;
    }
}
