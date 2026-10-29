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
    public List<string> Rooms { get; set; } = new();
    public List<PrivateRoomSetting> PrivateRooms { get; set; } = new();

    /// <summary>Names of the microphone and speakers to use; empty means the system default.</summary>
    public string? VoiceInput { get; set; }
    public string? VoiceOutput { get; set; }
    /// <summary>Loudness (0 to 100) the microphone has to reach before voice activation sends.</summary>
    public int VoiceSensitivity { get; set; } = 12;
    /// <summary>Send all the time instead of only when talking.</summary>
    public bool VoiceAlwaysOn { get; set; }
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
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }
    public string HistoryDirectory => Path.Combine(Directory, "history");
    public string VpnConfigPath => Path.Combine(Directory, "vpn.conf");
    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string IdentityPath => Path.Combine(Directory, "identity.key");

    public AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    public void SaveSettings(AppSettings settings) =>
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));

    public void ExportProfile(string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
        {
            var entry = zip.CreateEntry(Path.GetRelativePath(Directory, file).Replace('\\', '/'), CompressionLevel.Optimal);
            using var target = entry.Open();
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
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The archive contains an invalid path.");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
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
        if (System.IO.Directory.Exists(HistoryDirectory)) System.IO.Directory.Delete(HistoryDirectory, recursive: true);

        foreach (var file in System.IO.Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Directory, Path.GetRelativePath(staging, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        System.IO.Directory.Delete(staging, recursive: true);
    }

    public PeerIdentity LoadOrCreateIdentity()
    {
        try
        {
            if (File.Exists(IdentityPath)) return PeerIdentity.Import(File.ReadAllBytes(IdentityPath));
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException) { }

        var identity = PeerIdentity.Generate();
        File.WriteAllBytes(IdentityPath, identity.ExportPrivateKey());
        return identity;
    }
}
