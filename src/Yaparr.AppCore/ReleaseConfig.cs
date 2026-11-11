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

    private static readonly Lazy<ReleaseConfig> Shipped = new(() => Load(AppContext.BaseDirectory));

    /// <summary>The config this program was released with.</summary>
    public static ReleaseConfig Current => Shipped.Value;

    /// <summary>Reads the file in a folder. A missing, unreadable or unusable file gives <see cref="None"/>, so a damaged release still starts.</summary>
    public static ReleaseConfig Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return None;
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return new ReleaseConfig { BackupServer = BackupServerClient.ParseServerAddress(model?.BackupServer) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return None;
        }
    }

    /// <summary>The address to put in a box: the one this person used before if there is one, otherwise the release's default (empty if neither).</summary>
    public string BackupServerToOffer(string? remembered) =>
        !string.IsNullOrWhiteSpace(remembered) ? remembered.Trim() : BackupServer?.ToString().TrimEnd('/') ?? "";
}
