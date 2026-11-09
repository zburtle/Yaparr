using System.Text.Json;

namespace Yaparr.Updates;

/// <summary>A newer Android package that was published, with what the phone needs to fetch it and check it.</summary>
public sealed record AndroidUpdate(Version Version, string Tag, string Notes, string Name, string DownloadUrl, string Sha256, long Size);

/// <summary>
/// Finds the newest Android release among a repository's releases. The Android app is released on its own (tags such as <c>android-v1.58.1</c>, and never
/// as the "latest" release, which belongs to the computer app), so the list of releases is read, not the latest one.
/// </summary>
public static class AndroidReleases
{
    public const string TagPrefix = "android-v";

    /// <summary>
    /// The newest release newer than <paramref name="current"/> that has a package with a published checksum from the repository's own release downloads,
    /// or null. A release without a checksum, with a package from anywhere else, or that is a draft or a pre-release is not offered.
    /// </summary>
    public static AndroidUpdate? Newest(string json, Version current, string repository)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        AndroidUpdate? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;
            if (Flag(release, "draft") || Flag(release, "prerelease")) continue;
            var tag = Text(release, "tag_name");
            if (tag == null || !TryParseTag(tag, out var version) || version <= current || (best != null && version <= best.Version)) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name") ?? "";
                if (!name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) continue;
                var digest = Text(asset, "digest");
                if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) continue;
                var sha = digest["sha256:".Length..];
                var url = Text(asset, "browser_download_url");
                if (!UpdateVerifier.IsSha256(sha) || !UpdateVerifier.IsTrustedDownload(url, repository)) continue;
                long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                best = new AndroidUpdate(version, tag, Text(release, "body") ?? "", name, url!, sha, size);
                break;
            }
        }
        return best;
    }

    /// <summary>The version in a tag such as <c>android-v1.58.1</c>; false for any other tag (the computer app's <c>v1.59.2</c> among them).</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal)) return false;
        return TryParseVersion(tag[TagPrefix.Length..], out version);
    }

    /// <summary>A version as the app reports it (<c>1.58.1</c>, possibly with a suffix such as <c>-beta</c>), always with three parts.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var end = text.IndexOfAny(['+', '-']);
        if (end >= 0) text = text[..end];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
