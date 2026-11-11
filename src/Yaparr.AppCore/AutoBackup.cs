using System.Security.Cryptography;

namespace Yaparr.AppCore;

/// <summary>Why a backup is due now.</summary>
public enum AutoBackupWhy
{
    /// <summary>It is not.</summary>
    Nothing,
    /// <summary>What cannot be lost has changed (a room, a key, a linked device, the name) and has been still for a moment.</summary>
    KeysChanged,
    /// <summary>A day has passed since the last backup, and the history has changed since.</summary>
    Daily,
}

/// <summary>What a check came to.</summary>
public enum AutoBackupResult
{
    /// <summary>No backup was due (or automatic backups are off).</summary>
    Nothing,
    /// <summary>Another check, or a backup the person asked for, was running.</summary>
    Busy,
    /// <summary>A backup was made and sent.</summary>
    BackedUp,
    /// <summary>The server has a newer backup than this device knows of, so nothing was sent and automatic backups wait for the person.</summary>
    Paused,
    /// <summary>The server does not know this device any more (signed out, password changed, account removed).</summary>
    SignedOut,
    /// <summary>It could not be done (no network, the server is down); it tries again later.</summary>
    Failed,
}

/// <summary>
/// When an automatic backup is due: a backup is saved as soon as what cannot be lost has changed, and once a day for the history. The two are different because
/// losing the identity key or the room keys is a disaster and is rare, while the history changes with every message and a day-old copy of it is fine. (The server
/// keeps the newest few copies and one a day for a week, so saving soon after a change does not push the older copies out.)
/// </summary>
public static class AutoBackupPlan
{
    /// <summary>How long what cannot be lost has to stay as it is before it is saved: a burst of changes (joining three rooms) becomes one backup.</summary>
    public static readonly TimeSpan KeysQuietFor = TimeSpan.FromMinutes(2);

    /// <summary>How often the history is saved.</summary>
    public static readonly TimeSpan HistoryEvery = TimeSpan.FromHours(24);

    /// <param name="keysChangedSince">When the keys were first seen to be different from the last backup's, if they are.</param>
    /// <param name="historyChanged">Whether the history has changed since the last backup; only asked when the answer matters.</param>
    public static AutoBackupWhy Decide(BackupServerState state, string currentKeys, DateTimeOffset? keysChangedSince, Func<bool> historyChanged, DateTimeOffset now)
    {
        // Off, signed out, or never backed up by hand (which is when the recovery code is made, and the person is shown it): nothing is sent on its own.
        if (!state.AutoBackup || !state.SignedIn || state.RecoveryCode.Length == 0 || state.Version <= 0) return AutoBackupWhy.Nothing;
        // Waiting for the person after another device saved a newer backup, or backing off after a failure.
        if (state.AutoPaused) return AutoBackupWhy.Nothing;
        if (state.RetryAfter is { } retry && now < retry) return AutoBackupWhy.Nothing;

        if (currentKeys != state.KeysFingerprint)
            return keysChangedSince is { } since && now - since >= KeysQuietFor ? AutoBackupWhy.KeysChanged : AutoBackupWhy.Nothing;

        // A backup made before this was recorded counts as long ago.
        if ((state.LastBackup is not { } last || now - last >= HistoryEvery) && historyChanged()) return AutoBackupWhy.Daily;
        return AutoBackupWhy.Nothing;
    }

    /// <summary>How long to wait after the n-th failure in a row: five minutes, doubling, at most six hours.</summary>
    public static TimeSpan Backoff(int failuresInARow) =>
        TimeSpan.FromMinutes(Math.Min(360, 5 * Math.Pow(2, Math.Clamp(failuresInARow, 1, 10) - 1)));
}

/// <summary>
/// Backs up to the backup server by itself, when <see cref="AutoBackupPlan"/> says it is due. Call <see cref="CheckAsync"/> every few minutes while the app
/// runs; it does nothing unless automatic backups are on, and never asks anything: when it cannot go on without the person (another device saved a newer
/// backup, or the sign-in is gone) it stops and leaves a note for the Backup page. A backup the person asks for and a check never run at once (<see cref="Gate"/>).
/// </summary>
public sealed class AutoBackup
{
    private readonly AppData _data;
    private readonly Func<DateTimeOffset> _now;
    private readonly HttpClient? _http;
    private readonly Action<string>? _log;
    private (string Keys, DateTimeOffset Since)? _pending;

