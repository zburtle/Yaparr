using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>What one <see cref="AccountCloudSync.SyncAsync"/> came to.</summary>
public enum AccountSyncResult
{
    /// <summary>Not signed in to a server, or no recovery code yet (it is made at the first backup by hand), so there is nothing to sync with.</summary>
    NotSetUp,
    /// <summary>Another sync was running.</summary>
    Busy,
    /// <summary>This device and the server agree.</summary>
    Synced,
    /// <summary>The server does not know this device any more.</summary>
    SignedOut,
    /// <summary>The server holds records this device's Sync key cannot open: the key was replaced on another device. Nothing was sent; the device needs the new key.</summary>
    KeyChanged,
    /// <summary>It could not be done (no network, the server is down); it tries again later.</summary>
    Failed,
}

/// <summary>
/// Puts the account's name, rooms and blocks on the server, locked with the recovery code (see <see cref="SyncEngine"/>), and brings in what the person's other devices put
/// there. The devices already keep these the same among themselves while they are online (<c>ChatNode</c>'s account entries, newest <c>Ts</c> wins); this carries the same
/// entries through the server, so a device that was off, or was never online together with the others, still gets them. The entries stay the app's: the cloud copy of an
/// entry holds the entry itself (value, <c>Ts</c>, deleted), and an entry that comes back is handed to the node, which keeps it only if it is newer than its own by the
/// same rule, so the cloud never overrules the rule the devices already use between them.
/// </summary>
public sealed class AccountCloudSync
{
    private const string Kind = "account";

    /// <summary>What the sync page says when the Sync key was replaced on another device.</summary>
    public const string KeyChangedNote = "Your Sync key was replaced on another device. Enter the new key to keep syncing.";

    private readonly Func<IReadOnlyList<AccountEntry>> _entries;
    private readonly Action<IReadOnlyList<AccountEntry>> _apply;
    private readonly SyncEngine _engine;
    private readonly ISyncState _state;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly List<AccountEntry> _incoming = new();
    private readonly PieceSync? _pieces;

    private sealed record Value(string? V, long Ts, bool D);

    public AccountCloudSync(Func<IReadOnlyList<AccountEntry>> entries, Action<IReadOnlyList<AccountEntry>> apply, ISyncState state, ISyncTransport transport,
        SyncSealer sealer, string deviceId, Action<string>? log = null, Func<DateTimeOffset>? now = null, IKeepTransport? keep = null, IPieceSource? pieces = null, string? knownFile = null, bool haltOnUnreadable = false, Action<PieceProgress>? progress = null)
    {
        _entries = entries;
        _apply = apply;
        _state = state;
        _log = log;
        var clock = new SyncClock(deviceId, now ?? (() => DateTimeOffset.UtcNow));
        // A device that was off: the writes it had made and the ones it had read are what its clock has to be later than.
        foreach (var held in state.All()) clock.Receive(held.Clock);
        _engine = new SyncEngine(state, transport, sealer, clock, deviceId);
        _engine.Remote += OnRemote;
        _engine.HaltOnUnreadable = haltOnUnreadable;
        if (keep != null && pieces != null) _pieces = new PieceSync(_engine, keep, sealer, pieces, log, knownFile) { Progress = progress };
    }

