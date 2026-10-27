using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace Yaparr.Wpf;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string Name, string DownloadUrl, string? Sha256);

public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    public static string? Repository
    {
        get
        {
            var repo = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
            return string.IsNullOrWhiteSpace(repo) ? null : repo;
        }
    }

    public static Version CurrentVersion
    {
        get
        {
            var text = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return TryParse(text, out var v) ? v : new Version(0, 0, 0);
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var repo = Repository;
        if (repo is null) return null;

        using var response = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (!TryParse(tag, out var version) || version <= CurrentVersion) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;

            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d["sha256:".Length..];

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateInfo(version, tag!, notes, name, asset.GetProperty("browser_download_url").GetString()!, sha);
        }
        return null;
    }

    public static async Task<string> DownloadAsync(UpdateInfo update, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Yaparr-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(update.Name));

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, ct);
        }

        if (update.Sha256 is not null)
        {
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("The downloaded installer failed its checksum.");
            }
        }
        return path;
    }

    public static void Install(string msiPath)
    {
        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\"") { UseShellExecute = false });
        Application.Current.Shutdown();
    }

    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['+', '-']);
        if (end >= 0) text = text[..end];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Yaparr-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
