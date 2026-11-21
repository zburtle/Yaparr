using System.Collections.Concurrent;
using Yaparr.Chat.Files;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

// File sharing: any size, in a room or a direct chat, sent only to someone who asks, from every member that has it. See docs/file-sharing.md.
public sealed partial class ChatNode
{
    private FileLibrary? _files;
    private FileServer? _fileServer;
    private readonly ConcurrentDictionary<string, FileDownload> _fileDownloads = new();
    private readonly ConcurrentDictionary<string, Dictionary<PeerId, PeerConnection>> _fileAttached = new();
    private readonly ConcurrentDictionary<PeerId, SwarmPeer> _swarmPeers = new();

    /// <summary>Raised as a file download moves on: progress, waiting for someone, paused, finished, or failed. Also for a file that finished a moment ago.</summary>
    public event Action<FileProgress>? FileTransferChanged;

    /// <summary>Whether this node can share and download files (it needs <see cref="ChatNodeOptions.FilesDirectory"/>).</summary>
    public bool FileSharingAvailable => _files != null;

    private void InitFileSwarm()
    {
        if (_options.FilesDirectory is not { } directory) return;
        _files = new FileLibrary(directory, _options.OpenSharedFile);
        _fileServer = new FileServer(
            root => _fileDownloads.TryGetValue(root, out var d) ? d.File : _files.Shared(root),
            MayServeFile, FileUploadSlots, () => _options.FileBytesPerSecond, () => Environment.TickCount64, _options.Log);
    }

    // The same few places for everyone a node feeds as for a stream: from its measured upload, two to six.
    private int FileUploadSlots() => MeasuredUploadBytesPerSecond is { } up ? Math.Clamp((int)(up / (512 * 1024)), 2, 6) : 3;

    private void StartFileSwarm()
    {
        if (_files == null) return;
        foreach (var record in _files.Downloads())
        {
            try { BeginDownload(record.Offer, record.Destination, record.Paused); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _options.Log?.Invoke($"could not pick up the download of {record.Offer.Name}: {ex.Message}"); }
        }
        _ = Task.Run(FileLoopAsync);
    }

