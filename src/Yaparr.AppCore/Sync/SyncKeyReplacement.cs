using System.Security.Cryptography;
using System.Text;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>What <see cref="SyncKeyReplacement.ReplaceAsync"/> came to.</summary>
public sealed record KeyReplacementResult(bool Done, string Problem, int RecordsRemoved, int PiecesRemoved);

/// <summary>
/// Replaces the account's Sync key (for a key that was lost or that someone else has seen): everything the account keeps in sync is locked again with the new key, and what the old
/// key locked is removed from the server. It goes in the order that leaves nothing half done:
/// <list type="number">
/// <item>sync with the old key, so this device holds all of what the account has;</item>
/// <item>keep what the old state held (which records, which pieces), then save the new key and drop the sync state, so the next run writes <em>everything</em> under the new key;</item>
/// <item>run the sync with the new key (the old records come back as unreadable and are skipped); if it stops half way, the next ordinary sync finishes it, because nothing is held as written;</item>
/// <item>remove what the old key locked: the old records the new state does not hold (written as deleted, which needs no key), the pieces they pointed at, and the whole-profile backups.</item>
/// </list>
/// Other devices see records they cannot open and halt (<see cref="SyncEngine.HaltOnUnreadable"/>) instead of writing over them with the old key, until they are given the new key.
/// </summary>
public static class SyncKeyReplacement
{
    public static async Task<KeyReplacementResult> ReplaceAsync(AppData data, ChatNode node, string newCode, HttpClient? http = null, Action<string>? log = null, CancellationToken cancel = default,
        Func<CancellationToken, Task>? removeBackups = null)
    {
        var server = data.LoadBackupServer();
        if (!server.SignedIn || server.RecoveryCode.Length == 0) return new(false, "This device is not signed in to Yaparr Cloud with a Sync key.", 0, 0);
        var normal = RecoveryCode.Normalize(newCode);
        if (normal == null) return new(false, "That is not a Sync key.", 0, 0);
        if (normal == RecoveryCode.Normalize(server.RecoveryCode)) return new(false, "That is the Sync key already in use.", 0, 0);

        // 1. Current under the old key.
        var first = await AccountCloudSync.RunAsync(data, node, http, log, cancel, haltOnUnreadable: true);
        if (first != AccountSyncResult.Synced) return new(false, first == AccountSyncResult.KeyChanged ? "Another device has already replaced the Sync key. Enter that one." : "Sync with the old key did not finish. Try again when it can.", 0, 0);

        // 2. What the old key locked, then the switch.
        var which = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server.Url + "|" + server.Username)))[..12];
        var statePath = Path.Combine(data.Directory, $"sync-{which}.json");
        var knownPath = Path.Combine(data.Directory, $"sync-{which}.known");
        var old = new FileSyncState(statePath, log).All().Where(e => !e.Deleted).ToList();
        var oldPieces = old.Where(e => e.Kind == PieceSync.Kind).Select(e => PieceSync.PieceIdOf(e.Value)).Where(id => id != null).Select(id => id!).ToHashSet();
        var latestClock = old.Select(e => e.Clock).Concat(new FileSyncState(statePath, log).All().Select(e => e.Clock)).DefaultIfEmpty("").Max(StringComparer.Ordinal) ?? "";

        server.RecoveryCode = normal;
        // The old backups are removed below; a made-over fingerprint has the next automatic backup make a new one, locked with the new key.
        server.KeysFingerprint = "";
        data.SaveBackupServer(server);
        foreach (var path in new[] { statePath, knownPath }) try { File.Delete(path); } catch (IOException) { }

        // 3. Everything again, under the new key.
        var second = await AccountCloudSync.RunAsync(data, node, http, log, cancel, haltOnUnreadable: false);
        if (second != AccountSyncResult.Synced)
            return new(false, "The new Sync key is saved on this device, but sending everything with it did not finish. It carries on by itself the next time Yaparr syncs.", 0, 0);

        // 4. What the old key locked.
        var live = new FileSyncState(statePath, log).All().Where(e => !e.Deleted).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var newPieces = new FileSyncState(statePath, log).All().Where(e => !e.Deleted && e.Kind == PieceSync.Kind).Select(e => PieceSync.PieceIdOf(e.Value)).Where(id => id != null).Select(id => id!).ToHashSet();
        int records = 0, pieces = 0;
        try
        {
            using var transport = new SyncClient(new Uri(server.Url), server.Token, http);
            var clock = new SyncClock(node.Id.Value);
            clock.Receive(latestClock);
            var markers = old.Where(e => !live.Contains(e.Key)).Select(e => new SyncWireRecord(e.Kind, e.Id, clock.Next(), node.Id.Value, true, "")).ToList();
            for (int i = 0; i < markers.Count; i += SyncClient.MaxBatch)
            {
                var batch = markers.Skip(i).Take(SyncClient.MaxBatch).ToList();
                var result = await transport.PushAsync(batch, cancel);
                records += batch.Count - result.Stale.Count;
            }
            using var keep = new KeepClient(new Uri(server.Url), server.Token, http);
            foreach (var id in oldPieces.Where(id => !newPieces.Contains(id)))
            {
                await keep.DeleteAsync(id, cancel);
                pieces++;
            }
            if (removeBackups != null) await removeBackups(cancel);
            else
            {
                using var backups = new BackupServerClient(new Uri(server.Url), server.Token);
                await backups.DeleteAsync(cancel);
            }
        }
        catch (BackupServerException ex)
        {
            log?.Invoke("replacing the Sync key: could not remove what the old key locked: " + ex.Message);
            return new(true, "The Sync key is replaced, but what the old key locked could not all be removed from the server: " + ex.Message, records, pieces);
        }
        server = data.LoadBackupServer();
        server.AutoNote = "";
        data.SaveBackupServer(server);
        log?.Invoke($"replaced the Sync key: {records} old record(s) and {pieces} old piece(s) removed");
        return new(true, "", records, pieces);
    }

    /// <summary>
    /// Gives this device the replacement key another device made: the key is checked against what the server holds (a record that opens), saved, and the sync state is kept (it holds
    /// only what this device has already read and decrypted), so the next sync reads the new records. Returns a problem for the person, or "" when it is done.
    /// </summary>
    public static async Task<string> AdoptAsync(AppData data, string typed, HttpClient? http = null, CancellationToken cancel = default)
    {
        var server = data.LoadBackupServer();
        if (!server.SignedIn) return "This device is not signed in to Yaparr Cloud.";
        var code = RecoveryCode.Normalize(typed);
        if (code == null) return "That is not a Sync key. It is six groups of four letters and digits.";
        SyncSealer sealer;
        try { sealer = SyncSealer.FromRecoveryCode(code); }
        catch (ArgumentException) { return "That is not a Sync key."; }
        try
        {
            using var transport = new SyncClient(new Uri(server.Url), server.Token, http);
            var opens = false;
            long cursor = 0;
            while (!opens)
            {
                var page = await transport.PullAsync(cursor, cancel);
                foreach (var record in page.Records.Where(r => !r.Deleted))
                {
                    try { sealer.Open(record.Kind, record.Id, record.Clock, record.Body); opens = true; break; }
                    catch (CryptographicException) { }
                }
                if (page.Cursor > cursor) cursor = page.Cursor;
                if (!page.More || page.Records.Count == 0) break;
            }
            if (!opens) return "That key does not open what Yaparr Cloud holds. Check it and try again.";
        }
        catch (BackupServerException ex) { return ex.Message; }
        server.RecoveryCode = code;
        server.AutoNote = "";
        data.SaveBackupServer(server);
        return "";
    }
}
