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
}

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

    public PieceSync(SyncEngine engine, IKeepTransport keep, SyncSealer sealer, IPieceSource source, Action<string>? log = null)
    {
        _engine = engine;
        _keep = keep;
        _sealer = sealer;
        _source = source;
        _log = log;
        engine.Remote += OnRemote;
    }

    private static Index? Parse(byte[] value)
    {
        try { return JsonSerializer.Deserialize<Index>(value) is { P.Length: 64 } index ? index : null; }
        catch (JsonException) { return null; }
    }

    private void OnRemote(SyncEntry entry)
    {
        if (entry.Kind != Kind || entry.Deleted || Parse(entry.Value) is not { } index) return;
        lock (_incoming) _incoming[entry.Id] = index;
    }

    private Index? Held(string name) => _engine.Get(Kind, name) is { Deleted: false } held ? Parse(held.Value) : null;

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
        }

        foreach (var (name, tag) in _source.Local())
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
        }

        // The records go out before what they replaced is removed, so no device is ever pointed at a piece that is gone.
        int removed = 0;
        if (uploaded > 0)
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
