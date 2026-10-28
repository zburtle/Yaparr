using System.Buffers.Binary;
using System.Collections.Concurrent;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

// Attachments: the message carries a small description, the file travels on its own channel so chat keeps flowing
// during a download. A file can come from the sender or from any room member that already has it.
public sealed partial class ChatNode
{
    private const byte FileWant = 1, FileChunk = 2, FileMissing = 3;
    private const int FileRange = 128 * 1024, FileMaxRange = 256 * 1024, FileChunkBytes = 16 * 1024;
    private static readonly TimeSpan FileStallTimeout = TimeSpan.FromSeconds(20);

    private sealed class Transfer
    {
        public required byte[] Buffer;
        public int Received;
        public int RangeEnd;
        public TaskCompletionSource<bool> Range = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long LastReported;
    }

    private readonly BlobStore _blobs;
    private readonly ConcurrentDictionary<(PeerId Peer, string Hash), Transfer> _transfers = new();
    private readonly ConcurrentDictionary<string, Task<AttachmentState>> _fetches = new();

    /// <summary>Raised as a file is downloaded, becomes available, or turns out not to be available.</summary>
    public event Action<AttachmentUpdate>? AttachmentChanged;

    /// <summary>
    /// Stores a picture so it can be sent. Put the result in a message with <see cref="Attachment.ToText"/>.
    /// Still pictures should be resized and stripped of metadata by the caller first; this only checks that the file
    /// is a picture we are willing to send and receive.
    /// </summary>
    public Attachment PrepareAttachment(byte[] data, string fileName, byte[]? thumbnail = null)
    {
        if (data.Length == 0 || data.Length > Attachment.MaxBytes)
            throw new ArgumentException($"A picture can be at most {Attachment.MaxBytes / (1024 * 1024)} MB.");
        if (!ImageSniffer.TryRead(data, out var info))
            throw new ArgumentException("That is not a picture Yaparr can send (PNG, JPEG, GIF or WebP, not too large).");
        if (thumbnail != null && !(thumbnail.Length <= Attachment.MaxThumbnailBytes && ImageSniffer.TryRead(thumbnail, out var t)
                                   && t.Frames == 1 && t.Width <= Attachment.MaxThumbnailDimension && t.Height <= Attachment.MaxThumbnailDimension))
            thumbnail = null;
        var hash = Attachment.HashOf(data);
        _blobs.Save(hash, data);
        return new Attachment(hash, Attachment.CleanName(fileName), data.Length, info.Mime, info.Width, info.Height, thumbnail);
    }

    public bool HasAttachment(string hash) => _blobs.Has(hash);

    /// <summary>The file for an attachment that is available on this computer, or null.</summary>
    public byte[]? ReadAttachment(string hash) => _blobs.Read(hash);

    /// <summary>
    /// Downloads an attachment from the sender or any other connected room member that has it. A file that is too big
    /// to fetch automatically over this connection is only fetched when <paramref name="force"/> is set.
    /// </summary>
    public Task<AttachmentState> FetchAttachmentAsync(ChatMessage message, bool force = false, CancellationToken ct = default)
    {
        if (message.Attachment is not { } a) return Task.FromResult(AttachmentState.Unavailable);
        if (_blobs.Has(a.Hash)) return Task.FromResult(AttachmentState.Ready);
        lock (_fetches)
        {
            if (_fetches.TryGetValue(a.Hash, out var running) && !running.IsCompleted) return running;
            var task = Task.Run(() => FetchCoreAsync(message, a, force, ct));
            _fetches[a.Hash] = task;
            return task;
        }
    }

    private void AutoFetch(ChatMessage message)
    {
        if (message.Attachment != null && !_blobs.Has(message.Attachment.Hash)) _ = FetchAttachmentAsync(message);
    }

    private void RaiseAttachment(string hash, AttachmentState state, long received, long total) =>
        AttachmentChanged?.Invoke(new AttachmentUpdate(hash, state, received, total));

    // Traffic that our own TURN relay is carrying costs the relay's bandwidth, so big files wait for a click there.
    private bool IsMetered(PeerConnection conn) => _turn is { } turn && !conn.IsRelayed && turn.UsesRelayFor(conn.RemoteEndPoint);

    private List<PeerState> Holders(ChatMessage message)
    {
        var list = new List<PeerState>();
        if (_peers.TryGetValue(message.From.Id, out var sender)) list.Add(sender);
        if (message.Room != null && _rooms.Values.FirstOrDefault(r => r.Name == message.Room) is { } room)
            list.AddRange(room.MemberStates.Values.Where(m => m.Id != message.From.Id));
        return list.OrderBy(p => IsMetered(p.Connection)).ToList();
    }

