using System.Net;
using System.Net.Http.Headers;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>
/// The pictures part of a Yaparr Cloud server (its README, "Blobs"): <see cref="IBlobRelay"/> for the node. Uploading needs this device to be signed in (the same token as for
/// backups); fetching does not, so it carries no token and the server cannot tie a read to an account.
/// </summary>
public sealed class BlobRelayClient : IBlobRelay
{
    // The largest locked file the server takes (a picture of 8 MiB, plus the lock); a download is not read past it.
    private const long MaxBytes = 9_437_184;
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly Uri _server;
    private readonly string _token;
    private readonly HttpClient _http;

    public BlobRelayClient(Uri server, string token, HttpClient? http = null)
    {
        _server = server;
        _token = token;
        _http = http ?? Shared;
        Server = server.GetLeftPart(UriPartial.Authority);
    }

    public string Server { get; }

    public async Task<bool> UploadAsync(string id, byte[] lockedBytes, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_server, "/v1/blob/" + id)) { Content = new ByteArrayContent(lockedBytes) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try
        {
            using var response = await _http.SendAsync(request, cancel);
            if (response.IsSuccessStatusCode) return true;
            // Signed out: the sign-in is cleared by the next mailbox run. Over the quota, too big, refused: not worth trying again.
            return false;
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task<byte[]?> DownloadAsync(string id, CancellationToken cancel = default)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_server, "/v1/blob/" + id), HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaxBytes) return null;
            using var stream = await response.Content.ReadAsStreamAsync(cancel);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
            {
                if (body.Length + read > MaxBytes) return null;
                body.Write(buffer, 0, read);
            }
            return body.ToArray();
        }
        catch (HttpRequestException) { return null; }
    }
}