    private async Task FileLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                foreach (var (root, download) in _fileDownloads)
                {
                    try { AttachHolders(root, download); download.Tick(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { _options.Log?.Invoke($"file {root[..8]}: {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---- sharing and downloading, for the apps

    /// <summary>
    /// Gets a file ready to be offered: reads it once to work out its pieces (<paramref name="progress"/> gets the bytes read so far) and remembers where it is. Nothing is copied or sent;
    /// put the result in a message with <see cref="FileOffer.ToText"/> and send it to the room or chat it is for. <paramref name="room"/> or <paramref name="direct"/> says where it will be
    /// posted, so that only people in it are given the file. The file must stay where it is for as long as it is to be downloadable.
    /// </summary>
    public async Task<FileOffer> ShareFileAsync(string path, ChatRoom? room = null, PeerId? direct = null, IProgress<long>? progress = null, CancellationToken ct = default, string? name = null)
    {
        var files = _files ?? throw new InvalidOperationException("File sharing needs a folder to keep its records in.");
        if (room == null && direct == null) throw new ArgumentException("Say where the file will be posted.");
        // A content address (the phone's file chooser) is read through the app's opener; anything else is a path.
        bool isAddress = path.Contains("://", StringComparison.Ordinal);
        await using var source = (isAddress ? _options.OpenSharedFile?.Invoke(path) : null) ?? (isAddress ? null : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
                                 ?? throw new IOException("That file cannot be opened.");
        var (offer, hashes) = await PieceHashes.HashStreamAsync(source, name ?? Path.GetFileName(path), progress, ct).ConfigureAwait(false);
        files.AddScope(offer.Root, room?.Name, direct is { } person ? AccountOf(person).Value : null);
        files.AddShared(isAddress ? path : Path.GetFullPath(path), offer, hashes);
        return offer;
    }

    /// <summary>The file offered in a message, or null when it offers none.</summary>
    public static FileOffer? OfferIn(ChatMessage message) => FileOffer.TryParse(message.Text, out var offer, out _) ? offer : null;

    /// <summary>True when this device can give the file to others (it sent it, or has finished downloading it and it is still where it was saved).</summary>
    public bool HasFile(string root) => _files?.Shared(root) != null;

    /// <summary>Where a file that is on this device is (the original, or where the download was saved), or null.</summary>
    public string? FilePath(string root) => _files?.Shared(root)?.Path;

    /// <summary>
    /// Starts downloading the file a message offers, to <paramref name="destination"/> (a path for the finished file; a name already taken gets a number added). Nothing happens
    /// until this is called. Calling it again for a file that is already coming only returns where it is. Returns null when the message offers no file or sharing is not available.
    /// </summary>
    public FileProgress? StartFileDownload(ChatMessage message, string destination)
    {
        if (_files is not { } files || OfferIn(message) is not { } offer) return null;
        files.AddScope(offer.Root, message.Room, message.Room == null && !IsMyDevice(message.From.Id) ? message.From.Account.Value : null);
        if (_fileDownloads.TryGetValue(offer.Root, out var running)) { running.Resume(); return running.Snapshot(); }
        if (files.Shared(offer.Root) != null) return new FileProgress(offer.Root, FileState.Completed, offer.Size, offer.Size, 0, 0);
        return BeginDownload(offer, UniquePath(destination), paused: false).Snapshot();
    }

    public FileProgress? FileProgressOf(string root) => _fileDownloads.TryGetValue(root, out var d) ? d.Snapshot() : null;

    public void PauseFile(string root)
    {
        if (!_fileDownloads.TryGetValue(root, out var d)) return;
        d.Pause();
        if (_files?.Downloads().FirstOrDefault(r => r.Offer.Root == root) is { } record) _files.SetDownload(record.Offer, record.Destination, paused: true);
    }

    public void ResumeFile(string root)
    {
        if (!_fileDownloads.TryGetValue(root, out var d)) return;
        d.Resume();
        if (_files?.Downloads().FirstOrDefault(r => r.Offer.Root == root) is { } record) _files.SetDownload(record.Offer, record.Destination, paused: false);
    }

    /// <summary>Stops a download for good and throws away what had arrived.</summary>
    public void CancelFile(string root)
    {
        if (!_fileDownloads.TryRemove(root, out var d)) return;
        d.Cancel();
        _fileAttached.TryRemove(root, out _);
        _files?.ForgetDownload(root);
        d.File.Delete();
    }

    /// <summary>Stops offering a file that was shared or downloaded (it is not deleted).</summary>
    public void StopSharingFile(string root) => _files?.RemoveShared(root);

    private FileDownload BeginDownload(FileOffer offer, string destination, bool paused)
    {
        var files = _files!;
        var partial = PartialFile.OpenOrCreate(files.PartialDirectory(offer.Root), offer);
        var download = new FileDownload(partial, () => Environment.TickCount64);
        if (!_fileDownloads.TryAdd(offer.Root, download)) { partial.Dispose(); return _fileDownloads[offer.Root]; }
        files.SetDownload(offer, destination, paused);
        download.Changed += progress => FileTransferChanged?.Invoke(progress);
        download.Finished += () => _ = Task.Run(() => CompleteDownload(offer, destination, download));
        download.Referred += _ => AttachHolders(offer.Root, download);
        if (paused) download.Pause();
        AttachHolders(offer.Root, download);
        if (partial.Have.IsComplete) _ = Task.Run(() => CompleteDownload(offer, destination, download));
        return download;
    }

    private void CompleteDownload(FileOffer offer, string destination, FileDownload download)
    {
        var files = _files;
        if (files == null) return;
        try
        {
            var hashes = download.File.Hashes;
            var target = UniquePath(destination);
            download.File.Finish(target);
            if (hashes != null) files.AddShared(Path.GetFullPath(target), offer, hashes);
            files.ForgetDownload(offer.Root);
            _fileDownloads.TryRemove(offer.Root, out _);
            _fileAttached.TryRemove(offer.Root, out _);
            FileTransferChanged?.Invoke(new FileProgress(offer.Root, FileState.Completed, offer.Size, offer.Size, 0, 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke($"could not save {offer.Name}: {ex.Message}");
            FileTransferChanged?.Invoke(new FileProgress(offer.Root, FileState.Failed, download.File.BytesHave, offer.Size, 0, 0));
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "", name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, name + "-" + Guid.NewGuid().ToString("N")[..8] + ext);
    }

    // ---- who may have a file, and who has it

    // A message that offers a file says where it was posted; only people in those rooms and chats are given it (the file's root is also unguessable).
    private void NoteFileOffer(ChatMessage message)
    {
        if (_files == null || OfferIn(message) is not { } offer) return;
        if (message.Room != null) _files.AddScope(offer.Root, message.Room, null);
        else if (!IsMyDevice(message.From.Id)) _files.AddScope(offer.Root, null, message.From.Account.Value);
    }

    private bool MayServeFile(PeerId peer, string root)
    {
        if (_files is not { } files || !_peers.TryGetValue(peer, out var state) || IsBlocked(peer)) return false;
        if (IsSibling(state)) return true;
        return files.InScope(root, scope =>
            scope.Accounts.Contains(state.AccountId.Value) || scope.Accounts.Contains(state.Id.Value)
            || scope.Rooms.Any(name => _rooms.Values.FirstOrDefault(r => r.Name == name) is { } room && room.MemberStates.ContainsKey(peer) && !IsBannedIn(room, state.AccountId)));
    }

    // The people connected to us who could have this file: the members of the rooms it was posted in, the other person's devices in a chat, and our own devices.
    private List<PeerState> HoldersOfFile(string root)
    {
        var scope = _files!.ScopeOf(root);
        var found = new Dictionary<PeerId, PeerState>();
        foreach (var name in scope.Rooms)
            if (_rooms.Values.FirstOrDefault(r => r.Name == name) is { } room)
                foreach (var m in room.MemberStates.Values) found[m.Id] = m;
        foreach (var account in scope.Accounts)
            foreach (var d in DevicesOf(new PeerId(account))) found[d.Id] = d;
        foreach (var p in _peers.Values.Where(IsSibling)) found[p.Id] = p;
        return found.Values.Where(p => p.Id != Id && !IsBlocked(p.Id)).ToList();
    }

    private void AttachHolders(string root, FileDownload download)
    {
        var wanted = HoldersOfFile(root).ToDictionary(p => p.Id);
        var attached = _fileAttached.GetOrAdd(root, _ => new());
        lock (attached)
        {
            foreach (var id in attached.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                attached.Remove(id);
                download.RemovePeer(id);
            }
            foreach (var (id, state) in wanted)
            {
                var conn = state.Connection;
                if (attached.TryGetValue(id, out var known) && ReferenceEquals(known, conn)) continue;
                attached[id] = conn;
                download.AddPeer(SwarmPeerOf(state));
            }
        }
    }

    private SwarmPeer SwarmPeerOf(PeerState state) => _swarmPeers.GetOrAdd(state.Id, _ => new SwarmPeer(this, state)).For(state);

    private sealed class SwarmPeer(ChatNode node, PeerState initial) : IFilePeer
    {
        private volatile PeerState _state = initial;

        public SwarmPeer For(PeerState state) { _state = state; return this; }

        public PeerId Id => _state.Id;
        public bool Metered => node.IsMetered(_state.Connection);
        public bool CanRelay => _state.CanRelay == true;
        public long Backlog => _state.Connection.ReliableBacklog(ChannelIds.FileSwarm);
        public void Send(byte[] frame) => _state.Connection.SendReliable(ChannelIds.FileSwarm, frame);
    }

    private void OnFileSwarmData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (!reliable || _fileServer == null || conn.RemoteId is not { } id || !_peers.TryGetValue(id, out var state) || IsBlocked(id)) return;
        var frame = data.ToArray();
        if (!FileWire.TryRoot(frame, out var root)) return;
        var peer = SwarmPeerOf(state);
        _fileDownloads.TryGetValue(root, out var download);
        switch (frame[0])
        {
            case FileWire.Interest or FileWire.HashesWant or FileWire.Want or FileWire.Cancel:
                _fileServer.OnFrame(peer, frame);
                break;
            case FileWire.Have or FileWire.HaveMore:
                _fileServer.OnFrame(peer, frame);
                download?.OnFrame(peer, frame);
                break;
            default:
                download?.OnFrame(peer, frame);
                break;
        }
    }

    private void FileSwarmPeerGone(PeerId id)
    {
        _fileServer?.PeerGone(id);
        _swarmPeers.TryRemove(id, out _);
    }

    private void DisposeFileSwarm()
    {
        _fileServer?.Dispose();
        foreach (var d in _fileDownloads.Values) d.Dispose();
    }
}
