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

    /// <summary>
    /// Used when none of the bootstrap host names can be resolved (DNS down or blocked). Addresses can go stale,
    /// so names are always tried first.
    /// </summary>
    public IReadOnlyList<string> FallbackSeeds { get; init; } = new[]
    {
        "67.215.246.10:6881",
        "87.98.162.88:6881",
        "212.129.33.59:6881",
        "82.221.103.244:6881",
        "185.157.221.247:25401",
    };

    /// <summary>File where nodes that answered are remembered, so a restart can bootstrap without DNS. Null disables it.</summary>
    public string? NodeCachePath { get; init; }

    public bool ReadOnly { get; init; } = true;

    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int K { get; init; } = 8;

    public int Alpha { get; init; } = 4;
}

public sealed class DhtNode : IDisposable
{
    private enum State { New, Queried, Responded, Failed }

    private enum LookupKind { FindNode, GetPeers, Get }

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

    private sealed record StoredItem(byte[] Key, byte[] Salt, long Seq, byte[] Value, byte[] Signature, long ExpiresAt);
    private readonly Dictionary<string, StoredItem> _items = new();
    private static readonly long ItemTtlMs = (long)TimeSpan.FromHours(2).TotalMilliseconds;
    private const int MaxStoredItems = 2048;

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

    /// <summary>How many nodes the latest lookup asked and how many of them answered.</summary>
    public (int Queried, int Answered) LastLookup { get; private set; }

    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        await _bootstrapLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bootstrapped && _table.Count > 0) return;

            var seeds = new List<IPEndPoint>();
            var unresolved = new List<string>();
            foreach (var host in _options.BootstrapHosts)
            {
                var (name, port) = SplitHostPort(host);
                try
                {
                    var addrs = await ResolveAsync(name, ct).ConfigureAwait(false);
                    if (addrs.Count == 0) unresolved.Add(name);
                    seeds.AddRange(addrs.Select(a => new IPEndPoint(a, port)));
                }
                catch (SocketException ex) { unresolved.Add($"{name} ({ex.SocketErrorCode})"); }
            }
            if (unresolved.Count > 0) _mux.Log?.Invoke($"DHT: could not look up {string.Join(", ", unresolved)}");

            // Nodes that answered last time, and as a last resort the built-in addresses.
            foreach (var cached in LoadCachedNodes()) seeds.Add(cached);
            if (seeds.Count == 0 && _options.BootstrapHosts.Count > 0 && unresolved.Count == _options.BootstrapHosts.Count)
            {
                _mux.Log?.Invoke("DHT: using built-in addresses for the bootstrap nodes");
                foreach (var fallback in _options.FallbackSeeds)
                    if (IPEndPoint.TryParse(fallback, out var ep)) seeds.Add(ep);
            }
            seeds = seeds.Distinct().ToList();

            var args = new Func<BDict>(() => new BDict { ["target"] = Id.Bytes });
            var responses = await Task.WhenAll(seeds.Select(s => QueryAsync(s, "find_node", args(), ct))).ConfigureAwait(false);
            foreach (var r in responses.Where(r => r != null)) AddCompactNodes(r!.Bytes("nodes"));

            if (_table.Count > 0)
            {
                await LookupAsync(Id, LookupKind.FindNode, ct).ConfigureAwait(false);
                _bootstrapped = true;
            }
            else _mux.Log?.Invoke($"DHT: none of {seeds.Count} bootstrap node(s) answered; UDP may be blocked");
        }
        finally { _bootstrapLock.Release(); }
    }

    private static async Task<List<IPAddress>> ResolveAsync(string name, CancellationToken ct)
    {
        if (IPAddress.TryParse(name, out var literal)) return new List<IPAddress> { literal };
        try
        {
            return (await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, ct).ConfigureAwait(false)).ToList();
        }
        catch (SocketException)
        {
            // Some resolvers only answer the general query.
            return (await Dns.GetHostAddressesAsync(name, ct).ConfigureAwait(false))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        }
    }

    private long _nodesSavedAt;

    private IEnumerable<IPEndPoint> LoadCachedNodes()
    {
        if (_options.NodeCachePath is not { } path || !File.Exists(path)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines.Take(64))
            if (IPEndPoint.TryParse(line.Trim(), out var ep)) yield return ep;
    }

    private void SaveCachedNodes()
    {
        if (_options.NodeCachePath is not { } path || Environment.TickCount64 - _nodesSavedAt < 60_000) return;
        _nodesSavedAt = Environment.TickCount64;
        var nodes = _table.Recent(64);
        if (nodes.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllLines(path, nodes.Select(n => n.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Looks up a BEP 44 mutable item and returns the newest one whose signature is valid for this key and salt.
    /// </summary>
    public async Task<FoundItem?> GetMutableAsync(byte[] publicKey, byte[] salt, CancellationToken ct = default)
    {
        await BootstrapAsync(ct).ConfigureAwait(false);
        FoundItem? best = null;
        int valid = 0;
        var gate = new object();
        // Stop at the first valid value: an item is stored on several nodes, and waiting for
        // every one of them would take most of a minute on the real network.
        await LookupAsync(new DhtId(MutableItem.Target(publicKey, salt)), LookupKind.Get, ct, r =>
        {
            if (r.Bytes("v") is not { } v || r.Bytes("k") is not { } k || r.Bytes("sig") is not { } sig || r.Int("seq") is not { } seq) return;
            if (!k.AsSpan().SequenceEqual(publicKey) || !MutableItem.Verify(publicKey, salt, seq, v, sig)) return;
            lock (gate)
            {
                valid++;
                if (best == null || seq > best.Seq) best = new FoundItem(v, seq, sig);
            }
        }, enough: () => { lock (gate) return valid >= 1; }).ConfigureAwait(false);
        return best;
    }

    /// <summary>
    /// Stores a BEP 44 mutable item on the nodes closest to its key. Returns how many nodes accepted it.
    /// A node keeps only the highest sequence number, so <paramref name="seq"/> must grow with every update.
    /// </summary>
    public async Task<int> PutMutableAsync(byte[] seed, byte[] publicKey, byte[] salt, long seq, byte[] value, CancellationToken ct = default)
    {
        if (value.Length > MutableItem.MaxValueLength || salt.Length > MutableItem.MaxSaltLength)
            throw new ArgumentException("Value or salt too large for a mutable item.");
        await BootstrapAsync(ct).ConfigureAwait(false);
        var (_, closest) = await LookupAsync(new DhtId(MutableItem.Target(publicKey, salt)), LookupKind.Get, ct).ConfigureAwait(false);

        var sig = MutableItem.Sign(seed, publicKey, salt, seq, value);
        var results = await Task.WhenAll(closest.Where(c => c.Token != null).Take(_options.K).Select(async c =>
        {
            var args = new BDict { ["k"] = publicKey, ["seq"] = seq, ["sig"] = sig, ["token"] = c.Token!, ["v"] = value };
            if (salt.Length > 0) args["salt"] = salt;
            return await QueryAsync(c.Contact.EndPoint, "put", args, ct).ConfigureAwait(false) != null;
        })).ConfigureAwait(false);
        SaveCachedNodes();
        return results.Count(ok => ok);
    }

    public async Task<IReadOnlyList<IPEndPoint>> GetPeersAsync(byte[] infoHash, CancellationToken ct = default)
    {
        await BootstrapAsync(ct).ConfigureAwait(false);
        var (peers, _) = await LookupAsync(new DhtId(infoHash), LookupKind.GetPeers, ct).ConfigureAwait(false);
        return peers;
    }

    public async Task<int> AnnounceAsync(byte[] infoHash, CancellationToken ct = default) =>
        (await AnnounceAndFindAsync(infoHash, ct).ConfigureAwait(false)).Accepted;

    public async Task<(int Accepted, IReadOnlyList<IPEndPoint> Peers)> AnnounceAndFindAsync(byte[] infoHash, CancellationToken ct = default)
    {
        await BootstrapAsync(ct).ConfigureAwait(false);
        var (peers, closest) = await LookupAsync(new DhtId(infoHash), LookupKind.GetPeers, ct).ConfigureAwait(false);

        var tasks = closest.Where(c => c.Token != null).Take(_options.K).Select(async c =>
        {
            var args = new BDict
            {
                ["info_hash"] = infoHash,
                ["port"] = (long)(_mux.AdvertisedPort ?? _mux.LocalPort),
                ["implied_port"] = _mux.AdvertisedPort == null ? 1L : 0L,
                ["token"] = c.Token!,
            };
            return await QueryAsync(c.Contact.EndPoint, "announce_peer", args, ct).ConfigureAwait(false) != null;
        });
        int accepted = (await Task.WhenAll(tasks).ConfigureAwait(false)).Count(ok => ok);
        accepted += await AnnounceViaRelayAsync(infoHash, closest, ct).ConfigureAwait(false);
        SaveCachedNodes();
        return (accepted, peers);
    }

    private async Task<int> AnnounceViaRelayAsync(byte[] infoHash, List<Candidate> closest, CancellationToken ct)
    {
        if (_mux.Route is not TurnClient { IsActive: true } turn) return 0;
        var results = await Task.WhenAll(closest.Take(_options.K).Select(async c =>
        {
            var node = c.Contact.EndPoint;
            if (!await turn.EnsurePermissionAsync(node.Address, ct).ConfigureAwait(false)) return false;
            var probe = await QueryAsync(node, "get_peers", new BDict { ["info_hash"] = infoHash }, ct, viaRelay: true).ConfigureAwait(false);
            if (probe?.Bytes("token") is not { } token) return false;
            var args = new BDict { ["info_hash"] = infoHash, ["port"] = 0L, ["implied_port"] = 1L, ["token"] = token };
            return await QueryAsync(node, "announce_peer", args, ct, viaRelay: true).ConfigureAwait(false) != null;
        })).ConfigureAwait(false);
        return results.Count(ok => ok);
    }

    private async Task<(List<IPEndPoint> Peers, List<Candidate> Closest)> LookupAsync(DhtId target, LookupKind kind, CancellationToken ct, Action<BDict>? onReply = null, Func<bool>? enough = null)
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

        // Many nodes do not know BEP 44 and answer "get" with an error, so such a lookup has to ask more nodes at once.
        int width = kind == LookupKind.Get ? _options.Alpha * 2 : _options.Alpha;
        int maxRounds = kind == LookupKind.Get ? 12 : 24;
        for (int round = 0; round < maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            if (enough?.Invoke() == true) break;
            var closest = candidates.Values.Where(c => c.State != State.Failed).Order(byDistance).Take(_options.K).ToList();
            var batch = closest.Where(c => c.State == State.New).Take(width).ToList();
            if (batch.Count == 0) break;

            await Task.WhenAll(batch.Select(async cand =>
            {
                cand.State = State.Queried;
                var args = new BDict();
                string q;
                switch (kind)
                {
                    case LookupKind.GetPeers: q = "get_peers"; args["info_hash"] = target.Bytes; break;
                    case LookupKind.Get: q = "get"; args["target"] = target.Bytes; break;
                    default: q = "find_node"; args["target"] = target.Bytes; break;
                }

                var r = await QueryAsync(cand.Contact.EndPoint, q, args, ct).ConfigureAwait(false);
                if (r == null)
                {
                    cand.State = State.Failed;
                    // A node that does not know BEP 44 answers "get" with an error; that does not make it a dead node.
                    if (kind != LookupKind.Get) _table.Remove(cand.Contact.EndPoint);
                    return;
                }

                cand.State = State.Responded;
                cand.Token = r.Bytes("token");
                onReply?.Invoke(r);
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
        int queried = candidates.Values.Count(c => c.State != State.New);
        int answered = candidates.Values.Count(c => c.State == State.Responded);
        LastLookup = (queried, answered);
        if (kind == LookupKind.GetPeers && peers.Count == 0)
            _mux.Log?.Invoke(answered == 0
                ? $"DHT lookup: none of {queried} node(s) answered ({_table.Count} known); UDP replies may be blocked or not coming back"
                : $"DHT lookup: {answered} of {queried} node(s) answered, but none knew any peers for this room");
        return (peers.ToList(), responded);
    }

    private async Task<BDict?> QueryAsync(IPEndPoint to, string method, BDict args, CancellationToken ct, bool viaRelay = false)
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
            if (viaRelay) _mux.SendRelayed(Bencode.Encode(msg), to);
            else _mux.Send(Bencode.Encode(msg), to);

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
            case "get":
                if (args.Bytes("target") is not { Length: DhtId.Length } getTarget) return;
                reply["token"] = MakeToken(from.Address, _secret);
                reply["nodes"] = CompactNodes(_table.Closest(new DhtId(getTarget), _options.K));
                if (FindItem(getTarget) is { } found)
                {
                    reply["seq"] = found.Seq;
                    // Only send the value if the asker does not already have this version or a newer one.
                    if (args.Int("seq") is not { } have || found.Seq > have)
                    {
                        reply["v"] = found.Value;
                        reply["k"] = found.Key;
                        reply["sig"] = found.Signature;
                    }
                }
                break;
            case "put":
                if (!HandlePut(args, from, tid)) return;
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

    private StoredItem? FindItem(byte[] target)
    {
        lock (_storeGate)
        {
            var key = Convert.ToHexString(target);
            if (!_items.TryGetValue(key, out var item)) return null;
            if (item.ExpiresAt >= Environment.TickCount64) return item;
            _items.Remove(key);
            return null;
        }
    }

    // BEP 44 put. Errors use the codes from the specification.
    private bool HandlePut(BDict args, IPEndPoint from, byte[] tid)
    {
        if (args.Bytes("token") is not { } token || args.Bytes("v") is not { } v || args.Bytes("k") is not { } k
            || args.Bytes("sig") is not { } sig || args.Int("seq") is not { } seq) return false;
        var salt = args.Bytes("salt") ?? Array.Empty<byte>();

        if (!ValidToken(from.Address, token)) { SendError(tid, from, 203, "bad token"); return false; }
        if (v.Length > MutableItem.MaxValueLength) { SendError(tid, from, 205, "message too big"); return false; }
        if (salt.Length > MutableItem.MaxSaltLength) { SendError(tid, from, 207, "salt too big"); return false; }
        if (!MutableItem.Verify(k, salt, seq, v, sig)) { SendError(tid, from, 206, "invalid signature"); return false; }

        var target = MutableItem.Target(k, salt);
        lock (_storeGate)
        {
            var key = Convert.ToHexString(target);
            long now = Environment.TickCount64;
            if (_items.TryGetValue(key, out var existing) && existing.ExpiresAt >= now)
            {
                if (args.Int("cas") is { } cas && cas != existing.Seq) { SendError(tid, from, 301, "CAS mismatch"); return false; }
                if (seq < existing.Seq || (seq == existing.Seq && !existing.Value.AsSpan().SequenceEqual(v)))
                {
                    SendError(tid, from, 302, "sequence number less than current");
                    return false;
                }
            }
            else if (_items.Count >= MaxStoredItems)
            {
                foreach (var dead in _items.Where(kv => kv.Value.ExpiresAt < now).Select(kv => kv.Key).ToList()) _items.Remove(dead);
                if (_items.Count >= MaxStoredItems) { SendError(tid, from, 202, "storage full"); return false; }
            }
            _items[key] = new StoredItem(k, salt, seq, v, sig, now + ItemTtlMs);
        }
        return true;
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