    private async Task<AttachmentState> FetchCoreAsync(ChatMessage message, Attachment a, bool force, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        bool deferred = false;
        foreach (var holder in Holders(message))
        {
            long limit = IsMetered(holder.Connection) ? _options.MaxMeteredAutoFetchBytes : _options.MaxAutoFetchBytes;
            if (!force && a.Size > limit) { deferred = true; continue; }

            RaiseAttachment(a.Hash, AttachmentState.Fetching, 0, a.Size);
            byte[]? data = null;
            try { data = await FetchFromAsync(holder, a, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (data == null) continue;

            _blobs.Save(a.Hash, data);
            RaiseAttachment(a.Hash, AttachmentState.Ready, a.Size, a.Size);
            return AttachmentState.Ready;
        }
        var state = deferred ? AttachmentState.NeedsConfirmation : AttachmentState.Unavailable;
        RaiseAttachment(a.Hash, state, 0, a.Size);
        return state;
    }

    private async Task<byte[]?> FetchFromAsync(PeerState peer, Attachment a, CancellationToken ct)
    {
        var transfer = new Transfer { Buffer = new byte[a.Size] };
        var key = (peer.Id, a.Hash);
        if (!_transfers.TryAdd(key, transfer)) return null;
        try
        {
            var hash = Convert.FromHexString(a.Hash);
            while (transfer.Received < a.Size)
            {
                int offset, length;
                Task<bool> range;
                lock (transfer)
                {
                    offset = transfer.Received;
                    length = (int)Math.Min(FileRange, a.Size - offset);
                    transfer.RangeEnd = offset + length;
                    transfer.Range = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    range = transfer.Range.Task;
                }
                var want = new byte[41];
                want[0] = FileWant;
                hash.CopyTo(want, 1);
                BinaryPrimitives.WriteUInt32BigEndian(want.AsSpan(33), (uint)offset);
                BinaryPrimitives.WriteUInt32BigEndian(want.AsSpan(37), (uint)length);
                SendFile(peer.Connection, want);

                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(FileStallTimeout);
                try { if (!await range.WaitAsync(stall.Token).ConfigureAwait(false)) return null; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _options.Log?.Invoke($"download from {peer.Id.Short} stalled");
                    return null;
                }
            }
            if (Attachment.HashOf(transfer.Buffer) == a.Hash) return transfer.Buffer;
            _options.Log?.Invoke($"a picture from {peer.Id.Short} did not match its hash and was discarded");
            return null;
        }
        finally { _transfers.TryRemove(key, out _); }
    }

    private void SendFile(PeerConnection conn, byte[] frame)
    {
        try { conn.SendReliable(ChannelIds.File, frame); }
        catch (InvalidOperationException ex) { _options.Log?.Invoke($"could not send a file to {conn.RemoteId?.Short}: {ex.Message}"); }
    }

    private void OnFileData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (!reliable || conn.RemoteId is not { } id || !_peers.ContainsKey(id) || data.Length < 33) return;
        var span = data.Span;
        string hash = Convert.ToHexString(span.Slice(1, 32)).ToLowerInvariant();

        switch (span[0])
        {
            case FileWant when span.Length == 41:
                int offset = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(span[33..]), int.MaxValue);
                int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(span[37..]), int.MaxValue);
                if (length <= 0 || length > FileMaxRange) return;
                var bytes = _blobs.ReadRange(hash, offset, length);
                if (bytes == null || bytes.Length == 0)
                {
                    var missing = new byte[33];
                    missing[0] = FileMissing;
                    span.Slice(1, 32).CopyTo(missing.AsSpan(1));
                    SendFile(conn, missing);
                    return;
                }
                for (int sent = 0; sent < bytes.Length; sent += FileChunkBytes)
                {
                    int n = Math.Min(FileChunkBytes, bytes.Length - sent);
                    var chunk = new byte[37 + n];
                    chunk[0] = FileChunk;
                    span.Slice(1, 32).CopyTo(chunk.AsSpan(1));
                    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(33), (uint)(offset + sent));
                    bytes.AsSpan(sent, n).CopyTo(chunk.AsSpan(37));
                    SendFile(conn, chunk);
                }
                break;

            case FileChunk when span.Length > 37 && _transfers.TryGetValue((id, hash), out var t):
                long report = 0;
                lock (t)
                {
                    int at = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(span[33..]), int.MaxValue);
                    int n = span.Length - 37;
                    if (at != t.Received || t.Received + n > t.RangeEnd) return;
                    span[37..].CopyTo(t.Buffer.AsSpan(t.Received));
                    t.Received += n;
                    if (t.Received == t.RangeEnd) t.Range.TrySetResult(true);
                    long now = Environment.TickCount64;
                    if (now - t.LastReported >= 100) { t.LastReported = now; report = t.Received; }
                }
                if (report > 0) RaiseAttachment(hash, AttachmentState.Fetching, report, t.Buffer.Length);
                break;

            case FileMissing when _transfers.TryGetValue((id, hash), out var gone):
                lock (gone) gone.Range.TrySetResult(false);
                break;
        }
    }
}
