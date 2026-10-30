using System.Collections.Concurrent;
using System.Text.Json;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

// A room's soundboard: a list of short sounds that any member can add and that is passed around the room like presence
// (each entry signed by whoever added it, the newest statement about an entry winning). The sound files travel like pictures,
// from any member that has them. Playing one sends a small signed message; everyone in the voice channel who has the sound
// plays it from their own copy, so a press costs a few hundred bytes however many people listen.
public sealed partial class ChatNode
{
    private const int SoundBatch = 32;
    private static readonly TimeSpan PlayMaxAge = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, long> _seenPlays = new();
    private readonly ConcurrentDictionary<string, long> _lastPlayAt = new();
    private readonly ConcurrentDictionary<string, Task<bool>> _soundFetches = new();

    private static string SoundsFile(string directory, ChatRoom room) => Path.Combine(directory, room.Key + ".sounds.json");

    private void LoadSounds(ChatRoom room)
    {
        if (_options.SoundboardDirectory is not { } directory) return;
        try
        {
            var path = SoundsFile(directory, room);
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<StoredSound[]>(File.ReadAllBytes(path), Json) ?? Array.Empty<StoredSound>();
            lock (room.SoundGate)
                foreach (var s in stored.Where(s => SignedSound.Verify(s, room.Key))) room.SoundEntries[s.Id] = s;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not read the soundboard: " + ex.Message);
        }
    }

    private void SaveSounds(ChatRoom room)
    {
        if (_options.SoundboardDirectory is not { } directory) return;
        try
        {
            StoredSound[] all;
            lock (room.SoundGate) all = room.SoundEntries.Values.ToArray();
            Directory.CreateDirectory(directory);
            var path = SoundsFile(directory, room);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(all, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the soundboard: " + ex.Message);
        }
    }

    private SoundInfo ToInfo(StoredSound s) =>
        new(s.Id, s.Name, new PeerId(s.From), s.OwnerName, s.Hash, s.Size, TimeSpan.FromMilliseconds(s.Ms), _blobs.Has(s.Hash));

    internal IReadOnlyList<SoundInfo> GetSounds(ChatRoom room)
    {
        List<StoredSound> live;
        lock (room.SoundGate) live = room.SoundEntries.Values.Where(s => !s.Deleted).ToList();
        return live.Where(s => IsValidPeerId(s.From) && !IsBlocked(new PeerId(s.From)))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal).Select(ToInfo).ToList();
    }

    internal SoundClip? ReadSound(ChatRoom room, string id)
    {
        StoredSound? s;
        lock (room.SoundGate) room.SoundEntries.TryGetValue(id, out s);
        if (s == null || s.Deleted || _blobs.Read(s.Hash) is not { } data) return null;
        return SoundClip.TryParse(data, out var clip) ? clip : null;
    }

