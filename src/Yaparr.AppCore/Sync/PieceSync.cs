using Yaparr.Identity;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yaparr.AppCore;

/// <summary>What there is to keep in sync as pieces (see <see cref="PieceSync"/>): the app's side. A piece has a name and a <em>tag</em> that changes whenever its content does.</summary>
public interface IPieceSource
{
    /// <summary>Every piece this device could write now, by name, with its tag.</summary>
    IReadOnlyDictionary<string, string> Local();

    /// <summary>The content of a piece, or null when there is none (any more).</summary>
    byte[]? Build(string name);

    /// <summary>Takes in a piece that came from another device. Returns false when it is not a good piece.</summary>
    bool Take(string name, byte[] content);

    /// <summary>Whether taking a piece away here (it is no longer in <see cref="Local"/>) means it was removed on purpose, so the account should lose it too. History never is.</summary>
    bool Removable(string name) => false;

    /// <summary>Another device removed this piece: remove it here.</summary>
    void Drop(string name) { }
}

/// <summary>How far a <see cref="PieceSync.RunAsync"/> has come: pieces taken in or kept so far, of those it has to do. Told after each one.</summary>
public sealed record PieceProgress(int Done, int Total);

/// <summary>What one <see cref="PieceSync.RunAsync"/> did.</summary>
public sealed record PieceReport(int Uploaded, int Downloaded, int Removed, int Unreadable, bool Full);

/// <summary>
/// Things too big for a synced record, or that have to last, are kept as <b>pieces</b>: a piece is locked on the device (<see cref="SyncSealer"/>, bound to its name) and put on the
/// server's kept store under the SHA-256 of the locked bytes (<see cref="IKeepTransport"/>), and a small synced record of kind <c>piece</c> (named like the piece) says which piece is the
/// current one and what tag it had. The records go through the same engine as everything else, so the newest write of a piece's record wins on every device.
/// <para>
/// A run first takes in the pieces other devices have written since (this device's own state may then differ from the account's, which is why it comes first), then writes a piece for
/// each name whose tag is not the account's, sends the records, and removes the pieces they replaced. Two devices that wrote the same piece differently converge: each takes the other's
/// rows in, the tags then agree on the larger set, and whoever still differs writes it again.
/// </para>
/// </summary>
public sealed class PieceSync
{
    public const string Kind = "piece";
    private const int MaxPiece = 15 * 1024 * 1024;

    private sealed record Index(string P, string T);

    private readonly SyncEngine _engine;
    private readonly IKeepTransport _keep;
    private readonly SyncSealer _sealer;
    private readonly IPieceSource _source;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Index> _incoming = new(StringComparer.Ordinal);
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private readonly HashSet<string> _drops = new(StringComparer.Ordinal);
    private readonly string? _knownFile;
    private HashSet<string>? _known;

    /// <param name="knownFile">Where the names this device had at its last run are kept (a sealed file), to tell a piece removed here from one never taken in; null keeps them in memory only.</param>
    public PieceSync(SyncEngine engine, IKeepTransport keep, SyncSealer sealer, IPieceSource source, Action<string>? log = null, string? knownFile = null)
    {
        _knownFile = knownFile;
        _engine = engine;
        _keep = keep;
        _sealer = sealer;
        _source = source;
        _log = log;
        engine.Remote += OnRemote;
    }

    /// <summary>The id of the piece a piece record points at, or null if it is not a good record.</summary>
    internal static string? PieceIdOf(byte[] value) => Parse(value)?.P;

    private static Index? Parse(byte[] value)
    {
        try { return JsonSerializer.Deserialize<Index>(value) is { P.Length: 64 } index ? index : null; }
        catch (JsonException) { return null; }
    }

    private void OnRemote(SyncEntry entry)
    {
        if (entry.Kind != Kind) return;
        if (entry.Deleted) { lock (_incoming) { _incoming.Remove(entry.Id); _drops.Add(entry.Id); } return; }
        if (Parse(entry.Value) is not { } index) return;
        lock (_incoming) { _incoming[entry.Id] = index; _drops.Remove(entry.Id); }
    }

    private Index? Held(string name) => _engine.Get(Kind, name) is { Deleted: false } held ? Parse(held.Value) : null;