    /// <summary>Whether an entry is on the cloud copy: its id says what it is, and the name has to be one the server takes.</summary>
    private static bool Wanted(AccountEntry e) => ChatNode.IsCloudEntry(e) && e.Key.Length <= 128 && e.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '@' or '+' or '=' or '/' or '|');

    private static byte[] Encode(AccountEntry e) => JsonSerializer.SerializeToUtf8Bytes(new Value(e.Value, e.Ts, e.Deleted));

    private static AccountEntry? Decode(string key, byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<Value>(bytes) is { } v ? new AccountEntry(key, v.V, v.Ts, v.D) : null; }
        catch (JsonException) { return null; }
    }

    private void OnRemote(SyncEntry entry)
    {
        if (entry.Kind != Kind || entry.Deleted) return;
        if (Decode(entry.Id, entry.Value) is { } e && Wanted(e)) lock (_incoming) _incoming.Add(e);
    }

    // An entry of the node's that the copy does not have, or has an older version of (by the node's own rule).
    private bool Behind(AccountEntry e)
    {
        if (!Wanted(e)) return false;
        var held = _state.Get(Kind, e.Key);
        if (held == null || held.Deleted) return true;
        return Decode(e.Key, held.Value) is not { } there || ChatNode.IsNewerEntry(e, there);
    }

    private bool NeedsWrite() => _entries().Any(Behind);

    /// <summary>
    /// What the node holds is written to the cloud copy where that is newer than what the copy holds (by the node's own rule), the copy is synced, and what came from other
    /// devices is given to the node. Throws what the server's calls throw (<see cref="BackupServerException"/>).
    /// </summary>
    public async Task<(AccountSyncResult Result, SyncReport? Report)> SyncAsync(CancellationToken cancel = default)
    {
        if (!await _running.WaitAsync(0, cancel)) return (AccountSyncResult.Busy, null);
        try
        {
            int written = 0, sent = 0, received = 0, unreadable = 0;
            SyncReport report;
            // A second pass only when the first found an entry of this device that the server's order had put behind an older one (two devices writing in the same
            // millisecond): this device's entry is newer by the rule the devices use, so it is written again, and now wins.
            for (int pass = 0; ; pass++)
            {
                int writes = 0;
                foreach (var e in _entries())
                {
                    // What the copy holds is the same, or newer by the node's own rule (it is handed over below): nothing to write.
                    if (!Behind(e)) continue;
                    _engine.Set(Kind, e.Key, Encode(e));
                    writes++;
                }
                written += writes;

                report = await _engine.SyncAsync(cancel);
                if (report.Halted)
                {
                    _log?.Invoke("sync halted: the server holds records this Sync key cannot open");
                    return (AccountSyncResult.KeyChanged, report);
                }
                sent += report.Sent;
                received += report.Received;
                unreadable += report.Unreadable;

                AccountEntry[] incoming;
                lock (_incoming) { incoming = _incoming.ToArray(); _incoming.Clear(); }
                if (incoming.Length > 0) _apply(incoming);
                if (pass >= 2 || !report.Complete || !NeedsWrite()) break;
            }

            if (written > 0 || received > 0 || unreadable > 0)
                _log?.Invoke($"synced the account: sent {sent}, received {received}{(unreadable > 0 ? $", {unreadable} could not be opened" : "")}");

            // What is kept as pieces (history): taken in, and written where this device differs from the account.
            if (_pieces != null && report.Complete)
            {
                var kept = await _pieces.RunAsync(cancel);
                if (kept.Uploaded > 0 || kept.Downloaded > 0 || kept.Unreadable > 0)
                    _log?.Invoke($"synced pieces: kept {kept.Uploaded}, took in {kept.Downloaded}, removed {kept.Removed}{(kept.Unreadable > 0 ? $", {kept.Unreadable} could not be used" : "")}{(kept.Full ? ", the account is full" : "")}");
            }
            return (report.Complete ? AccountSyncResult.Synced : AccountSyncResult.Failed, report);
        }
        finally { _running.Release(); }
    }

    /// <summary>What this app keeps as pieces: the history of its node's store (h-, d-) and the person's own emoji and GIF shelves (e-, g-).</summary>
    private static IPieceSource PiecesOf(AppData data, ChatNode node)
    {
        var shelves = new ShelfPieceSource(new EmojiShelf(data.EmojiDirectory), new GifShelf(data.ShelfDirectory));
        var sources = new List<(string, IPieceSource)> { ("e-", shelves), ("g-", shelves), ("k-", new IdentityPieceSource(data.ReadIdentityFile)) };
        if (node.KeptHistory is { } store)
        {
            // What was taken in changes what a conversation that is open shows: it is told, so it reads its window again.
            var history = new HistoryPieceSource(store, (name, _) =>
            {
                if (HistoryPieces.WhoOf(name) is { } who) node.HistoryChangedBySync(new[] { who });
            });
            sources.Add(("h-", history));
            sources.Add(("d-", history));
            sources.Add(("x-", history));
        }
        return new PieceSources(sources.ToArray());
    }

    /// <summary>
    /// One sync of this app's account with the server it is signed in to (nothing when it is not, or has no recovery code yet). Never throws for what can go wrong with the
    /// server: that comes back as the result, and a sign-in the server no longer knows is cleared from the saved state so that it is not tried again.
    /// </summary>
    public static async Task<AccountSyncResult> RunAsync(AppData data, ChatNode node, HttpClient? http = null, Action<string>? log = null, CancellationToken cancel = default, bool haltOnUnreadable = true, Action<PieceProgress>? progress = null)
    {
        var server = data.LoadBackupServer();
        if (!server.SignedIn || server.RecoveryCode.Length == 0) return AccountSyncResult.NotSetUp;
        if (server.SubscriptionPaused(DateTimeOffset.UtcNow)) return AccountSyncResult.Failed;   // the subscription ended: left alone until the pause is over (BackupServerState.PauseForSubscription)
        SyncSealer sealer;
        try { sealer = SyncSealer.FromRecoveryCode(server.RecoveryCode); }
        catch (ArgumentException) { return AccountSyncResult.NotSetUp; }
        // Another server or another account is another copy: its state is kept in a file of its own.
        var which = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server.Url + "|" + server.Username)))[..12];
        var state = new FileSyncState(Path.Combine(data.Directory, $"sync-{which}.json"), log);
        try
        {
            using var transport = new SyncClient(new Uri(server.Url), server.Token, http);
            using var keep = new KeepClient(new Uri(server.Url), server.Token, http);
            var sync = new AccountCloudSync(() => node.AccountEntries, node.ApplyCloudEntries, state, transport, sealer, node.Id.Value, log,
                keep: keep, pieces: PiecesOf(data, node), knownFile: Path.Combine(data.Directory, $"sync-{which}.known"), haltOnUnreadable: haltOnUnreadable, progress: progress);
            var result = (await sync.SyncAsync(cancel)).Result;
            if (result == AccountSyncResult.KeyChanged && !server.AutoNote.StartsWith(KeyChangedNote, StringComparison.Ordinal))
            {
                server.AutoNote = KeyChangedNote;
                data.SaveBackupServer(server);
            }
            return result;
        }
        catch (BackupServerException ex) when (ex.SignedOut)
        {
            // The backup page says what to do; the same sign-in serves backups, so it is cleared there too.
            server.Token = "";
            server.AutoNote = "This device is no longer signed in to Yaparr Cloud. Sign in again.";
            data.SaveBackupServer(server);
            log?.Invoke("sync: no longer signed in");
            return AccountSyncResult.SignedOut;
        }
        catch (BackupServerException ex) when (ex.SubscriptionEnded)
        {
            server.PauseForSubscription(DateTimeOffset.UtcNow);
            data.SaveBackupServer(server);
            log?.Invoke("sync: the subscription has ended");
            return AccountSyncResult.Failed;
        }
        catch (Exception ex) when (ex is BackupServerException or IOException or UriFormatException)
        {
            if (cancel.IsCancellationRequested) throw;
            log?.Invoke("sync failed: " + ex.Message);
            return AccountSyncResult.Failed;
        }
    }
}
