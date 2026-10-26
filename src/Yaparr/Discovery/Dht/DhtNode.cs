using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Yaparr.Transport;

namespace Yaparr.Discovery.Dht;

public sealed class DhtOptions
{
    public IReadOnlyList<string> BootstrapHosts { get; init; } = new[]
    {
        "router.bittorrent.com:6881",
        "dht.transmissionbt.com:6881",
        "router.utorrent.com:6881",
        "dht.libtorrent.org:25401",
    };

    public bool ReadOnly { get; init; } = true;

    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int K { get; init; } = 8;

    public int Alpha { get; init; } = 4;
}

public sealed class DhtNode : IDisposable
{
    private enum State { New, Queried, Responded, Failed }

    private sealed class Candidate
    {
        public required DhtContact Contact;
        public State State;
        public byte[]? Token;
    }

    private sealed record Pending(IPEndPoint From, TaskCompletionSource<BDict> Completion);

    private readonly UdpMux _mux;
    private readonly DhtOptions _options;
    private readonly RoutingTable _table = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly SemaphoreSlim _bootstrapLock = new(1, 1);
    private int _nextTid;
    private bool _bootstrapped;

    private readonly object _storeGate = new();
    private readonly Dictionary<string, Dictionary<IPEndPoint, long>> _store = new();
    private static readonly long PeerTtlMs = (long)TimeSpan.FromMinutes(30).TotalMilliseconds;

    private byte[] _secret = RandomNumberGenerator.GetBytes(16);
    private byte[] _prevSecret = RandomNumberGenerator.GetBytes(16);
    private long _secretRotatedAt = Environment.TickCount64;

    public DhtNode(UdpMux mux, DhtOptions? options = null)
    {
        _mux = mux;
        _options = options ?? new DhtOptions();
        Id = DhtId.Random();
        _mux.AddHandler(HandlePacket);
    }

