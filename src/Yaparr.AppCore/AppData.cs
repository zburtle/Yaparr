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
}

public sealed class AppSettings
{
    public List<KnownPeer> KnownPeers { get; set; } = new();
    public string? DisplayName { get; set; }
    public bool VpnEnabled { get; set; }

    /// <summary>When a message you send has a web link, fetch the page's title, description and picture and send them along as a card under the link. The readers never contact the site.</summary>
    public bool LinkPreviews { get; set; } = true;

    /// <summary>The computer app: closing the window leaves Yaparr running behind an icon in the notification area (the Exit menu item and the icon's Exit end it).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>The phone app looks for a newer version when it opens and offers it. This contacts GitHub, which sees the phone's address.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The version of the update the phone app offered last, so that the same one is not offered again at every start.</summary>
    public string? OfferedUpdate { get; set; }

    /// <summary>System, Light or Dark: the colours the app uses (the phone app; the computer follows its system).</summary>
    public string Theme { get; set; } = "System";

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
    /// <summary>Bring everyone you hear to a similar loudness (on top of each person's own volume).</summary>
    public bool VoiceLevelSpeakers { get; set; }

    /// <summary>People whose soundboard sounds you have silenced (their voice is not affected), by id.</summary>
    public List<string> SoundboardMuted { get; set; } = new();
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

    /// <summary>The phone app: stay connected when the app is not on the screen, so that messages and calls keep coming (shows a notification while it is on). Off until the person turns it on.</summary>
    public bool BackgroundConnection { get; set; }

    /// <summary>The phone app: the rooms (by id) that notify for every message, not only for mentions, replies and direct messages.</summary>
    public List<string> NotifyAllRooms { get; set; } = new();
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
    private static readonly string[] SecretFiles = { "identity.key", "settings.json", "account.json", "vpn.conf", "history.key" };

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
    public string HistoryDirectory => Path.Combine(Directory, "history");
    /// <summary>Where the GIF shelf (pictures kept to send again) lives.</summary>
    public string ShelfDirectory => Path.Combine(Directory, "gifshelf");
    public string VpnConfigPath => Path.Combine(Directory, "vpn.conf");
    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string IdentityPath => Path.Combine(Directory, "identity.key");

    public AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(SecretStore.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException) { return new(); }
    }

    public void SaveSettings(AppSettings settings) => SecretStore.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));

    public void ExportProfile(string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Directory, file).Replace('\\', '/');
            if (!IsProfilePath(relative)) continue;
            var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
            using var target = entry.Open();
            // A backup has to open on another computer, so the files sealed for this one go in as they are inside.
            if (SecretFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                target.Write(SecretStore.ReadAllBytes(file));
                continue;
            }
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            source.CopyTo(target);
        }
    }

    public string StageProfile(string zipPath)
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