    internal SoundInfo AddSound(ChatRoom room, string name, SoundClip clip)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A sound needs a name.");
        var hash = SoundClip.HashOf(clip.Data);
        _blobs.Save(hash, clip.Data);
        var entry = SignedSound.Create(_identity, room.Key, SignedSound.NewId(), name, DisplayName, hash, clip.Data.Length, (int)clip.Duration.TotalMilliseconds, NextTimestamp(), deleted: false);
        PublishSound(room, entry);
        return ToInfo(entry);
    }

    internal void RenameSound(ChatRoom room, string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A sound needs a name.");
        var own = OwnSound(room, id);
        PublishSound(room, SignedSound.Create(_identity, room.Key, id, name, own.OwnerName, own.Hash, own.Size, own.Ms, NextTimestamp(), deleted: false));
    }

    internal void RemoveSound(ChatRoom room, string id)
    {
        var own = OwnSound(room, id);
        PublishSound(room, SignedSound.Create(_identity, room.Key, id, own.Name, own.OwnerName, own.Hash, own.Size, own.Ms, NextTimestamp(), deleted: true));
    }

    // Only the member who added a sound can change or remove it.
    private StoredSound OwnSound(ChatRoom room, string id)
    {
        StoredSound? s;
        lock (room.SoundGate) room.SoundEntries.TryGetValue(id, out s);
        if (s == null || s.Deleted) throw new KeyNotFoundException("There is no such sound.");
        if (s.From != Id.Value) throw new InvalidOperationException("Only the person who added a sound can change or remove it.");
        return s;
    }

    private void PublishSound(ChatRoom room, StoredSound entry)
    {
        lock (room.SoundGate) room.SoundEntries[entry.Id] = entry;
        SaveSounds(room);
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Sound, Room = RefTo(room, member), Snd = new[] { entry } });
        room.RaiseSoundsChanged();
    }

    private void SendSoundsTo(ChatRoom room, PeerState peer)
    {
        StoredSound[] all;
        lock (room.SoundGate) all = room.SoundEntries.Values.ToArray();
        for (int i = 0; i < all.Length; i += SoundBatch)
            Send(peer.Connection, new Wire { Type = Wire.Sound, Room = RefTo(room, peer), Snd = all.Skip(i).Take(SoundBatch).ToArray() });
    }

    private void OnSounds(PeerState peer, ChatRoom room, StoredSound[] sounds)
    {
        var accepted = new List<StoredSound>();
        foreach (var s in sounds.Take(SoundBatch * 2))
        {
            if (!IsValidPeerId(s.From) || !SignedSound.Verify(s, room.Key)) continue;
            lock (room.SoundGate)
            {
                if (room.SoundEntries.TryGetValue(s.Id, out var old) && (old.From != s.From || old.Ts >= s.Ts)) continue;
                room.SoundEntries[s.Id] = s;
            }
            accepted.Add(s);
        }
        if (accepted.Count == 0) return;
        SaveSounds(room);
        foreach (var s in accepted)
            Relay(peer, room, s.From, reference => new Wire { Type = Wire.Sound, Room = reference, Snd = new[] { s } });
        room.RaiseSoundsChanged();
        FetchMissingSounds(room);
    }

    /// <summary>Starts downloading the sounds of a room that are not on this computer yet.</summary>
    private void FetchMissingSounds(ChatRoom room)
    {
        List<StoredSound> missing;
        lock (room.SoundGate) missing = room.SoundEntries.Values.Where(s => !s.Deleted && !_blobs.Has(s.Hash)).ToList();
        foreach (var s in missing.Where(s => IsValidPeerId(s.From) && !IsBlocked(new PeerId(s.From))))
            _ = FetchSoundAsync(room, s);
    }

    private Task<bool> FetchSoundAsync(ChatRoom room, StoredSound s)
    {
        if (_blobs.Has(s.Hash)) return Task.FromResult(true);
        lock (_soundFetches)
        {
            if (_soundFetches.TryGetValue(s.Hash, out var running) && !running.IsCompleted) return running;
            var task = Task.Run(async () =>
            {
                var a = new Attachment(s.Hash, "sound", s.Size, "audio/x-yaparr-sound", 0, 0);
                foreach (var holder in room.MemberStates.Values.OrderByDescending(m => m.Id.Value == s.From).ThenBy(m => IsMetered(m.Connection)))
                {
                    byte[]? data = null;
                    try { data = await FetchFromAsync(holder, a, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return false; }
                    if (data == null || !SoundClip.TryParse(data, out _)) continue;
                    _blobs.Save(s.Hash, data);
                    room.RaiseSoundsChanged();
                    return true;
                }
                return false;
            });
            _soundFetches[s.Hash] = task;
            return task;
        }
    }

    internal bool PlaySound(ChatRoom room, string id)
    {
        if (room.ActiveVoice is not { } session || session.Deafened) return false;
        var clip = ReadSound(room, id);
        if (clip == null) return false;
        StoredSound s;
        lock (room.SoundGate) s = room.SoundEntries[id];
        var play = SignedSound.CreatePlay(_identity, room.Key, id, s.Hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _seenPlays[play.Id] = Environment.TickCount64;
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Play, Room = RefTo(room, member), Pl = play });
        session.PlaySound(Id, clip);
        room.RaiseSoundPlayed(Id, ToInfo(s));
        return true;
    }

    private void OnPlay(PeerState peer, ChatRoom room, StoredPlay play)
    {
        if (!IsValidPeerId(play.From) || !SignedSound.VerifyPlay(play, room.Key, PlayMaxAge)) return;
        if (!_seenPlays.TryAdd(play.Id, Environment.TickCount64)) return;
        Relay(peer, room, play.From, reference => new Wire { Type = Wire.Play, Room = reference, Pl = play });

        var from = new PeerId(play.From);
        if (from == Id || IsBlocked(from) || room.ActiveVoice is not { } session) return;
        // Only people in the voice channel can play sounds into it.
        lock (room.IndirectGate)
            if (!room.Voice.TryGetValue(from, out var voice) || !voice.State.Active) return;

        long now = Environment.TickCount64;
        string key = room.Key + from.Value;
        if (_lastPlayAt.TryGetValue(key, out var last) && now - last < _options.SoundboardMinInterval.TotalMilliseconds) return;
        _lastPlayAt[key] = now;

        StoredSound? s;
        lock (room.SoundGate) room.SoundEntries.TryGetValue(play.Sound, out s);
        if (s == null || s.Deleted || s.Hash != play.Hash) return;
        if (ReadSound(room, s.Id) is not { } clip)
        {
            _ = FetchSoundAsync(room, s);
            return;
        }
        session.PlaySound(from, clip);
        room.RaiseSoundPlayed(from, ToInfo(s));
    }

    private void MaintainSounds(ChatRoom room, long now)
    {
        if (room.ActiveVoice != null && now - room.LastSoundFetchAt >= 15_000)
        {
            room.LastSoundFetchAt = now;
            FetchMissingSounds(room);
        }
        long cutoff = now - (long)PlayMaxAge.TotalMilliseconds * 2;
        foreach (var kv in _seenPlays.Where(kv => kv.Value < cutoff).ToList()) _seenPlays.TryRemove(kv.Key, out _);
        foreach (var kv in _lastPlayAt.Where(kv => kv.Value < cutoff).ToList()) _lastPlayAt.TryRemove(kv.Key, out _);
    }
}
