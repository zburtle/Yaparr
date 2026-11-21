using System.Diagnostics;
using System.Threading.Channels;
using Yaparr.Identity;

namespace Yaparr.Chat.Files;

/// <summary>
/// Answers requests for files this device has all or part of: what it has, the piece hashes and the pieces (see <c>docs/file-sharing.md</c>). It serves only a few downloaders at a time
/// (<c>slots</c>); one past that is told it is busy, with members known to have pieces, so that a file spreads through a room instead of everyone asking the sender. What it sends is
/// paced (<c>bytesPerSecond</c>, 0 for no limit) and waits while a connection has too much it has not yet sent, so a big file never piles up in memory.
/// </summary>
internal sealed class FileServer : IDisposable
{
    private static readonly TimeSpan SlotIdle = TimeSpan.FromSeconds(3);
    private const long MaxBacklog = 256 * 1024;

    private readonly record struct Job(string Root, int Piece, int FirstBlock, int Blocks);

    private sealed class Queue
    {
        public required IFilePeer Peer;
        public required Channel<Job> Jobs;
        public readonly HashSet<(string Root, int Piece)> Cancelled = new();
    }

    private readonly Func<string, IPieceStore?> _find;
    private readonly Func<PeerId, string, bool> _mayServe;
    private readonly Func<int> _slots;
    private readonly Func<long> _bytesPerSecond;
    private readonly Func<long> _clock;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<PeerId, Queue> _queues = new();
    private readonly Dictionary<PeerId, long> _active = new();
    private readonly Dictionary<string, Dictionary<PeerId, PieceMap>> _holders = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clockWatch = Stopwatch.StartNew();
    private double _tokens;
    private long _tokensAt;

    public FileServer(Func<string, IPieceStore?> find, Func<PeerId, string, bool> mayServe, Func<int> slots, Func<long> bytesPerSecond, Func<long> clock, Action<string>? log = null)
    {
        _find = find;
        _mayServe = mayServe;
        _slots = slots;
        _bytesPerSecond = bytesPerSecond;
        _clock = clock;
        _log = log;
    }

    /// <summary>The members known to have pieces of a file (told to us in their <c>Have</c>), best supplied first, other than <paramref name="except"/>.</summary>
    public IReadOnlyList<PeerId> HoldersOf(string root, PeerId except)
    {
        lock (_gate)
            return _holders.TryGetValue(root, out var map)
                ? map.Where(h => h.Key != except && h.Value.Count > 0).OrderByDescending(h => h.Value.Count).Select(h => h.Key).Take(FileWire.MaxReferrals).ToList()
                : Array.Empty<PeerId>();
    }

    public void OnFrame(IFilePeer peer, byte[] frame)
    {
        if (!FileWire.TryRoot(frame, out var root)) return;
        switch (frame[0])
        {
            case FileWire.Interest: OnInterest(peer, root); break;
            case FileWire.Have: OnHave(peer, root, frame); break;
            case FileWire.HaveMore when frame.Length == FileWire.Header + 4: OnHaveMore(peer, root, (int)FileWire.U32(frame, FileWire.Header)); break;
            case FileWire.HashesWant when frame.Length == FileWire.Header + 6: OnHashesWant(peer, root, (int)FileWire.U32(frame, FileWire.Header), FileWire.U16(frame, FileWire.Header + 4)); break;
            case FileWire.Want when frame.Length == FileWire.Header + 8:
                OnWant(peer, root, (int)FileWire.U32(frame, FileWire.Header), FileWire.U16(frame, FileWire.Header + 4), FileWire.U16(frame, FileWire.Header + 6));
                break;
            case FileWire.Cancel when frame.Length == FileWire.Header + 4: OnCancel(peer, root, (int)FileWire.U32(frame, FileWire.Header)); break;
        }
    }

    public void PeerGone(PeerId id)
    {
        lock (_gate)
        {
            if (_queues.Remove(id, out var q)) q.Jobs.Writer.TryComplete();
            _active.Remove(id);
            foreach (var map in _holders.Values) map.Remove(id);
        }
    }

    /// <summary>Forgets everything told about a file (it was finished or dropped).</summary>
    public void Forget(string root)
    {
        lock (_gate) _holders.Remove(root);
    }

    private IPieceStore? Allowed(IFilePeer peer, string root)
    {
        var store = _find(root);
        if (store == null || !_mayServe(peer.Id, root))
        {
            Send(peer, FileWire.Simple(FileWire.Gone, root));
            return null;
        }
        return store;
    }

    private void OnInterest(IFilePeer peer, string root)
    {
        if (Allowed(peer, root) is not { } store) return;
        Send(peer, FileWire.HaveBits(root, store.Have));
    }

    private void OnHave(IFilePeer peer, string root, byte[] frame)
    {
        if (_find(root) is not { } store || !_mayServe(peer.Id, root)) return;
        if (!PieceMap.TryFromBytes(store.Offer.PieceCount, frame.AsSpan(FileWire.Header), out var map)) return;
        lock (_gate)
        {
            if (!_holders.TryGetValue(root, out var holders)) _holders[root] = holders = new();
            if (holders.Count < 1024 || holders.ContainsKey(peer.Id)) holders[peer.Id] = map;
        }
    }

