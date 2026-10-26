using System.IO;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Wpf;

public sealed class PrivateRoomSetting
{
    public string Label { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed class AppSettings
{
    public string? DisplayName { get; set; }
    public List<string> Rooms { get; set; } = new();
    public List<PrivateRoomSetting> PrivateRooms { get; set; } = new();
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
    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string IdentityPath => Path.Combine(Directory, "identity.key");

    public AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    public void SaveSettings(AppSettings settings) =>
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));

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
