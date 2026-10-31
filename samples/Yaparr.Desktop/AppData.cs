using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Desktop;

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
    public List<PrivateRoomSetting> PrivateRooms { get; set; } = new();

    /// <summary>Names of the microphone and speakers to use; empty means the system default.</summary>
    public string? VoiceInput { get; set; }
    public string? VoiceOutput { get; set; }
    /// <summary>Loudness (0 to 100) the microphone has to reach before voice activation sends.</summary>
    public int VoiceSensitivity { get; set; } = 12;
    /// <summary>Send all the time instead of only when talking.</summary>
    public bool VoiceAlwaysOn { get; set; }
    /// <summary>Stop other people hearing themselves through your speakers.</summary>
    public bool VoiceEchoCancellation { get; set; } = true;
    /// <summary>Off, Low, Moderate or High: how much steady background noise is taken out of your microphone.</summary>
    public string VoiceNoiseSuppression { get; set; } = "Moderate";

    /// <summary>People whose soundboard sounds you have silenced (their voice is not affected), by id.</summary>
    public List<string> SoundboardMuted { get; set; } = new();
    /// <summary>Silence everybody's soundboard sounds.</summary>
    public bool SoundboardsMuted { get; set; }
    /// <summary>How loud soundboard sounds are against voices, in percent (100 is as loud as a voice).</summary>
    public int SoundboardVolume { get; set; } = 80;

    /// <summary>Play a sound for new messages and for people joining or leaving your voice channel.</summary>
    public bool SoundsEnabled { get; set; } = true;
    /// <summary>Flash the taskbar button when a message arrives while the window is in the background.</summary>
    public bool FlashTaskbar { get; set; } = true;

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
    private static readonly string[] SecretFiles = { "identity.key", "settings.json", "account.json", "vpn.conf" };

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
    private static readonly string[] BackedUpFiles = { "identity.key", "settings.json", "account.json", "peers.json", "clocks.json", "network.json", "vpn.conf", "dht-nodes.txt" };
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
            try { PeerIdentity.Import(File.ReadAllBytes(identity)); }
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

    public string AccountStatePath => Path.Combine(Directory, "account.json");

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