    public DhtId Id { get; }
    public int KnownNodes => _table.Count;

    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        await _bootstrapLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bootstrapped && _table.Count > 0) return;

            var seeds = new List<IPEndPoint>();
            foreach (var host in _options.BootstrapHosts)
            {
                var (name, port) = SplitHostPort(host);
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                    seeds.AddRange(addrs.Select(a => new IPEndPoint(a, port)));
                }
                catch (SocketException) {  }
            }

            var args = new Func<BDict>(() => new BDict { ["target"] = Id.Bytes });
            var responses = await Task.WhenAll(seeds.Select(s => QueryAsync(s, "find_node", args(), ct))).ConfigureAwait(false);
            foreach (var r in responses.Where(r => r != null)) AddCompactNodes(r!.Bytes("nodes"));

            if (_table.Count > 0)
            {
                await LookupAsync(Id, wantPeers: false, ct).ConfigureAwait(false);
                _bootstrapped = true;
            }
        }
        finally { _bootstrapLock.Release(); }
    }

    public async Task<IReadOnlyList<IPEndPoint>> GetPeersAsync(byte[] infoHash, CancellationToken ct = default)
    {
        await BootstrapAsync(ct).ConfigureAwait(false);
        var (peers, _) = await LookupAsync(new DhtId(infoHash), wantPeers: true, ct).ConfigureAwait(false);
        return peers;
    }

    public async Task<int> AnnounceAsync(byte[] infoHash, CancellationToken ct = default) =>
        (await AnnounceAndFindAsync(infoHash, ct).ConfigureAwait(false)).Accepted;

    public async Task<(int Accepted, IReadOnlyList<IPEndPoint> Peers)> AnnounceAndFindAsync(byte[] infoHash, CancellationToken ct = default)
    {
        await BootstrapAsync(ct).ConfigureAwait(false);
        var (peers, closest) = await LookupAsync(new DhtId(infoHash), wantPeers: true, ct).ConfigureAwait(false);

        var tasks = closest.Where(c => c.Token != null).Take(_options.K).Select(async c =>
        {
            var args = new BDict
            {
                ["info_hash"] = infoHash,
                ["port"] = (long)_mux.LocalPort,
                ["implied_port"] = 1L,
                ["token"] = c.Token!,
            };
            return await QueryAsync(c.Contact.EndPoint, "announce_peer", args, ct).ConfigureAwait(false) != null;
        });
        int accepted = (await Task.WhenAll(tasks).ConfigureAwait(false)).Count(ok => ok);
        return (accepted, peers);
    }

    private async Task<(List<IPEndPoint> Peers, List<Candidate> Closest)> LookupAsync(DhtId target, bool wantPeers, CancellationToken ct)
    {
        var candidates = new Dictionary<IPEndPoint, Candidate>();
        var peers = new HashSet<IPEndPoint>();

        void Offer(DhtContact c)
        {
            if (c.Id.Equals(Id)) return;
            candidates.TryAdd(c.EndPoint, new Candidate { Contact = c });
        }
        foreach (var c in _table.Closest(target, _options.K * 2)) Offer(c);

        Comparer<Candidate> byDistance = Comparer<Candidate>.Create((a, b) => DhtId.CompareDistance(target, a.Contact.Id, b.Contact.Id));

        for (int round = 0; round < 24; round++)
        {
            ct.ThrowIfCancellationRequested();
            var closest = candidates.Values.Where(c => c.State != State.Failed).Order(byDistance).Take(_options.K).ToList();
            var batch = closest.Where(c => c.State == State.New).Take(_options.Alpha).ToList();
            if (batch.Count == 0) break;

            await Task.WhenAll(batch.Select(async cand =>
            {
                cand.State = State.Queried;
                var args = new BDict();
                string q;
                if (wantPeers) { q = "get_peers"; args["info_hash"] = target.Bytes; }
                else { q = "find_node"; args["target"] = target.Bytes; }

                var r = await QueryAsync(cand.Contact.EndPoint, q, args, ct).ConfigureAwait(false);
                if (r == null) { cand.State = State.Failed; _table.Remove(cand.Contact.EndPoint); return; }

                cand.State = State.Responded;
                cand.Token = r.Bytes("token");
                lock (candidates)
                {
                    foreach (var n in ParseCompactNodes(r.Bytes("nodes"))) Offer(n);
                    if (r.List("values") is { } values)
                        foreach (var v in values.OfType<byte[]>())
                            if (ParseCompactPeer(v) is { } ep) peers.Add(ep);
                }
            })).ConfigureAwait(false);
        }

        var responded = candidates.Values.Where(c => c.State == State.Responded).Order(byDistance).Take(_options.K).ToList();
        return (peers.ToList(), responded);
    }

    private async Task<BDict?> QueryAsync(IPEndPoint to, string method, BDict args, CancellationToken ct)
    {
        var tid = BitConverter.GetBytes((ushort)Interlocked.Increment(ref _nextTid));
        var key = Convert.ToHexString(tid);
        var tcs = new TaskCompletionSource<BDict>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = new Pending(to, tcs);
        try
        {
            args["id"] = Id.Bytes;
            var msg = new BDict { ["t"] = tid, ["y"] = "q", ["q"] = method, ["a"] = args };
            if (_options.ReadOnly) msg["ro"] = 1L;
            _mux.Send(Bencode.Encode(msg), to);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.QueryTimeout);
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (InvalidOperationException) { return null; }
        finally { _pending.TryRemove(key, out _); }
    }

    private bool HandlePacket(ReadOnlyMemory<byte> packet, IPEndPoint from)
    {
        if (packet.Length == 0 || packet.Span[0] != (byte)'d') return false;

        BDict msg;
        try { msg = (BDict)Bencode.Decode(packet.Span); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException) { return true; }

        switch (msg.Str("y"))
        {
            case "r":
                if (msg.Bytes("t") is { } tid && _pending.TryGetValue(Convert.ToHexString(tid), out var p) && p.From.Equals(from)
                    && msg.Dict("r") is { } r)
                {
                    if (r.Bytes("id") is { Length: DhtId.Length } id) _table.Add(new DhtContact(new DhtId(id), from));
                    p.Completion.TrySetResult(r);
                }
                break;
            case "e":
                if (msg.Bytes("t") is { } etid && _pending.TryGetValue(Convert.ToHexString(etid), out var ep) && ep.From.Equals(from))
                    ep.Completion.TrySetException(new InvalidOperationException("KRPC error"));
                break;
            case "q":
                if (!_options.ReadOnly) HandleQuery(msg, from);
                break;
        }
        return true;
    }

    private void HandleQuery(BDict msg, IPEndPoint from)
    {
        var tid = msg.Bytes("t");
        var args = msg.Dict("a");
        if (tid == null || args?.Bytes("id") is not { Length: DhtId.Length } senderId) return;

        if (msg.Int("ro") != 1) _table.Add(new DhtContact(new DhtId(senderId), from));

        RotateSecretsIfDue();
        var reply = new BDict { ["id"] = Id.Bytes };
        switch (msg.Str("q"))
        {
            case "ping":
                break;
            case "find_node":
                if (args.Bytes("target") is not { Length: DhtId.Length } target) return;
                reply["nodes"] = CompactNodes(_table.Closest(new DhtId(target), _options.K));
                break;
            case "get_peers":
                if (args.Bytes("info_hash") is not { Length: DhtId.Length } hash) return;
                reply["token"] = MakeToken(from.Address, _secret);
                var values = StoredPeers(hash);
                if (values.Count > 0) reply["values"] = values;
                reply["nodes"] = CompactNodes(_table.Closest(new DhtId(hash), _options.K));
                break;
            case "announce_peer":
                if (args.Bytes("info_hash") is not { Length: DhtId.Length } ih || args.Bytes("token") is not { } token) return;
                if (!ValidToken(from.Address, token)) { SendError(tid, from, 203, "bad token"); return; }
                int port = args.Int("implied_port") == 1 ? from.Port : (int)(args.Int("port") ?? 0);
                if (port is <= 0 or > 65535) return;
                Store(ih, new IPEndPoint(from.Address, port));
                break;
            default:
                SendError(tid, from, 204, "method unknown");
                return;
        }
        _mux.Send(Bencode.Encode(new BDict { ["t"] = tid, ["y"] = "r", ["r"] = reply }), from);
    }

    private void SendError(byte[] tid, IPEndPoint to, int code, string text) =>
        _mux.Send(Bencode.Encode(new BDict { ["t"] = tid, ["y"] = "e", ["e"] = new List<object> { (long)code, text } }), to);

    private void Store(byte[] infoHash, IPEndPoint peer)
    {
        lock (_storeGate)
        {
            var key = Convert.ToHexString(infoHash);
            if (!_store.TryGetValue(key, out var set)) _store[key] = set = new();
            set[peer] = Environment.TickCount64 + PeerTtlMs;
        }
    }

    private List<object> StoredPeers(byte[] infoHash)
    {
        lock (_storeGate)
        {
            if (!_store.TryGetValue(Convert.ToHexString(infoHash), out var set)) return new();
            long now = Environment.TickCount64;
            foreach (var dead in set.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) set.Remove(dead);
            return set.Keys.Take(50).Select(ep => (object)CompactPeer(ep)).ToList();
        }
    }

    private void RotateSecretsIfDue()
    {
        if (Environment.TickCount64 - _secretRotatedAt <= 5 * 60_000) return;
        _prevSecret = _secret;
        _secret = RandomNumberGenerator.GetBytes(16);
        _secretRotatedAt = Environment.TickCount64;
    }

    private static byte[] MakeToken(IPAddress ip, byte[] secret) =>
        HMACSHA1.HashData(secret, ip.GetAddressBytes())[..8];

    private bool ValidToken(IPAddress ip, byte[] token) =>
        CryptographicOperations.FixedTimeEquals(token, MakeToken(ip, _secret)) ||
        CryptographicOperations.FixedTimeEquals(token, MakeToken(ip, _prevSecret));

    private static byte[] CompactPeer(IPEndPoint ep)
    {
        var b = new byte[6];
        ep.Address.GetAddressBytes().CopyTo(b, 0);
        b[4] = (byte)(ep.Port >> 8); b[5] = (byte)ep.Port;
        return b;
    }

    private static IPEndPoint? ParseCompactPeer(byte[] b)
    {
        if (b.Length != 6) return null;
        int port = (b[4] << 8) | b[5];
        return port == 0 ? null : new IPEndPoint(new IPAddress(b.AsSpan(0, 4)), port);
    }

    private static byte[] CompactNodes(IEnumerable<DhtContact> contacts)
    {
        var list = contacts.Where(c => c.EndPoint.AddressFamily == AddressFamily.InterNetwork).ToList();
        var b = new byte[list.Count * 26];
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Id.Bytes.CopyTo(b, i * 26);
            CompactPeer(list[i].EndPoint).CopyTo(b, i * 26 + 20);
        }
        return b;
    }

    private static IEnumerable<DhtContact> ParseCompactNodes(byte[]? b)
    {
        if (b == null) yield break;
        for (int i = 0; i + 26 <= b.Length; i += 26)
        {
            var ep = ParseCompactPeer(b[(i + 20)..(i + 26)]);
            if (ep != null) yield return new DhtContact(new DhtId(b[i..(i + 20)]), ep);
        }
    }

    private void AddCompactNodes(byte[]? b)
    {
        foreach (var c in ParseCompactNodes(b)) _table.Add(c);
    }

    private static (string Host, int Port) SplitHostPort(string hostPort)
    {
        int i = hostPort.LastIndexOf(':');
        return (hostPort[..i], int.Parse(hostPort[(i + 1)..]));
    }

    public void Dispose()
    {
        foreach (var p in _pending.Values) p.Completion.TrySetCanceled();
    }
}
