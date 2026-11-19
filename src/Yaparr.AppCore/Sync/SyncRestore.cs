using System.Security.Cryptography;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.AppCore;

/// <summary>
/// The account's identity as a piece (<c>k-identity</c>, see <see cref="PieceSync"/>), so a new device can be brought back from the synced state alone, without a whole-profile
/// backup: the identity file of a device that holds the account key (<see cref="PeerIdentity.CanLinkDevices"/>, which every linked device does), locked with the Sync key like
/// every piece. Any device's file gives the account back (a restore makes a device key of its own), so the piece has a fixed tag and is written once, by whichever device gets
/// there first; devices do not rewrite it for each other. It is only ever <em>read</em> to restore (<see cref="SyncRestore"/>): a device that is running never takes one in, so the identity it has cannot be replaced by a sync.
/// </summary>
internal sealed class IdentityPieceSource(Func<byte[]?> identityFile) : IPieceSource
{
    public const string Name = "k-identity";

    private byte[]? Linkable()
    {
        var file = identityFile();
        if (file == null) return null;
        try
        {
            using var identity = PeerIdentity.Import(file);
            return identity.CanLinkDevices ? file : null;
        }
        catch (CryptographicException) { return null; }
    }

    public IReadOnlyDictionary<string, string> Local() =>
        Linkable() != null ? new Dictionary<string, string> { [Name] = "1" } : new Dictionary<string, string>();

    public byte[]? Build(string name) => name == Name ? Linkable() : null;

    // Nothing is ever taken in: restoring reads the piece itself, and a running device keeps the identity it has.
    public bool Take(string name, byte[] content) => true;
}

/// <summary>What <see cref="SyncRestore.FetchAsync"/> found.</summary>
public sealed record SyncRestoreResult(byte[] Identity, string? Name);

/// <summary>
/// Brings an account back on a new device from what is synced: reads the account's records, finds the identity piece and the name, and opens them with the Sync key. The rest (rooms,
/// blocks, pins, settings, history, shelves) comes through the usual sync once the device runs, so nothing else needs fetching here.
/// </summary>
public static class SyncRestore
{
    private sealed record Index(string P, string T);
    private sealed record AccountValue(string? V, long Ts, bool D);

    /// <summary>
    /// The identity and name the account holds, or null when it holds no identity piece (an account that has only whole-profile backups). Throws
    /// <see cref="CryptographicException"/> when the account has records but none opens with this key (the wrong Sync key), and what the server's calls throw.
    /// </summary>
    public static async Task<SyncRestoreResult?> FetchAsync(ISyncTransport sync, IKeepTransport keep, SyncSealer sealer, CancellationToken cancel = default)
    {
        SyncWireRecord? identity = null, name = null;
        long cursor = 0;
        while (true)
        {
            var page = await sync.PullAsync(cursor, cancel);
            foreach (var record in page.Records)
            {
                if (record.Deleted) continue;
                if (record is { Kind: PieceSync.Kind, Id: IdentityPieceSource.Name } && (identity == null || SyncClock.IsNewer(record.Clock, identity.Clock))) identity = record;
                if (record is { Kind: "account", Id: "name" } && (name == null || SyncClock.IsNewer(record.Clock, name.Clock))) name = record;
            }
            if (page.Cursor > cursor) cursor = page.Cursor;
            if (!page.More || page.Records.Count == 0) break;
        }
        if (identity == null) return null;

        byte[] indexBytes;
        try { indexBytes = sealer.Open(identity.Kind, identity.Id, identity.Clock, identity.Body); }
        catch (CryptographicException) { throw new CryptographicException("That Sync key does not open what Yaparr Cloud holds for this account."); }
        var index = JsonSerializer.Deserialize<Index>(indexBytes);
        if (index is not { P.Length: 64 }) return null;
        var locked = await keep.GetAsync(index.P, cancel);
        if (locked == null) return null;
        var file = sealer.Open(PieceSync.Kind, IdentityPieceSource.Name, "", Convert.ToBase64String(locked));

        string? displayName = null;
        if (name != null)
        {
            try { displayName = JsonSerializer.Deserialize<AccountValue>(sealer.Open(name.Kind, name.Id, name.Clock, name.Body)) is { D: false, V: { Length: > 0 } v } ? v : null; }
            catch (Exception ex) when (ex is CryptographicException or JsonException) { }
        }
        return new SyncRestoreResult(file, displayName);
    }

    /// <summary>
    /// Signs nothing in: with the sign-in the device already has, fetches the identity and name and makes a profile folder for them (<see cref="AppData.ApplyStagedProfile"/> puts it in
    /// place). Null when the account holds no identity piece, so the caller falls back to a whole-profile backup.
    /// </summary>
    public static async Task<string?> StageAsync(AppData data, Uri server, string token, string code, HttpClient? http = null, CancellationToken cancel = default)
    {
        using var sync = new SyncClient(server, token, http);
        using var keep = new KeepClient(server, token, http);
        return await StageAsync(data, sync, keep, SyncSealer.FromRecoveryCode(code), cancel);
    }

    public static async Task<string?> StageAsync(AppData data, ISyncTransport sync, IKeepTransport keep, SyncSealer sealer, CancellationToken cancel = default)
    {
        var found = await FetchAsync(sync, keep, sealer, cancel);
        return found == null ? null : data.StageFromAccount(found.Identity, found.Name);
    }
}
