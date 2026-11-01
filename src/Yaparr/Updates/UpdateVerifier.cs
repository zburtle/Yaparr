using System.Security.Cryptography;

namespace Yaparr.Updates;

/// <summary>The checks an update has to pass before it is run: where it came from, and that what arrived is what was published.</summary>
public static class UpdateVerifier
{
    /// <summary>True for an https address under the repository's own release downloads on GitHub, and nowhere else.</summary>
    public static bool IsTrustedDownload(string? url, string repository)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               && uri.IsDefaultPort
               && uri.UserInfo.Length == 0
               && uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for 64 hexadecimal digits, the form of a SHA-256 checksum.</summary>
    public static bool IsSha256(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);

    /// <summary>Checks a file against the published checksum. A file that does not match is deleted and an exception thrown.</summary>
    public static async Task VerifyAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        string actual;
        // The file has to be closed again before it can be deleted.
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        if (actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(path);
        throw new InvalidDataException("The downloaded installer failed its checksum, so it was not used.");
    }
}
