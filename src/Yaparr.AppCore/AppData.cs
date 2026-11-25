using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.AppCore;

public sealed class PrivateRoomSetting
{
    public string Label { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed class KnownPeer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Whether this person was last heard to use Yaparr Cloud (said when they were connected), so the list can say so when they are away.</summary>
    public bool Cloud { get; set; }
}

public sealed class AppSettings
{
    public List<KnownPeer> KnownPeers { get; set; } = new();

    /// <summary>Leave messages (and pictures) on Yaparr Cloud for people who are away, when signed in to a server. On by default; peer to peer is how messages travel either way.</summary>
    public bool LeaveForAbsent { get; set; } = true;
    public string? DisplayName { get; set; }
    public bool VpnEnabled { get; set; }

    /// <summary>When a message you send has a web link, fetch the page's title, description and picture and send them along as a card under the link. The readers never contact the site.</summary>
    public bool LinkPreviews { get; set; } = true;

    /// <summary>The computer app: closing the window leaves Yaparr running behind an icon in the notification area (the Exit menu item and the icon's Exit end it).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// The app looks for a newer version when it starts and offers it (the phone app and the desktop app both). This contacts GitHub, which sees the address
    /// the app connects from. The manual check (Help, Check for updates, on the desktop) is always there.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The version of the update the phone app offered last, so that the same one is not offered again at every start.</summary>
    public string? OfferedUpdate { get; set; }

    /// <summary>System, Light or Dark: the colours the app uses, chosen in the settings on the phone and on the computer (each device keeps its own). System follows the device's own light or dark setting.</summary>
    public string Theme { get; set; } = ThemeChoice.System;

    /// <summary>The first-run connection guide has been shown, so it does not come up again by itself.</summary>
    public bool ConnectionGuideShown { get; set; }
    public List<PrivateRoomSetting> PrivateRooms { get; set; } = new();

    /// <summary>For each text channel (the room's id, a slash and the channel's id), the time in milliseconds up to which it has been seen; what is newer is unread.</summary>
    public Dictionary<string, long> ReadMarks { get; set; } = new();

    /// <summary>The text channel that was on show in each room (by the room's id), which comes back the next time.</summary>
    public Dictionary<string, string> RoomChannels { get; set; } = new();

    /// <summary>Names of the microphone and speakers to use; empty means the system default.</summary>
    public string? VoiceInput { get; set; }
    public string? VoiceOutput { get; set; }
    /// <summary>Loudness (0 to 100) the microphone has to reach before voice activation sends.</summary>
    public int VoiceSensitivity { get; set; } = 12;
    /// <summary>How loud everyone sounds to you, in percent (100 is as received, up to 300).</summary>
    public int VoiceOutputVolume { get; set; } = 100;
    /// <summary>Send all the time instead of only when talking.</summary>
    public bool VoiceAlwaysOn { get; set; }
    /// <summary>Send only while a key is held (instead of voice activation).</summary>
    public bool VoicePushToTalk { get; set; }
    /// <summary>What to hold: an Avalonia key name such as F13, or Mouse3 (middle), Mouse4 (back) or Mouse5 (forward); none until one is chosen.</summary>
    public string? VoicePushToTalkKey { get; set; }
    /// <summary>Play a short tick when the push-to-talk key goes down.</summary>
    public bool VoicePushToTalkSound { get; set; }
    /// <summary>Stop other people hearing themselves through your speakers.</summary>
    public bool VoiceEchoCancellation { get; set; } = true;
    /// <summary>Off, Low, Moderate or High: how much steady background noise is taken out of your microphone.</summary>
    public string VoiceNoiseSuppression { get; set; } = "Moderate";
    /// <summary>YaparrCustom (WebRTC's noise suppression, at the strength above) or RNNoise (no strength). The computer app.</summary>
    public string VoiceNoiseSuppressor { get; set; } = "YaparrCustom";
    /// <summary>Take short, sharp clicks (keyboard and mouse) out of your microphone. Adds 10 ms of delay to your voice.</summary>
    public bool VoiceClickSuppression { get; set; }
    /// <summary>Raise a quiet microphone and lower a loud one towards a normal speaking level before it is sent.</summary>
    public bool VoiceAutomaticVolume { get; set; }
    /// <summary>How much data your voice takes: 32, 48 or 64 kilobits a second (64 unless chosen otherwise; it was 32 in older versions).</summary>
    public int VoiceBitrateKbps { get; set; } = 64;
    /// <summary>The video format last picked when sharing a screen: true for AV1, false for Automatic. The computer app.</summary>
    public bool ScreenShareAv1 { get; set; }
    /// <summary>True when H.265 was the video format last picked when sharing a screen (it is ignored where this version has H.265 switched off). The computer app.</summary>
    public bool ScreenShareH265 { get; set; }
    /// <summary>Lower the bitrate of a share when the viewers cannot keep up (on unless turned off in the share dialog). The computer app.</summary>
    public bool ScreenShareAdaptQuality { get; set; } = true;
    /// <summary>Bring everyone you hear to a similar loudness (on top of each person's own volume).</summary>
    public bool VoiceLevelSpeakers { get; set; }

