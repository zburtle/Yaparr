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
    private long _startedAt, _lastHelloSent, _lastSent, _lastReceived;

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

    /// <summary>Random id of the remote app run. A different one from the same peer means it restarted.</summary>
    internal byte[]? RemoteInstance { get; private set; }
    public ConnectionState State { get; private set; } = ConnectionState.Connecting;
    public PeerId? RemoteId { get; private set; }
    public byte[]? RemotePublicKey { get; private set; }

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
            switch (State)
            {
                case ConnectionState.Connecting:
                    if (now - _startedAt > _options.ConnectTimeout.TotalMilliseconds) timeout = "timed out while punching";
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
        try
        {
            int p = 2;
            if (packet[p++] != ProtocolVersion) return;
            idKey = ReadBlock(packet, ref p);
            ephKey = ReadBlock(packet, ref p);
            sig = ReadBlock(packet, ref p);
            instance = ReadBlock(packet, ref p);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { return; }

        if (instance.Length != InstanceIdLength) return;
        if (!PeerIdentity.Verify(idKey, SignedHelloData(ephKey, instance), sig)) return;

        var remoteId = PeerId.FromPublicKey(idKey);
        if (remoteId == _local.Id) { post.Add(() => CloseCore(false, "is ourselves")); return; }
        if (_expected is { } want && want != remoteId) { post.Add(() => CloseCore(false, "presented an unexpected identity")); return; }
        if (State == ConnectionState.Established && RemoteId != remoteId) return;

        long now = Environment.TickCount64;
        _lastReceived = now;
        bool newSession = _remoteEph == null || !_remoteEph.AsSpan().SequenceEqual(ephKey);
        if (newSession)
        {
            _crypto?.Dispose();
            _crypto = DeriveSession(ephKey);
            _remoteEph = ephKey;
            _channels.Clear();
            RemoteId = remoteId;
            RemotePublicKey = idKey;
            RemoteInstance = instance;
            State = ConnectionState.Established;
            _lastSent = now;
            post.Add(() => { _established.TrySetResult(this); Connected?.Invoke(this); });
        }

        if (now - _lastHelloSent >= 100) SendHelloLocked(now);
    }

    private void OnDataLocked(ReadOnlySpan<byte> packet, List<Action> post)
    {
        if (State != ConnectionState.Established || _crypto == null) return;
        var frame = _crypto.Decrypt(packet, headerLength: 2);
        if (frame == null || frame.Length == 0) return;

        long now = Environment.TickCount64;
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
                if (!_channels.TryGetValue(ch, out var channel)) _channels[ch] = channel = new ReliableChannel();

                var delivered = new List<byte[]>();
                channel.OnSegment(seq, last, frame[7..], delivered);
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
        _send(_crypto.Encrypt(new[] { Magic, PacketData }, frame), RemoteEndPoint);
        _lastSent = Environment.TickCount64;
    }

    private void SendHelloLocked(long now)
    {
        var sig = _local.Sign(SignedHelloData(_ephPublic, _instance));
        using var ms = new MemoryStream();
        ms.WriteByte(Magic); ms.WriteByte(PacketHello); ms.WriteByte(ProtocolVersion);
        WriteBlock(ms, _local.PublicKey);
        WriteBlock(ms, _ephPublic);
        WriteBlock(ms, sig);
        WriteBlock(ms, _instance);
        _send(ms.ToArray(), RemoteEndPoint);
        _lastHelloSent = now;
    }

    private SessionCrypto DeriveSession(byte[] remoteEph)
    {
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(remoteEph, out _);
        var secret = _eph.DeriveRawSecretAgreement(theirs.PublicKey);

        bool low = _ephPublic.AsSpan().SequenceCompareTo(remoteEph) < 0;
        var salt = SHA256.HashData(low ? _ephPublic.Concat(remoteEph).ToArray() : remoteEph.Concat(_ephPublic).ToArray());
        SessionId = salt;
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
