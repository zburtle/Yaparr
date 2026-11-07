using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

// A room's GIF shelf: pictures that any member can add and everyone in the room can send again. It is passed around the room the way the
// soundboard is (each entry signed by whoever added it, the newest statement about an entry winning, a removal kept as a note so that it
// cannot be undone by someone who still has the old entry), and the files travel like pictures, from any member that has them. The files are
// only fetched when somebody opens the shelf (RequestGifs), so a room's shelf costs nothing until it is looked at.
public sealed partial class ChatNode
{
    private const int GifBatch = 32;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<bool>> _gifFetches = new();

    private static string GifsFile(string directory, ChatRoom room) => Path.Combine(directory, room.Key + ".gifs.json");

    private void LoadGifs(ChatRoom room)
    {
        if (_options.SoundboardDirectory is not { } directory) return;
        try
        {
            var path = GifsFile(directory, room);
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<StoredGif[]>(File.ReadAllBytes(path), Json) ?? Array.Empty<StoredGif>();
            lock (room.GifGate)
                foreach (var g in stored.Where(g => SignedGif.Verify(g, room.Key))) room.GifEntries[g.Id] = g;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not read the GIF shelf: " + ex.Message);
        }
    }

    private void SaveGifs(ChatRoom room)
    {
        if (_options.SoundboardDirectory is not { } directory) return;
        try
        {
            StoredGif[] all;
            lock (room.GifGate) all = room.GifEntries.Values.ToArray();
            Directory.CreateDirectory(directory);
            var path = GifsFile(directory, room);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(all, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the GIF shelf: " + ex.Message);
        }
    }

    private GifInfo ToInfo(StoredGif g) => new(g.Id, new PeerId(g.From), g.OwnerName, g.Hash, g.Size, g.Mime, g.W, g.H, _blobs.Has(g.Hash));

    internal IReadOnlyList<GifInfo> GetGifs(ChatRoom room)
    {
        List<StoredGif> live;
        lock (room.GifGate) live = room.GifEntries.Values.Where(g => !g.Deleted).ToList();
        return live.Where(g => IsValidPeerId(g.From) && !IsBlocked(new PeerId(g.From)) && !IsBannedIn(room, new PeerId(g.From)) && !IsGifDeleted(room, g.Id))
            .OrderByDescending(g => g.Ts).ThenBy(g => g.Id, StringComparer.Ordinal).Select(ToInfo).ToList();
    }

    internal byte[]? ReadGif(ChatRoom room, string id)
    {
        StoredGif? g;
        lock (room.GifGate) room.GifEntries.TryGetValue(id, out g);
        if (g == null || g.Deleted || IsGifDeleted(room, id)) return null;
        return _blobs.Read(g.Hash);
    }

    internal GifInfo AddGif(ChatRoom room, byte[] data)
    {
        if (!ImageSniffer.TryRead(data, out var info))
            throw new ArgumentException("That is not a PNG, JPEG, GIF or WebP picture, or it is too large.");
        if (data.Length > GifLimits.MaxBytes)
            throw new ArgumentException($"That picture is {data.Length / (1024.0 * 1024):0.#} MB; a picture on a shelf can be at most {GifLimits.MaxBytes / (1024 * 1024)} MB.");
        // The same picture twice is the same entry.
        var hash = Attachment.HashOf(data);
        lock (room.GifGate)
        {
            var same = room.GifEntries.Values.FirstOrDefault(g => !g.Deleted && g.Hash == hash && !IsGifDeleted(room, g.Id));
            if (same != null) return ToInfo(same);
        }
        _blobs.Save(hash, data);
        var entry = SignedGif.Create(_identity, room.Key, SignedGif.NewId(), DisplayName, hash, data.Length, info.Mime, info.Width, info.Height, NextTimestamp(), deleted: false);
        PublishGif(room, entry);
        return ToInfo(entry);
    }

    // Only the member who added a picture can remove it (an admin takes one down with DeleteGif).
    internal void RemoveGif(ChatRoom room, string id)
    {
        StoredGif? g;
        lock (room.GifGate) room.GifEntries.TryGetValue(id, out g);
        if (g == null || g.Deleted) throw new KeyNotFoundException("There is no such picture on the shelf.");
        if (g.From != Id.Value) throw new InvalidOperationException("Only the person who added a picture can remove it.");
        PublishGif(room, SignedGif.Create(_identity, room.Key, id, g.OwnerName, g.Hash, g.Size, g.Mime, g.W, g.H, NextTimestamp(), deleted: true));
    }

    // Only so many removed pictures per person are kept; the oldest are forgotten. Call with GifGate held.
    private static void ForgetOldGifRemovals(ChatRoom room, string from)
    {
        foreach (var g in room.GifEntries.Values.Where(g => g.Deleted && g.From == from).OrderByDescending(g => g.Ts).Skip(GifLimits.RemovedPerMember).ToList())
            room.GifEntries.Remove(g.Id);
    }

    // The files of the pictures that are on a shelf, for the clean-up of old files at startup: from the rooms we are in (not counting what an
    // admin took down), and from the lists of rooms we are not in at the moment, which are read from disk. (A removed picture's file is not
    // deleted at once, because the same file may be a picture in the chat history; it goes with the old files.)
    private void AddLiveGifHashes(HashSet<string> hashes)
    {
        var joined = new HashSet<string>();
        foreach (var room in _rooms.Values)
        {
            joined.Add(room.Key);
            StoredGif[] all;
            lock (room.GifGate) all = room.GifEntries.Values.ToArray();
            foreach (var g in all.Where(g => !g.Deleted && !IsGifDeleted(room, g.Id))) hashes.Add(g.Hash);
        }
        if (_options.SoundboardDirectory is { } directory && Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.gifs.json"))
            {
                if (joined.Contains(Path.GetFileName(file)[..^".gifs.json".Length])) continue;
                try
                {
                    foreach (var g in JsonSerializer.Deserialize<StoredGif[]>(File.ReadAllBytes(file), Json) ?? Array.Empty<StoredGif>())
                        if (!g.Deleted) hashes.Add(g.Hash);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
    }

    internal void PublishGif(ChatRoom room, StoredGif entry)
    {
        lock (room.GifGate) { room.GifEntries[entry.Id] = entry; ForgetOldGifRemovals(room, entry.From); }
        SaveGifs(room);
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Gif, Room = RefTo(room, member), Gf = new[] { entry } });
        room.RaiseGifsChanged();
    }

    private void SendGifsTo(ChatRoom room, PeerState peer)
    {
        StoredGif[] all;
        lock (room.GifGate) all = room.GifEntries.Values.ToArray();
        for (int i = 0; i < all.Length; i += GifBatch)
            Send(peer.Connection, new Wire { Type = Wire.Gif, Room = RefTo(room, peer), Gf = all.Skip(i).Take(GifBatch).ToArray() });
    }

    private void OnGifs(PeerState peer, ChatRoom room, StoredGif[] gifs)
    {
        var accepted = new List<StoredGif>();
        foreach (var g in gifs.Take(GifBatch * 2))
        {
            if (g == null || !IsValidPeerId(g.From) || IsBannedIn(room, new PeerId(g.From)) || !SignedGif.Verify(g, room.Key)) continue;
            lock (room.GifGate)
            {
                if (room.GifEntries.TryGetValue(g.Id, out var old) && (old.From != g.From || old.Ts >= g.Ts)) continue;
                room.GifEntries[g.Id] = g;
                ForgetOldGifRemovals(room, g.From);
            }
            accepted.Add(g);
        }
        if (accepted.Count == 0) return;
        SaveGifs(room);
        foreach (var g in accepted)
            Relay(peer, room, g.From, reference => new Wire { Type = Wire.Gif, Room = reference, Gf = new[] { g } });
        room.RaiseGifsChanged();
    }

    /// <summary>Starts downloading the pictures of a room's shelf that are not on this computer yet (done when the shelf is opened).</summary>
    internal void RequestGifs(ChatRoom room)
    {
        List<StoredGif> missing;
        lock (room.GifGate) missing = room.GifEntries.Values.Where(g => !g.Deleted && !IsGifDeleted(room, g.Id) && !_blobs.Has(g.Hash)).ToList();
        foreach (var g in missing.Where(g => IsValidPeerId(g.From) && !IsBlocked(new PeerId(g.From)) && !IsBannedIn(room, new PeerId(g.From))))
            _ = FetchGifAsync(room, g);
    }

    private Task<bool> FetchGifAsync(ChatRoom room, StoredGif g)
    {
        if (_blobs.Has(g.Hash)) return Task.FromResult(true);
        lock (_gifFetches)
        {
            if (_gifFetches.TryGetValue(g.Hash, out var running) && !running.IsCompleted) return running;
            var task = Task.Run(async () =>
            {
                var a = new Attachment(g.Hash, "gif", g.Size, g.Mime, g.W, g.H);
                foreach (var holder in room.MemberStates.Values.OrderByDescending(m => m.Id.Value == g.From).ThenBy(m => IsMetered(m.Connection)))
                {
                    byte[]? data = null;
                    try { data = await FetchFromAsync(holder, a, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return false; }
                    if (data == null || data.Length > GifLimits.MaxBytes || !ImageSniffer.TryRead(data, out var info) || info.Mime != g.Mime) continue;
                    _blobs.Save(g.Hash, data);
                    room.RaiseGifsChanged();
                    return true;
                }
                return false;
            });
            _gifFetches[g.Hash] = task;
            return task;
        }
    }
}
