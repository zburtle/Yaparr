using Yaparr.Identity;

namespace Yaparr.Chat.Files;

/// <summary>Someone a file can be asked of, over one connection. A test can stand in for it.</summary>
internal interface IFilePeer
{
    PeerId Id { get; }

    /// <summary>The connection runs through the shared TURN server, whose bandwidth is limited.</summary>
    bool Metered { get; }

    /// <summary>The device can pass things on for others (<see cref="ChatNode.IsRelayCapable"/>); of two holders that are otherwise equal, it is asked first.</summary>
    bool CanRelay { get; }

    /// <summary>Bytes handed to the connection that it has not yet sent (and had confirmed), so a sender can wait instead of piling up.</summary>
    long Backlog { get; }

    /// <summary>Sends one frame. Throws <see cref="InvalidOperationException"/> when the connection is gone.</summary>
    void Send(byte[] frame);
}

internal sealed class FileDownloadOptions
{
    /// <summary>Pieces asked of one holder at the same time.</summary>
    public int Pipeline { get; init; } = 4;

    /// <summary>A request that has had no block for this long is given up and asked of someone else.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a holder that said it was busy (or stalled) is left alone.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a holder whose pieces are not known is asked again.</summary>
    public TimeSpan InterestRefresh { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A holder that sends this many pieces that do not match is not asked again for this file.</summary>
    public int MaxStrikes { get; init; } = 3;

    /// <summary>Whether holders reached through the shared server are used.</summary>
    public bool AllowMetered { get; init; }

    /// <summary>The most half-received pieces kept to be carried on from where they stopped.</summary>
    public int MaxPartialPieces { get; init; } = 64;
}

public enum FileState
{
    /// <summary>Nobody who has it is connected (or no one has what is still missing). It carries on when someone is.</summary>
    Waiting,
    Downloading,
    Paused,
    Completed,
    Canceled,
    /// <summary>The disk would not take it (full, or the folder is gone).</summary>
    Failed,
}

public sealed record FileProgress(string Root, FileState State, long BytesDone, long Size, int Holders, long BytesPerSecond);

/// <summary>
/// Downloads one file (see <c>docs/file-sharing.md</c>): asks every holder that is connected what it has, fetches the piece hashes and checks them against the root, then asks
/// holders for pieces, a few at a time each, the rarest first, and writes each verified piece into the <see cref="PartialFile"/>. A request that stalls, a holder that goes away
/// or says it is busy, and a piece that does not match all end the same way: the piece is asked for again, from someone else if there is someone, keeping the blocks that did arrive.
/// </summary>
internal sealed class FileDownload : IDisposable
{
    private sealed class Holder
    {
        public required IFilePeer Peer;
        public PieceMap? Has;
        public int Strikes;
        public long CooldownUntil;
        public long InterestAt;
        public long GoneUntil;
        public double Speed;          // bytes a second, smoothed
        public long SpeedAt;
        public long WindowBytes;
        public bool Banned;
        public int InFlight;
    }

    private sealed class Request
    {
        public required PeerId Holder;
        public required byte[] Buffer;
        public int NextBlock;
        public long StartedAt;
        public long LastBlockAt;
    }

    private readonly object _gate = new();
    private readonly PartialFile _file;
    private readonly Func<long> _clock;
    private readonly FileDownloadOptions _options;
    private readonly Dictionary<PeerId, Holder> _holders = new();
    private readonly Dictionary<int, Request> _requests = new();
    private readonly Dictionary<int, (byte[] Buffer, int Blocks)> _partials = new();
    private readonly HashSet<int> _verifying = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileState _state = FileState.Waiting;
    private bool _paused;
    private int _cursor;

    // The piece hashes being fetched, when the partial file does not have them yet.
    private byte[]? _hashBuffer;
    private int _hashesHave;
    private PeerId? _hashesFrom;
    private long _hashesAskedAt;
    private readonly HashSet<PeerId> _hashContributors = new();

    public FileDownload(PartialFile file, Func<long> clock, FileDownloadOptions? options = null)
    {
        _file = file;
        _clock = clock;
        _options = options ?? new FileDownloadOptions();
        _cursor = Random.Shared.Next(Math.Max(1, file.Offer.PieceCount));
        if (file.Have.IsComplete)
        {
            _state = FileState.Completed;
            _done.TrySetResult();
        }
    }

