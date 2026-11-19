using System.Net;
using System.Net.Http.Headers;

namespace Yaparr.AppCore;

/// <summary>What the server said to a piece that was put.</summary>
public enum KeepPutResult
{
    Stored,
    /// <summary>The account's room for kept pieces is used up (or the piece is too big): nothing was stored, and trying again will not help until something is removed.</summary>
    Full,
    /// <summary>Not stored for another reason (too many calls lately); a later run tries again.</summary>
    Refused,
}

/// <summary>The three calls of the kept-pieces part of a Yaparr Cloud server (its README, "Kept pieces"). A piece is already locked; its id is the SHA-256 of the locked bytes.</summary>
public interface IKeepTransport
{
    Task<KeepPutResult> PutAsync(string id, byte[] locked, CancellationToken cancel = default);

    /// <summary>The piece, or null when the server has none by that id.</summary>
    Task<byte[]?> GetAsync(string id, CancellationToken cancel = default);

    Task DeleteAsync(string id, CancellationToken cancel = default);
}

/// <summary><see cref="IKeepTransport"/> over HTTP, with this device's sign-in. A sign-in the server no longer knows is a <see cref="BackupServerException"/> with <c>SignedOut</c>.</summary>
public sealed class KeepClient : IKeepTransport, IDisposable
{
    // What the server takes (KeepMaxBytes, 16 MiB); a download is not read past it.
    private const long MaxBytes = 16_777_216 + 1024;
    private readonly Uri _server;
    private readonly string _token;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public KeepClient(Uri server, string token, HttpClient? http = null)
    {
        _server = server;
        _token = token;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    private HttpRequestMessage Request(HttpMethod method, string id)
    {
        var request = new HttpRequestMessage(method, new Uri(_server, "/v1/keep/" + id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static void ThrowIfSignedOut(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new BackupServerException("This device is no longer signed in.", HttpStatusCode.Unauthorized);
    }

    public async Task<KeepPutResult> PutAsync(string id, byte[] locked, CancellationToken cancel = default)
    {
        using var request = Request(HttpMethod.Put, id);
        request.Content = new ByteArrayContent(locked);
        try
        {
            using var response = await _http.SendAsync(request, cancel);
            ThrowIfSignedOut(response);
            if (response.IsSuccessStatusCode) return KeepPutResult.Stored;
            return response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge ? KeepPutResult.Full : KeepPutResult.Refused;
        }
        catch (HttpRequestException ex) { throw new BackupServerException("Could not reach the server: " + ex.Message, null, ex); }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested) { throw new BackupServerException("The server took too long.", null, ex); }
    }

    public async Task<byte[]?> GetAsync(string id, CancellationToken cancel = default)
    {
        using var request = Request(HttpMethod.Get, id);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            ThrowIfSignedOut(response);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new BackupServerException($"The server said {(int)response.StatusCode}.", response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new BackupServerException("A piece was larger than the server allows.", response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync(cancel);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
            {
                if (body.Length + read > MaxBytes) throw new BackupServerException("A piece was larger than the server allows.", response.StatusCode);
                body.Write(buffer, 0, read);
            }
            return body.ToArray();
        }
        catch (HttpRequestException ex) { throw new BackupServerException("Could not reach the server: " + ex.Message, null, ex); }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested) { throw new BackupServerException("The server took too long.", null, ex); }
    }

    public async Task DeleteAsync(string id, CancellationToken cancel = default)
    {
        using var request = Request(HttpMethod.Delete, id);
        try
        {
            using var response = await _http.SendAsync(request, cancel);
            ThrowIfSignedOut(response);
        }
        catch (HttpRequestException ex) { throw new BackupServerException("Could not reach the server: " + ex.Message, null, ex); }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested) { throw new BackupServerException("The server took too long.", null, ex); }
    }
}