    private void OnHaveMore(IFilePeer peer, string root, int piece)
    {
        lock (_gate)
            if (_holders.TryGetValue(root, out var holders) && holders.TryGetValue(peer.Id, out var map)) map.Set(piece);
    }

    private void OnHashesWant(IFilePeer peer, string root, int first, int count)
    {
        if (Allowed(peer, root) is not { } store) return;
        if (store.Hashes is not { } hashes) { Send(peer, FileWire.Simple(FileWire.Gone, root)); return; }
        if (first < 0 || first >= store.Offer.PieceCount) return;
        count = Math.Min(Math.Min(count, FileWire.MaxHashesPerFrame), store.Offer.PieceCount - first);
        if (count <= 0) return;
        Send(peer, FileWire.HashesBlock(root, first, hashes.AsSpan(first * PieceHashes.HashSize, count * PieceHashes.HashSize)));
    }

    private void OnWant(IFilePeer peer, string root, int piece, int firstBlock, int blocks)
    {
        if (Allowed(peer, root) is not { } store) return;
        var offer = store.Offer;
        if (piece < 0 || piece >= offer.PieceCount || firstBlock < 0 || blocks <= 0 || firstBlock + blocks > offer.BlockCount(piece)) return;
        if (!store.Have[piece]) { Send(peer, FileWire.WithPiece(FileWire.NotHave, root, piece)); return; }

        Queue queue;
        lock (_gate)
        {
            long now = _clock();
            foreach (var idle in _active.Where(a => now - a.Value > SlotIdle.TotalMilliseconds).Select(a => a.Key).ToList()) _active.Remove(idle);
            if (!_active.ContainsKey(peer.Id) && _active.Count >= Math.Max(1, _slots()))
            {
                var referrals = HoldersOfLocked(root, peer.Id);
                Send(peer, FileWire.BusyWith(root, referrals));
                return;
            }
            _active[peer.Id] = now;
            if (!_queues.TryGetValue(peer.Id, out queue!) || !ReferenceEquals(queue.Peer, peer))
            {
                queue = new Queue { Peer = peer, Jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true }) };
                _queues[peer.Id] = queue;
                _ = Task.Run(() => ServeAsync(queue));
            }
            queue.Cancelled.Remove((root, piece));
        }
        queue.Jobs.Writer.TryWrite(new Job(root, piece, firstBlock, blocks));
    }

    private IReadOnlyList<PeerId> HoldersOfLocked(string root, PeerId except) =>
        _holders.TryGetValue(root, out var map)
            ? map.Where(h => h.Key != except && h.Value.Count > 0).OrderByDescending(h => h.Value.Count).Select(h => h.Key).Take(FileWire.MaxReferrals).ToList()
            : Array.Empty<PeerId>();

    private void OnCancel(IFilePeer peer, string root, int piece)
    {
        lock (_gate)
            if (_queues.TryGetValue(peer.Id, out var q)) q.Cancelled.Add((root, piece));
    }

    private async Task ServeAsync(Queue queue)
    {
        var peer = queue.Peer;
        try
        {
            await foreach (var job in queue.Jobs.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (IsCancelled(queue, job)) continue;
                var store = _find(job.Root);
                var data = store?.ReadPiece(job.Piece);
                if (store == null || data == null)
                {
                    store?.Have.Clear(job.Piece);
                    Send(peer, FileWire.WithPiece(FileWire.NotHave, job.Root, job.Piece));
                    continue;
                }
                for (int block = job.FirstBlock; block < job.FirstBlock + job.Blocks; block++)
                {
                    if (IsCancelled(queue, job)) break;
                    int at = block * FileOffer.BlockSize;
                    int length = Math.Min(FileOffer.BlockSize, data.Length - at);
                    if (length <= 0) break;
                    while (peer.Backlog > MaxBacklog) await Task.Delay(5, _cts.Token).ConfigureAwait(false);
                    await WaitForRateAsync(length).ConfigureAwait(false);
                    if (!Send(peer, FileWire.BlockOf(job.Root, job.Piece, block, data.AsSpan(at, length)))) return;
                    lock (_gate) _active[peer.Id] = _clock();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Invoke($"serving a file to {peer.Id.Short} failed: {ex.Message}");
        }
    }

    private bool IsCancelled(Queue queue, Job job)
    {
        lock (_gate) return queue.Cancelled.Remove((job.Root, job.Piece));
    }

    private async Task WaitForRateAsync(int bytes)
    {
        long rate = _bytesPerSecond();
        if (rate <= 0) return;
        while (true)
        {
            long wait;
            lock (_gate)
            {
                long now = _clockWatch.ElapsedMilliseconds;
                _tokens = Math.Min(rate, _tokens + (now - _tokensAt) * rate / 1000.0);
                _tokensAt = now;
                if (_tokens >= bytes || _tokens >= rate) { _tokens -= bytes; return; }
                wait = (long)Math.Ceiling((bytes - _tokens) * 1000.0 / rate);
            }
            await Task.Delay((int)Math.Clamp(wait, 1, 200), _cts.Token).ConfigureAwait(false);
        }
    }

    private bool Send(IFilePeer peer, byte[] frame)
    {
        try { peer.Send(frame); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate)
            foreach (var q in _queues.Values) q.Jobs.Writer.TryComplete();
    }
}