    public FileOffer Offer => _file.Offer;
    public PartialFile File => _file;

    /// <summary>Ends when the last piece is in.</summary>
    public Task Completion => _done.Task;

    public FileState State { get { lock (_gate) return _state; } }

    /// <summary>Raised (outside the lock) when progress or the state changes, at most a few times a second while blocks arrive.</summary>
    public event Action<FileProgress>? Changed;

    /// <summary>Raised with members that a holder said have pieces and might have room (the answer to Busy); the node can ask them too.</summary>
    public event Action<IReadOnlyList<PeerId>>? Referred;

    /// <summary>Raised once, when the last piece is in and the file is whole.</summary>
    public event Action? Finished;

    private long _lastReport;

    // ---- the node tells us who is there

    public void AddPeer(IFilePeer peer)
    {
        lock (_gate)
        {
            if (_state is FileState.Completed or FileState.Canceled or FileState.Failed) return;
            if (!_holders.TryGetValue(peer.Id, out var holder)) _holders[peer.Id] = holder = new Holder { Peer = peer };
            else holder.Peer = peer;
            holder.Has = null;
            ReleaseRequestsOf(peer.Id);
            AskInterest(holder);
            Schedule();
        }
        Report(force: true);
    }

    public void RemovePeer(PeerId id)
    {
        lock (_gate)
        {
            _holders.Remove(id);
            ReleaseRequestsOf(id);
            if (_hashesFrom == id) _hashesFrom = null;
            Schedule();
        }
        Report(force: true);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state is FileState.Completed or FileState.Canceled or FileState.Failed) return;
            _paused = true;
            _state = FileState.Paused;
            foreach (var piece in _requests.Keys.ToList()) CancelRequest(piece, tellHolder: true);
        }
        Report(force: true);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            _state = FileState.Waiting;
            foreach (var h in _holders.Values) { h.Has = null; AskInterest(h); }
            Schedule();
        }
        Report(force: true);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_state is FileState.Completed or FileState.Canceled) return;
            _state = FileState.Canceled;
            foreach (var piece in _requests.Keys.ToList()) CancelRequest(piece, tellHolder: true);
            _partials.Clear();
        }
        _done.TrySetCanceled();
        Report(force: true);
    }

    public void Dispose() => Cancel();

    // ---- time: stalls, cool-downs, asking again

    public void Tick()
    {
        lock (_gate)
        {
            if (_state is FileState.Completed or FileState.Canceled or FileState.Failed or FileState.Paused) return;
            long now = _clock();
            foreach (var (piece, request) in _requests.ToList())
            {
                if (now - request.LastBlockAt < _options.StallTimeout.TotalMilliseconds) continue;
                if (_holders.TryGetValue(request.Holder, out var slow)) slow.CooldownUntil = now + (long)_options.Cooldown.TotalMilliseconds;
                CancelRequest(piece, tellHolder: true);
            }
            if (_hashesFrom != null && now - _hashesAskedAt >= _options.StallTimeout.TotalMilliseconds)
            {
                if (_holders.TryGetValue(_hashesFrom.Value, out var slow)) slow.CooldownUntil = now + (long)_options.Cooldown.TotalMilliseconds;
                _hashesFrom = null;
            }
            foreach (var h in _holders.Values)
                if (h.Has == null && !h.Banned && now >= h.GoneUntil && now - h.InterestAt >= _options.InterestRefresh.TotalMilliseconds) AskInterest(h);
            Schedule();
        }
        Report();
    }

    // ---- what holders say

    public void OnFrame(IFilePeer from, ReadOnlySpan<byte> frame)
    {
        if (frame.Length < FileWire.Header) return;
        bool finished = false;
        (int Piece, byte[] Data)? verify = null;
        IReadOnlyList<PeerId>? referred = null;
        lock (_gate)
        {
            if (_state is FileState.Completed or FileState.Canceled or FileState.Failed) return;
            if (!_holders.TryGetValue(from.Id, out var holder) || holder.Banned) return;
            switch (frame[0])
            {
                case FileWire.Have:
                    if (PieceMap.TryFromBytes(Offer.PieceCount, frame[FileWire.Header..], out var map)) { holder.Has = map; holder.GoneUntil = 0; }
                    break;
                case FileWire.HaveMore when frame.Length == FileWire.Header + 4:
                    holder.Has?.Set((int)FileWire.U32(frame, FileWire.Header));
                    break;
                case FileWire.Gone:
                    holder.Has = null;
                    holder.GoneUntil = _clock() + 60_000;
                    ReleaseRequestsOf(from.Id);
                    break;
                case FileWire.NotHave when frame.Length == FileWire.Header + 4:
                    int missing = (int)FileWire.U32(frame, FileWire.Header);
                    holder.Has?.Clear(missing);
                    if (_requests.TryGetValue(missing, out var wasAsked) && wasAsked.Holder == from.Id) CancelRequest(missing, tellHolder: false);
                    break;
                case FileWire.Busy:
                    holder.CooldownUntil = _clock() + (long)_options.Cooldown.TotalMilliseconds;
                    ReleaseRequestsOf(from.Id);
                    referred = ReadReferrals(frame, from.Id);
                    break;
                case FileWire.HashesData:
                    OnHashes(from.Id, frame);
                    break;
                case FileWire.Block:
                    verify = OnBlock(holder, frame);
                    break;
            }
            Schedule();
        }
        if (referred is { Count: > 0 }) Referred?.Invoke(referred);
        if (verify is { } v) finished = FinishPiece(from.Id, v.Piece, v.Data);
        Report();
        if (finished) Finished?.Invoke();
    }

    private IReadOnlyList<PeerId> ReadReferrals(ReadOnlySpan<byte> frame, PeerId except)
    {
        var list = new List<PeerId>();
        if (frame.Length < FileWire.Header + 1) return list;
        int n = Math.Min((int)frame[FileWire.Header], FileWire.MaxReferrals);
        for (int i = 0; i < n && frame.Length >= FileWire.Header + 1 + (i + 1) * 32; i++)
        {
            var id = new PeerId(Convert.ToHexStringLower(frame.Slice(FileWire.Header + 1 + i * 32, 32)));
            if (id != except) list.Add(id);
        }
        return list;
    }

    private void OnHashes(PeerId from, ReadOnlySpan<byte> frame)
    {
        if (_file.Hashes != null || _hashBuffer == null || _hashesFrom != from || frame.Length < FileWire.Header + 4) return;
        int first = (int)FileWire.U32(frame, FileWire.Header);
        var hashes = frame[(FileWire.Header + 4)..];
        if (first != _hashesHave || hashes.Length == 0 || hashes.Length % PieceHashes.HashSize != 0 || first * PieceHashes.HashSize + hashes.Length > _hashBuffer.Length) return;
        hashes.CopyTo(_hashBuffer.AsSpan(first * PieceHashes.HashSize));
        _hashesHave += hashes.Length / PieceHashes.HashSize;
        _hashContributors.Add(from);
        _hashesFrom = null;
        if (_hashesHave < Offer.PieceCount) return;

        var all = _hashBuffer;
        _hashBuffer = null;
        _hashesHave = 0;
        if (_file.SetHashes(all)) { _hashContributors.Clear(); return; }
        // Not the list the root names: whoever sent parts of it was lying or broken.
        foreach (var id in _hashContributors)
            if (_holders.TryGetValue(id, out var liar)) Strike(liar);
        _hashContributors.Clear();
    }

    private (int Piece, byte[] Data)? OnBlock(Holder holder, ReadOnlySpan<byte> frame)
    {
        if (frame.Length < FileWire.Header + 6) return null;
        int piece = (int)Math.Min(FileWire.U32(frame, FileWire.Header), int.MaxValue);
        int block = FileWire.U16(frame, FileWire.Header + 4);
        var data = frame[(FileWire.Header + 6)..];
        if (!_requests.TryGetValue(piece, out var request) || request.Holder != holder.Peer.Id || block != request.NextBlock) return null;
        int expected = Math.Min(FileOffer.BlockSize, Offer.PieceLength(piece) - block * FileOffer.BlockSize);
        if (data.Length != expected) return null;

        data.CopyTo(request.Buffer.AsSpan(block * FileOffer.BlockSize));
        request.NextBlock++;
        long now = _clock();
        request.LastBlockAt = now;
        Measure(holder, data.Length, now);
        if (request.NextBlock < Offer.BlockCount(piece)) return null;

        _requests.Remove(piece);
        holder.InFlight--;
        _verifying.Add(piece);
        return (piece, request.Buffer);
    }

    // Outside the lock: hashing and writing a piece take a few milliseconds and the holders should not wait for them.
    private bool FinishPiece(PeerId from, int piece, byte[] data)
    {
        bool ok;
        try { ok = _file.TryWritePiece(piece, data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _verifying.Remove(piece);
                _state = FileState.Failed;
            }
            _done.TrySetException(ex);
            Report(force: true);
            return false;
        }

        bool finished = false;
        List<IFilePeer>? tell = null;
        lock (_gate)
        {
            _verifying.Remove(piece);
            if (!ok)
            {
                if (_holders.TryGetValue(from, out var liar)) Strike(liar);
            }
            else
            {
                _partials.Remove(piece);
                tell = _holders.Values.Where(h => !h.Banned && h.Has != null).Select(h => h.Peer).ToList();
                if (_file.Have.IsComplete && _state != FileState.Completed)
                {
                    _state = FileState.Completed;
                    finished = true;
                }
            }
            Schedule();
        }
        if (ok && tell != null)
            foreach (var peer in tell) TrySend(peer, FileWire.WithPiece(FileWire.HaveMore, Offer.Root, piece));
        if (finished)
        {
            _done.TrySetResult();
            Report(force: true);
        }
        return finished;
    }

    // ---- choosing what to ask whom

    private void Schedule()
    {
        if (_state is FileState.Completed or FileState.Canceled or FileState.Failed or FileState.Paused) return;
        long now = _clock();
        var usable = _holders.Values.Where(h => !h.Banned && (_options.AllowMetered || !h.Peer.Metered)).ToList();

        if (_file.Hashes == null)
        {
            if (_hashesFrom == null) AskForHashes(usable, now);
            SetState(usable.Any(h => h.Has != null) ? FileState.Downloading : FileState.Waiting);
            return;
        }

        foreach (var h in usable.OrderByDescending(h => h.Speed).ThenByDescending(h => h.Peer.CanRelay))
        {
            if (h.Has == null || now < h.CooldownUntil) continue;
            while (h.InFlight < _options.Pipeline && NextPieceFor(h) is { } piece) StartRequest(h, piece, now);
        }
        bool any = _requests.Count > 0 || _verifying.Count > 0 || usable.Any(h => h.Has != null && now >= h.CooldownUntil);
        SetState(any ? FileState.Downloading : FileState.Waiting);
    }

    private void SetState(FileState state)
    {
        if (_state is FileState.Completed or FileState.Canceled or FileState.Failed or FileState.Paused) return;
        _state = state;
    }

    private void AskForHashes(List<Holder> usable, long now)
    {
        var holder = usable.Where(h => h.Has != null && now >= h.CooldownUntil).OrderByDescending(h => h.Speed).FirstOrDefault();
        if (holder == null) return;
        _hashBuffer ??= new byte[Offer.PieceCount * PieceHashes.HashSize];
        int count = Math.Min(FileWire.MaxHashesPerFrame, Offer.PieceCount - _hashesHave);
        _hashesFrom = holder.Peer.Id;
        _hashesAskedAt = now;
        if (!TrySend(holder.Peer, FileWire.WantHashes(Offer.Root, _hashesHave, count))) _hashesFrom = null;
    }

    // The piece this holder has that we still need, taking the one the fewest holders have out of a handful found from a random place (so downloaders do not all fetch the same
    // pieces first and then have nothing to give one another).
    private int? NextPieceFor(Holder holder)
    {
        int total = Offer.PieceCount;
        int scan = Math.Min(total, 16384);
        int best = -1, bestCount = int.MaxValue, found = 0;
        for (int i = 0; i < scan && found < 32; i++)
        {
            int piece = (_cursor + i) % total;
            if (_file.Have[piece] || _requests.ContainsKey(piece) || _verifying.Contains(piece) || holder.Has is not { } has || !has[piece]) continue;
            found++;
            int availability = _holders.Values.Count(h => !h.Banned && h.Has != null && h.Has[piece]);
            if (availability < bestCount) { best = piece; bestCount = availability; }
        }
        _cursor = (_cursor + Math.Max(1, scan / 4)) % total;
        return best < 0 ? null : best;
    }

    private void StartRequest(Holder holder, int piece, long now)
    {
        int blocks = Offer.BlockCount(piece);
        var buffer = new byte[Offer.PieceLength(piece)];
        int first = 0;
        if (_partials.Remove(piece, out var part)) { buffer = part.Buffer; first = part.Blocks; }
        var request = new Request { Holder = holder.Peer.Id, Buffer = buffer, NextBlock = first, StartedAt = now, LastBlockAt = now };
        _requests[piece] = request;
        holder.InFlight++;
        if (!TrySend(holder.Peer, FileWire.WantPiece(Offer.Root, piece, first, blocks - first)))
        {
            _requests.Remove(piece);
            holder.InFlight--;
            if (first > 0) _partials[piece] = (buffer, first);
        }
    }

    private void CancelRequest(int piece, bool tellHolder)
    {
        if (!_requests.Remove(piece, out var request)) return;
        if (_holders.TryGetValue(request.Holder, out var holder))
        {
            holder.InFlight = Math.Max(0, holder.InFlight - 1);
            if (tellHolder) TrySend(holder.Peer, FileWire.WithPiece(FileWire.Cancel, Offer.Root, piece));
        }
        // What did arrive is kept, so the next holder is asked only for the rest.
        if (request.NextBlock > 0 && _partials.Count < _options.MaxPartialPieces) _partials[piece] = (request.Buffer, request.NextBlock);
    }

    private void ReleaseRequestsOf(PeerId id)
    {
        foreach (var piece in _requests.Where(r => r.Value.Holder == id).Select(r => r.Key).ToList()) CancelRequest(piece, tellHolder: false);
        if (_holders.TryGetValue(id, out var h)) h.InFlight = 0;
        if (_hashesFrom == id) _hashesFrom = null;
    }

    private void Strike(Holder holder)
    {
        if (++holder.Strikes >= _options.MaxStrikes) holder.Banned = true;
    }

    private void AskInterest(Holder holder)
    {
        holder.InterestAt = _clock();
        if (!TrySend(holder.Peer, FileWire.Simple(FileWire.Interest, Offer.Root))) return;
        // Pieces we already hold make us a source for the holder's own download, and for anyone it refers to us.
        if (_file.Have.Count > 0) TrySend(holder.Peer, FileWire.HaveBits(Offer.Root, _file.Have));
    }

    private static bool TrySend(IFilePeer peer, byte[] frame)
    {
        try { peer.Send(frame); return true; }
        catch (InvalidOperationException) { return false; }
    }

    // ---- numbers

    // What a holder delivers, over windows of half a second, half of each new figure and half of the old one.
    private static void Measure(Holder holder, int bytes, long now)
    {
        if (holder.SpeedAt == 0) holder.SpeedAt = now;
        holder.WindowBytes += bytes;
        long elapsed = now - holder.SpeedAt;
        if (elapsed < 500) return;
        double rate = holder.WindowBytes * 1000.0 / elapsed;
        holder.Speed = holder.Speed == 0 ? rate : holder.Speed * 0.5 + rate * 0.5;
        holder.WindowBytes = 0;
        holder.SpeedAt = now;
    }

    public FileProgress Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    private FileProgress SnapshotLocked()
    {
        long done = _file.BytesHave + _requests.Sum(r => (long)r.Value.NextBlock * FileOffer.BlockSize) + _partials.Sum(p => (long)p.Value.Blocks * FileOffer.BlockSize);
        done = Math.Min(done, Offer.Size);
        return new FileProgress(Offer.Root, _state, _state == FileState.Completed ? Offer.Size : done, Offer.Size, _holders.Values.Count(h => !h.Banned && h.Has != null), (long)_holders.Values.Sum(h => h.Speed));
    }

    private void Report(bool force = false)
    {
        FileProgress progress;
        lock (_gate)
        {
            long now = _clock();
            if (!force && now - _lastReport < 250) return;
            _lastReport = now;
            progress = SnapshotLocked();
        }
        Changed?.Invoke(progress);
    }
}