    public AutoBackup(AppData data, Func<DateTimeOffset>? now = null, HttpClient? http = null, Action<string>? log = null)
    {
        _data = data;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = http;
        _log = log;
    }

    /// <summary>Held while a backup is being made, by a check or by the person's own request, so two never run at once.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Raised when the state on disk may have changed (a backup was made, or paused, or failed), for a page that shows it.</summary>
    public event Action? Changed;

    public async Task<AutoBackupResult> CheckAsync(CancellationToken cancel = default)
    {
        if (!await Gate.WaitAsync(0, cancel)) return AutoBackupResult.Busy;
        try
        {
            var state = _data.LoadBackupServer();
            if (!state.AutoBackup || !state.SignedIn) { _pending = null; return AutoBackupResult.Nothing; }

            var now = _now();
            var keys = _data.KeysFingerprint();
            // When the keys were first seen to differ from the last backup's. Each further change starts the quiet time again.
            if (keys == state.KeysFingerprint) _pending = null;
            else if (_pending is not { } p || p.Keys != keys) _pending = (keys, now);

            var lastBackup = state.LastBackup ?? DateTimeOffset.MinValue;
            var why = AutoBackupPlan.Decide(state, keys, _pending?.Since, () => _data.HistoryChangedSince(lastBackup), now);
            if (why == AutoBackupWhy.Nothing) return AutoBackupResult.Nothing;

            return await BackUpAsync(state, keys, why, now, cancel);
        }
        finally { Gate.Release(); }
    }

    private async Task<AutoBackupResult> BackUpAsync(BackupServerState state, string keys, AutoBackupWhy why, DateTimeOffset now, CancellationToken cancel)
    {
        var temp = Path.Combine(Path.GetTempPath(), "Yaparr-auto-backup-" + Guid.NewGuid().ToString("N") + ".yapbak");
        try
        {
            using var client = new BackupServerClient(new Uri(state.Url), state.Token, _http);
            var versions = await client.VersionsAsync(cancel);
            var newest = versions.Count == 0 ? 0 : versions[0].Version;

            // Another device saved since this one last did: adding to that is for the person to decide (the Backup page asks), not for a timer.
            if (newest != state.Version)
                return Paused(state, newest);

            var recoveryCode = state.RecoveryCode;
            await Task.Run(() => _data.ExportProfile(temp, recoveryCode), cancel);
            int version;
            try { version = await client.UploadAsync(temp, newest, cancel); }
            catch (BackupServerConflictException conflict) { return Paused(state, conflict.NewestVersion); }

            state.RecordBackup(version, keys, now);
            state.AutoNote = why == AutoBackupWhy.KeysChanged
                ? $"Backed up automatically (version {version}), after your rooms or keys changed."
                : $"Backed up automatically (version {version}).";
            _data.SaveBackupServer(state);
            _log?.Invoke($"backed up automatically to a server (version {version}, {why})");
            return AutoBackupResult.BackedUp;
        }
        catch (BackupServerException ex) when (ex.SignedOut)
        {
            state.Token = "";
            state.AutoNote = "Automatic backups stopped: this device is no longer signed in to the backup server. Sign in again.";
            _data.SaveBackupServer(state);
            _log?.Invoke("automatic backup: no longer signed in");
            return AutoBackupResult.SignedOut;
        }
        catch (Exception ex) when (ex is BackupServerException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            if (cancel.IsCancellationRequested) throw;
            state.Failures++;
            state.FirstFailure ??= now;
            state.RetryAfter = now + AutoBackupPlan.Backoff(state.Failures);
            var days = (now - state.FirstFailure.Value).TotalDays;
            state.AutoNote = $"Couldn't back up automatically: {ex.Message} Trying again later." +
                (days >= 3 ? $" It has been failing for {(int)days} days." : "");
            _data.SaveBackupServer(state);
            _log?.Invoke("automatic backup failed: " + ex.Message);
            return AutoBackupResult.Failed;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            Changed?.Invoke();
        }
    }

    private AutoBackupResult Paused(BackupServerState state, int newest)
    {
        state.AutoPaused = true;
        state.AutoNote = $"Automatic backups are waiting: another device saved a newer backup (version {newest}). Press Back up now to carry on.";
        _data.SaveBackupServer(state);
        _log?.Invoke($"automatic backup paused: the server has version {newest}");
        return AutoBackupResult.Paused;
    }
}