    /// <summary>People whose soundboard sounds you have silenced (their voice is not affected), by id.</summary>
    public List<string> SoundboardMuted { get; set; } = new();
    /// <summary>
    /// The sound on each of the ten number keys of a room's soundboard (1 to 9, then 0), by room key: ten sound ids, "" for an empty key. A room that is not
    /// listed has the first ten sounds as they are listed (see <see cref="SoundSlots"/>). Yours alone: nobody else sees it.
    /// </summary>
    public Dictionary<string, List<string>> SoundboardSlots { get; set; } = new();
    /// <summary>Silence everybody's soundboard sounds.</summary>
    public bool SoundboardsMuted { get; set; }
    /// <summary>How loud soundboard sounds are against voices, in percent (100 is as loud as a voice).</summary>
    public int SoundboardVolume { get; set; } = 80;
    /// <summary>Where the soundboard opens: over the main window (false) or in a window of its own (true). Follows the last choice made with the Pop out / Show in the app button.</summary>
    public bool SoundboardInWindow { get; set; }

    /// <summary>Where a shared screen opens: in a window of its own (true) or inside the main window over the chat (false). Follows the last choice made with the Pop out / Show in the app button.</summary>
    public bool WatchScreensInWindow { get; set; } = true;

    /// <summary>Play a sound for new messages and for people joining or leaving your voice channel.</summary>
    public bool SoundsEnabled { get; set; } = true;
    /// <summary>Flash the taskbar button when a message arrives while the window is in the background.</summary>
    public bool FlashTaskbar { get; set; } = true;
    /// <summary>Linux and Windows: show a pop-up in the system's notification area for a message that arrives while the window is in the background.</summary>
    public bool DesktopPopups { get; set; } = true;
    /// <summary>For busy rooms: only being named or answered, and direct messages, make a sound or flash the taskbar. Other messages still count as unread.</summary>
    public bool NotifyOnlyMentions { get; set; }
    /// <summary>Show the number of unread messages on the taskbar button (Windows).</summary>
    public bool TaskbarCount { get; set; } = true;

    /// <summary>The phone app: show a notification for a direct message, a mention or a reply to you, and for every message of the rooms in <see cref="NotifyAllRooms"/>, while the app is not open.</summary>
    public bool NotifyMessages { get; set; } = true;

    /// <summary>
    /// The phone app: this phone has been asked, once, whether to turn on notifications and push (right after signing in to Yaparr Cloud), whatever the answer was, so it is not asked again.
    /// Belongs to the phone: it is not one of the settings kept the same on every device.
    /// </summary>
    public bool NotificationsOffered { get; set; }