    private HashSet<string> LoadKnown()
    {
        if (_known != null) return _known;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (_knownFile != null && File.Exists(_knownFile))
        {
            try { foreach (var name in JsonSerializer.Deserialize<string[]>(SecretStore.ReadAllBytes(_knownFile)) ?? []) names.Add(name); }
            catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or UnauthorizedAccessException) { }
        }
        return _known = names;
    }

    private void SaveKnown(HashSet<string> names)
    {
        _known = names;
        if (_knownFile == null) return;
        try { SecretStore.WriteAllBytes(_knownFile, JsonSerializer.SerializeToUtf8Bytes(names.Order(StringComparer.Ordinal).ToArray())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.Invoke("could not save the names of kept pieces: " + ex.Message); }
    }

    /// <summary>Told how far a run has come, after each piece it takes in or keeps (a new device has many to take in; a first sync many to keep).</summary>
    public Action<PieceProgress>? Progress { get; set; }

    /// <summary>How many pieces the account has that this device has not taken in yet.</summary>
    public int Waiting { get { lock (_incoming) return _incoming.Count; } }

    /// <summary>
    /// Takes in what is waiting, writes what differs, and sends it. Call after the records were pulled (<see cref="SyncEngine.SyncAsync"/>); the sends happen here.
    /// Throws what the server's calls throw (<see cref="BackupServerException"/>); pieces not yet taken in stay waiting.
    /// </summary>
    public async Task<PieceReport> RunAsync(CancellationToken cancel = default)
    {
        int uploaded = 0, downloaded = 0, unreadable = 0;
        bool full = false;
        var replaced = new List<string>();

        KeyValuePair<string, Index>[] waiting;
        lock (_incoming) waiting = _incoming.ToArray();
        int total = waiting.Length, done = 0;
        var plan = _source.Local();
        total += plan.Count(p => Held(p.Key) is not { } h || h.T != p.Value);
        void Told() { done++; try { Progress?.Invoke(new PieceProgress(Math.Min(done, total), total)); } catch (Exception ex) when (ex is not OutOfMemoryException) { } }
        foreach (var (name, index) in waiting)
        {
            cancel.ThrowIfCancellationRequested();
            if (!_done.Contains(index.P))
            {
                var locked = await _keep.GetAsync(index.P, cancel);
                if (locked != null)
                {
                    byte[]? content = null;
                    try { content = _sealer.Open(Kind, name, "", Convert.ToBase64String(locked)); }
                    catch (CryptographicException) { unreadable++; }
                    if (content != null)
                    {
                        if (_source.Take(name, content)) downloaded++; else unreadable++;
                    }
                }
                _done.Add(index.P);
            }
            lock (_incoming) if (_incoming.TryGetValue(name, out var now) && now == index) _incoming.Remove(name);
            Told();
        }

        string[] drops;
        lock (_incoming) { drops = _drops.ToArray(); _drops.Clear(); }
        foreach (var name in drops)
            if (_source.Removable(name) && _engine.Get(Kind, name) is { Deleted: true }) _source.Drop(name);

        var local = _source.Local();
        // A piece this device had at its last run and has not now, which the account still holds, was removed here: the account loses it too.
        var known = LoadKnown();
        int dropped = 0;
        foreach (var name in known)
            if (!local.ContainsKey(name) && _source.Removable(name) && Held(name) is { } gone)
            {
                _engine.Remove(Kind, name);
                replaced.Add(gone.P);
                dropped++;
            }
        SaveKnown(local.Keys.Where(_source.Removable).ToHashSet(StringComparer.Ordinal));

        foreach (var (name, tag) in local)
        {
            cancel.ThrowIfCancellationRequested();
            var held = Held(name);
            if (held != null && held.T == tag) continue;
            var content = _source.Build(name);
            if (content == null) continue;
            var locked = Convert.FromBase64String(_sealer.Seal(Kind, name, "", content));
            if (locked.Length > MaxPiece) { _log?.Invoke($"keeping {name}: too big for one piece, left out"); continue; }
            var id = Convert.ToHexStringLower(SHA256.HashData(locked));
            var result = await _keep.PutAsync(id, locked, cancel);
            if (result == KeepPutResult.Full) { full = true; _log?.Invoke("the account has no room left for kept pieces"); break; }
            if (result != KeepPutResult.Stored) break;   // too many calls lately: the rest waits for the next run
            _done.Add(id);
            if (held != null && held.P != id) replaced.Add(held.P);
            _engine.Set(Kind, name, JsonSerializer.SerializeToUtf8Bytes(new Index(id, tag)));
            uploaded++;
            Told();
        }

        // The records go out before what they replaced is removed, so no device is ever pointed at a piece that is gone.
        int removed = 0;
        if (uploaded > 0 || dropped > 0)
        {
            var report = await _engine.SyncAsync(cancel);
            if (report.Complete)
                foreach (var id in replaced.Distinct())
                {
                    try { await _keep.DeleteAsync(id, cancel); removed++; }
                    catch (BackupServerException ex) when (!ex.SignedOut) { /* left on the server; it counts against the quota until removed */ }
                }
        }
        return new PieceReport(uploaded, downloaded, removed, unreadable, full);
    }
}
