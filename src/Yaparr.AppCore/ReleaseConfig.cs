using System.Text.Json;

namespace Yaparr.AppCore;

/// <summary>
/// What a release ships with, in <c>yaparr.release.json</c> beside the program (the installer, the Flatpak and the .deb all carry it, since each
/// is made from the same published folder). It holds defaults only: today the address of the backup server a person is offered, so they do not
/// type it. It is read, never written, and nothing in the app needs it: with the file missing or unreadable there is simply no default, and a
/// person can type any other address as before.
/// <code>{ "backupServer": "https://cloud.yaparr.com" }</code>
/// </summary>
public sealed class ReleaseConfig
{
    public const string FileName = "yaparr.release.json";

    /// <summary>The backup server offered by default, as <see cref="BackupServerClient.ParseServerAddress"/> would read it, or null if there is none.</summary>
    public Uri? BackupServer { get; init; }

    public static readonly ReleaseConfig None = new();

    private sealed record FileModel(string? BackupServer);

    private static ReleaseConfig? s_current;

    /// <summary>The config this program was released with: the file beside it, or what the platform gave to <see cref="Use"/> (Android keeps it in the package).</summary>
    public static ReleaseConfig Current => s_current ??= Load(AppContext.BaseDirectory);

    /// <summary>For a platform where the file cannot sit beside the program: sets what <see cref="Current"/> is.</summary>
    public static void Use(ReleaseConfig config) => s_current = config;

    /// <summary>Reads the file in a folder. A missing, unreadable or unusable file gives <see cref="None"/>, so a damaged release still starts.</summary>
    public static ReleaseConfig Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return None;
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return None;
        }
    }

    /// <summary>Reads the text of the file. Anything that is not usable (not JSON, too big, a server that is not https) gives <see cref="None"/>.</summary>
    public static ReleaseConfig Parse(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024) return None;
            var model = JsonSerializer.Deserialize<FileModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return new ReleaseConfig { BackupServer = BackupServerClient.ParseServerAddress(model?.BackupServer) };
        }
        catch (JsonException)
        {
            return None;
        }
    }

    /// <summary>The address to put in a box: the one this person used before if there is one, otherwise the release's default (empty if neither).</summary>
    public string BackupServerToOffer(string? remembered) =>
        !string.IsNullOrWhiteSpace(remembered) ? remembered.Trim() : BackupServer?.ToString().TrimEnd('/') ?? "";
}