    /// <summary>
    /// When the network does not let UDP out and the device is signed in to Yaparr Cloud, send Yaparr's traffic through the server's gateway, which is enough for messages (see
    /// <c>GatewayFallback</c>). Belongs to the device (its network is its own): not one of the settings kept the same on every device.
    /// </summary>
    public bool GatewayWhenUdpBlocked { get; set; } = true;

    /// <summary>The phone app: stay connected when the app is not on the screen, so that messages and calls keep coming (shows a notification while it is on). Off until the person turns it on.</summary>
    public bool BackgroundConnection { get; set; }

    /// <summary>The phone app: the rooms (by id) that notify for every message, not only for mentions, replies and direct messages.</summary>
    public List<string> NotifyAllRooms { get; set; } = new();

    /// <summary>The phone app and the computer app: the rooms (by id) that never notify, not even for a mention or a reply (their messages still count as unread).</summary>
    public List<string> MutedRooms { get; set; } = new();

    /// <summary>The computer app: a message sound of a room's own (a name in <c>UiSounds.MessageStyles</c>), by room id; a room that is not here uses <see cref="MessageSound"/>.</summary>
    public Dictionary<string, string> RoomSounds { get; set; } = new();

    /// <summary>Which sound a new message makes: one of the names in <c>UiSounds.MessageStyles</c>.</summary>
    public string MessageSound { get; set; } = "Chime";

    /// <summary>How loud each person sounds to you, in percent (0 to 200), by account id. People not listed are at 100.</summary>
    public Dictionary<string, int> PersonVolumes { get; set; } = new();

    /// <summary>People you have blocked, with the name they had when you blocked them.</summary>
    public List<KnownPeer> BlockedPeers { get; set; } = new();
}

public sealed class AppData
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yaparr");

    public static void MigrateLegacyDefault()
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeerChat");
        if (System.IO.Directory.Exists(legacy) && !System.IO.Directory.Exists(DefaultDirectory))
        {
            try { System.IO.Directory.Move(legacy, DefaultDirectory); }
            catch (IOException) {  }
        }
    }

    public AppData(string directory)
    {
        Directory = directory;
        CreatePrivateDirectory(directory);
        ProtectSecrets();
    }

    // Files with keys in them: sealed so only this Windows user can read them, and closed to other users elsewhere.
    // Files from earlier versions are plain until this runs.
    private static readonly string[] SecretFiles = { "identity.key", "settings.json", "account.json", "vpn.conf", "history.key", "backup-server.json" };

    private void ProtectSecrets()
    {
        foreach (var name in SecretFiles)
        {
            var path = Path.Combine(Directory, name);
            try { SecretStore.Upgrade(path); }
            catch (IOException) { }
            MakePrivate(path);
        }
    }

    // What a backup holds and a restore accepts. Anything else in the folder (the FFmpeg program, installers, the log) is not
    // part of a profile, and a restore must never be able to place a program where Yaparr will later run it.
    private static readonly string[] BackedUpFiles = { "identity.key", "settings.json", "account.json", "peers.json", "clocks.json", "network.json", "vpn.conf", "history.key", "dht-nodes.txt" };
    private static readonly string[] BackedUpFolders = { "history", "direct", "attachments", "soundboards" };
    private const long MaxRestoreBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxRestoreEntries = 200_000;

    internal static bool IsProfilePath(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) return false;
        return parts.Length == 1 ? BackedUpFiles.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
                                 : BackedUpFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates a folder only its owner can use (on Linux and macOS; Windows keeps a profile folder private already).</summary>
    public static void CreatePrivateDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Makes a file only its owner can read (on Linux and macOS).</summary>
    public static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public string Directory { get; }

    /// <summary>
    /// A random id this device made for itself, once, and keeps (<c>device-key.txt</c>). It goes to Yaparr Cloud when the device signs in, so signing in again (after being signed out, say)
    /// takes the place of the device's earlier sign-in in the server's list of devices instead of adding another one. It is not part of a backup: a profile restored on another computer
    /// is another device and makes its own.
    /// </summary>
    public string DeviceKey
    {
        get
        {
            var path = Path.Combine(Directory, "device-key.txt");
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 16 and <= 64 } held && held.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return held;
                var made = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                File.WriteAllText(path, made);
                return made;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }   // none: the server then treats each sign-in as a device of its own, as before
        }
    }
    public string HistoryDirectory => Path.Combine(Directory, "history");

    /// <summary>The database every room's messages are kept in (encrypted by SQLCipher with a key made from the history key).</summary>
    public string HistoryDatabasePath => Path.Combine(HistoryDirectory, "history.db");

    /// <summary>
    /// Opens the history database, and brings over the history files of earlier versions (one file for each room) the first time. Keep it open for as long
    /// as the app runs and dispose it at the end.
    /// </summary>
    public Yaparr.History.Sqlite.SqliteHistoryStore OpenHistoryStore(Yaparr.Identity.HistoryCipher cipher, Action<string>? log = null)
    {
        var key = cipher.DatabaseKey();
        var store = Yaparr.History.Sqlite.SqliteHistoryStore.Open(HistoryDatabasePath, key, log);
        // A backup is made from a consistent copy of the database, not from the file it is writing to.
        _historySnapshot = path => store.SnapshotTo(path, key);
        try
        {
            Yaparr.Chat.HistoryImport.FromFiles(store, HistoryDirectory, cipher, log);
            Yaparr.Chat.DirectHistoryImport.FromFiles(store, Path.Combine(Directory, "direct"), cipher, log);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { log?.Invoke($"could not bring over the old history: {ex.Message}"); }
        return store;
    }
    /// <summary>Where the GIF shelf (pictures kept to send again) lives.</summary>
    public string ShelfDirectory => Path.Combine(Directory, "gifshelf");
    /// <summary>Where a person's own custom emoji live (see <c>EmojiShelf</c>).</summary>
    public string EmojiDirectory => Path.Combine(Directory, "emojishelf");
    public string VpnConfigPath => Path.Combine(Directory, "vpn.conf");
    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string IdentityPath => Path.Combine(Directory, "identity.key");

    public AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(SecretStore.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException) { return new(); }
    }

    /// <summary>Raised after the settings were saved, so what is kept the same on all devices (<see cref="SettingsSync"/>) can be given to the account.</summary>
    public event Action<AppSettings>? SettingsSaved;

    public void SaveSettings(AppSettings settings)
    {
        SecretStore.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));
        SettingsSaved?.Invoke(settings);
    }

    private string BackupServerPath => Path.Combine(Directory, "backup-server.json");
    private string PushPath => Path.Combine(Directory, "push.json");

    /// <summary>
    /// A fingerprint of what cannot be lost: the identity key, the key that locks the history, the account (its rooms, blocks and devices), the VPN configuration, the
    /// name, and the room keys. It changes when one of those does (a room joined or made, a key changed, a device linked, the name changed) and not when the
    /// history grows or a setting like the theme is changed. Automatic backups compare it with the one saved at the last backup to know a backup is due.
    /// </summary>
    public string KeysFingerprint()
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(string label, byte[]? bytes)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{label}:{bytes?.Length ?? -1};"));
            if (bytes != null) hash.AppendData(bytes);
        }
        // The files are sealed for this computer, and sealing differs from one write to the next, so what is hashed is what is inside.
        foreach (var name in new[] { "identity.key", "history.key", "account.json", "vpn.conf" })
        {
            byte[]? bytes = null;
            var path = Path.Combine(Directory, name);
            try { if (File.Exists(path)) bytes = SecretStore.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
            Add(name, bytes);
        }
        var settings = LoadSettings();
        Add("name", System.Text.Encoding.UTF8.GetBytes(settings.DisplayName ?? ""));
        foreach (var room in settings.PrivateRooms.OrderBy(r => r.Key, StringComparer.Ordinal))
            Add("room", System.Text.Encoding.UTF8.GetBytes(room.Key + "|" + room.Label));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>True if any message, direct chat, picture or sound has been written since the given moment: whether a backup of the history would hold anything new.</summary>
    public bool HistoryChangedSince(DateTimeOffset since)
    {
        foreach (var folder in BackedUpFolders)
        {
            var path = Path.Combine(Directory, folder);
            if (!System.IO.Directory.Exists(path)) continue;
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    if (File.GetLastWriteTimeUtc(file) > since.UtcDateTime) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    /// <summary>How this device is woken for push notifications, and the wake address it gave out. Sealed like the other files with secrets, and not part of a backup.</summary>
    public PushState LoadPush()
    {
        try { return JsonSerializer.Deserialize<PushState>(SecretStore.ReadAllText(PushPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException) { return new(); }
    }

    public void SavePush(PushState state)
    {
        SecretStore.WriteAllText(PushPath, JsonSerializer.Serialize(state, Json));
        MakePrivate(PushPath);
    }

    /// <summary>What this device remembers about its backup server. It is sealed like the other files with secrets, and is not part of a backup.</summary>
    public BackupServerState LoadBackupServer()
    {
        try { return JsonSerializer.Deserialize<BackupServerState>(SecretStore.ReadAllText(BackupServerPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException) { return new(); }
    }

    public void SaveBackupServer(BackupServerState state)
    {
        SecretStore.WriteAllText(BackupServerPath, JsonSerializer.Serialize(state, Json));
        MakePrivate(BackupServerPath);
    }

    /// <summary>
    /// Saves a backup of the profile. With a passphrase the file is locked (see <see cref="ProfileCipher"/>) and can be kept anywhere; without one it is a
    /// plain zip that anyone who gets it can use.
    /// </summary>
    public void ExportProfile(string zipPath, string? passphrase = null)
    {
        if (passphrase != null && ProfileCipher.CheckPassphrase(passphrase) is { } why) throw new ArgumentException(why, nameof(passphrase));
        if (File.Exists(zipPath)) File.Delete(zipPath);
        try
        {
            Stream file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // The zip is written straight into the locking stream, so the plain backup never exists as a file.
            if (passphrase != null) file = ProfileCipher.Encrypt(file, passphrase);
            using (file) WriteProfileZip(file);
        }
        catch
        {
            // Not a half-written backup that looks like a real one.
            try { File.Delete(zipPath); } catch (IOException) { }
            throw;
        }
    }

    private Action<string>? _historySnapshot;

    /// <summary>
    /// Pictures (and sound files) that have been on this computer for longer than this are left out of a backup: the messages stay, and the pictures are
    /// asked for again from other members when someone scrolls back to them. How long a file has been here is when it was saved.
    /// </summary>
    public static readonly TimeSpan BackupAttachmentAge = TimeSpan.FromDays(183);

    private void WriteProfileZip(Stream destination)
    {
        string? snapshot = null;
        try
        {
            WriteProfileZip(destination, ref snapshot);
        }
        finally
        {
            if (snapshot != null) try { File.Delete(snapshot); } catch (IOException) { }
        }
    }

    private void WriteProfileZip(Stream destination, ref string? snapshot)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var databaseName = Path.GetFileName(HistoryDatabasePath);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Directory, file).Replace('\\', '/');
            if (!IsProfilePath(relative)) continue;
            // The database is written to while the app runs, so a copy of the file could catch a write half way (and a journal beside it is only half of
            // one): what goes into the backup is a consistent copy made by the database itself, and the files beside it are left out.
            var inHistory = relative.StartsWith("history/", StringComparison.OrdinalIgnoreCase);
            var name = Path.GetFileName(relative);
            if (inHistory && name.StartsWith(databaseName + "-", StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > BackupAttachmentAge) continue;
            var sourcePath = file;
            if (inHistory && string.Equals(name, databaseName, StringComparison.OrdinalIgnoreCase) && _historySnapshot != null)
            {
                snapshot = Path.Combine(Directory, "history.snapshot.tmp");
                _historySnapshot(snapshot);
                sourcePath = snapshot;
            }
            var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
            using var target = entry.Open();
            // A backup has to open on another computer, so the files sealed for this one go in as they are inside.
            if (SecretFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                target.Write(SecretStore.ReadAllBytes(file));
                continue;
            }
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            source.CopyTo(target);
        }
    }

    /// <summary>True if the backup file is locked with a passphrase, so <see cref="StageProfile"/> needs it.</summary>
    public static bool IsBackupLocked(string path) => ProfileCipher.IsLocked(path);

    /// <summary>
    /// Opens a backup into a staging folder. A locked backup needs its passphrase: a wrong one (or a damaged file) throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/>, and nothing is left behind.
    /// </summary>
    public string StageProfile(string zipPath, string? passphrase = null)
    {
        if (!ProfileCipher.IsLocked(zipPath)) return StagePlainProfile(zipPath);
        if (passphrase == null) throw new InvalidDataException("This backup is locked with a passphrase.");

        // The zip is read from a temporary copy, since reading one needs to jump around in it; the copy is gone as soon as it has been opened.
        var plain = Path.Combine(Path.GetTempPath(), "Yaparr-unlocked-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(plain, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                ProfileCipher.Decrypt(input, output, passphrase);
            return StagePlainProfile(plain);
        }
        finally { try { File.Delete(plain); } catch (IOException) { } }
    }

    private string StagePlainProfile(string zipPath)
    {
        var staging = Path.Combine(Path.GetTempPath(), "Yaparr-import-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            if (zip.Entries.Count > MaxRestoreEntries) throw new InvalidDataException("This archive has too many files to be a backup.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The archive contains an invalid path.");
                if (!IsProfilePath(entry.FullName)) continue;
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = File.Create(target);
                var buffer = new byte[81920];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > MaxRestoreBytes) throw new InvalidDataException("This archive is too big to be a backup.");
                    output.Write(buffer, 0, n);
                }
            }

            var identity = Path.Combine(staging, "identity.key");
            if (!File.Exists(identity) || !File.Exists(Path.Combine(staging, "settings.json")))
                throw new InvalidDataException("This is not a Yaparr profile export.");
            try
            {
                // A restored backup becomes another device of the account, not a second copy of the one it came from: the account is kept, and this
                // computer gets a device key of its own, vouched for by the account key, exactly as when a device is linked. So restoring the same
                // backup on two computers, or while the original is still in use, gives separate devices of one person and nothing clashes.
                using var restored = PeerIdentity.Import(File.ReadAllBytes(identity));
                if (restored.CanLinkDevices)
                {
                    using var fresh = restored.CreateDevice();
                    File.WriteAllBytes(identity, fresh.Export());
                }
            }
            catch (System.Security.Cryptography.CryptographicException) { throw new InvalidDataException("The identity key in this archive is not valid."); }
            return staging;
        }
        catch
        {
            System.IO.Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    /// <summary>The identity file as it is on disk (for what is kept in sync), or null if there is none.</summary>
    internal byte[]? ReadIdentityFile()
    {
        try { return File.Exists(IdentityPath) ? SecretStore.ReadAllBytes(IdentityPath) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>
    /// A staged profile made from the synced account alone: the identity (this computer gets a device key of its own, vouched for by the account key, exactly as when a backup is
    /// restored) and the name. Everything else comes through the usual sync once the app runs, so no history or settings are staged.
    /// </summary>
    public string StageFromAccount(byte[] identityFile, string? name)
    {
        var staging = Path.Combine(Path.GetTempPath(), "Yaparr-import-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            try
            {
                using var restored = PeerIdentity.Import(identityFile);
                using var fresh = restored.CanLinkDevices ? restored.CreateDevice() : null;
                File.WriteAllBytes(Path.Combine(staging, "identity.key"), (fresh ?? restored).Export());
            }
            catch (System.Security.Cryptography.CryptographicException) { throw new InvalidDataException("The identity key in the account is not valid."); }
            File.WriteAllText(Path.Combine(staging, "settings.json"), JsonSerializer.Serialize(new AppSettings { DisplayName = name }, Json));
            return staging;
        }
        catch
        {
            System.IO.Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public void ApplyStagedProfile(string staging)
    {
        if (File.Exists(IdentityPath)) File.Delete(IdentityPath);
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        if (File.Exists(AccountStatePath)) File.Delete(AccountStatePath);
        if (System.IO.Directory.Exists(HistoryDirectory)) System.IO.Directory.Delete(HistoryDirectory, recursive: true);
        var direct = Path.Combine(Directory, "direct");
        if (System.IO.Directory.Exists(direct)) System.IO.Directory.Delete(direct, recursive: true);

        foreach (var file in System.IO.Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Directory, Path.GetRelativePath(staging, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        ProtectSecrets();
        System.IO.Directory.Delete(staging, recursive: true);
    }

    public bool HasIdentity => File.Exists(IdentityPath);

    // Logging out. The running app holds files open and saves settings as it goes, so it does not delete anything itself: it leaves this
    // note (with its own process id) and restarts, and the next start waits for the old process to be gone and then removes the profile.
    private string LogOutNotePath => Path.Combine(Directory, "logout.pending");

    // What stays when a profile is removed: the programs and files that belong to no account.
    private static readonly string[] KeptOnLogOut = { "ffmpeg", "update", "logs", "logout.pending" };

    /// <summary>True when a log out was asked for and has not been finished.</summary>
    public bool LogOutPending => File.Exists(LogOutNotePath);

    /// <summary>Asks for this profile to be removed the next time Yaparr starts. Restart straight after.</summary>
    public void RequestLogOut() => File.WriteAllText(LogOutNotePath, Environment.ProcessId.ToString());

    /// <summary>
    /// Finishes a log out that was asked for: waits for the process that asked to be gone, then removes the identity key, rooms, history,
    /// pictures and settings (everything but <see cref="KeptOnLogOut"/>). Safe to call at every start; it does nothing without the note, and
    /// the note goes last, so a log out that was cut short is finished at the next start. Returns whether a profile was removed.
    /// </summary>
    public bool CompleteLogOut(int waitMilliseconds = 15_000)
    {
        if (!LogOutPending) return false;
        if (int.TryParse(File.ReadAllText(LogOutNotePath).Trim(), out var pid) && pid != Environment.ProcessId)
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(pid); old.WaitForExit(waitMilliseconds); }
            catch (ArgumentException) { }       // already gone
            catch (InvalidOperationException) { }
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(Directory))
                {
                    var name = Path.GetFileName(path);
                    if (KeptOnLogOut.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); else File.Delete(path);
                }
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file the old process still has open: give it a moment.
                if (attempt >= 20) throw;
                Thread.Sleep(250);
            }
        }
        File.Delete(LogOutNotePath);
        return true;
    }

    public string AccountStatePath => Path.Combine(Directory, "account.json");

    /// <summary>Profile pictures: yours and those of the people you have met.</summary>
    public string AvatarDirectory => Path.Combine(Directory, "avatars");

    public void SaveIdentity(PeerIdentity identity) => SecretStore.WriteAllBytes(IdentityPath, identity.Export());

    public PeerIdentity LoadOrCreateIdentity()
    {
        try
        {
            if (File.Exists(IdentityPath)) return PeerIdentity.Import(SecretStore.ReadAllBytes(IdentityPath));
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or ArgumentException or FormatException) { }

        // A file we could not read is kept aside, not written over: it may be the key to an account.
        try { if (File.Exists(IdentityPath)) File.Move(IdentityPath, IdentityPath + ".unreadable", overwrite: true); }
        catch (IOException) { }
        var identity = PeerIdentity.Generate();
        SaveIdentity(identity);
        return identity;
    }
}
