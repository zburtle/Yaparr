using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>How this device is woken for push notifications.</summary>
public enum PushRoute
{
    /// <summary>Not at all: the app only hears of messages while it is connected.</summary>
    None,

    /// <summary>Through UnifiedPush: a distributor app on the phone (ntfy, say) holds the connection and wakes Yaparr.</summary>
    UnifiedPush,

    /// <summary>Through Firebase Cloud Messaging, on phones with Google's services and no distributor.</summary>
    Fcm,
}

/// <summary>
/// What this device remembers about its push registration: how it is woken, where, and the wake address it gives to the people in its rooms. Sealed like the
/// other files with secrets, and not part of a backup (a restored profile on another phone registers again).
/// </summary>
public sealed class PushState
{
    /// <summary>Names this device to the server (a random id made once).</summary>
    public string DeviceId { get; set; } = "";

    public PushRoute Route { get; set; }

    /// <summary>The UnifiedPush address the distributor gave, or Firebase's device token.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The distributor's package (UnifiedPush), to show which one is in use.</summary>
    public string Distributor { get; set; } = "";

    /// <summary>The server the registration is on, and the wake address it gave (what the people in the rooms are told).</summary>
    public string Server { get; set; } = "";

    public string WakeAddress { get; set; } = "";

    public bool Registered => Route != PushRoute.None && Endpoint.Length > 0 && WakeAddress.Length > 0;
}

public sealed record PushOptions(bool UnifiedPush, bool Fcm);

/// <summary>
/// The push side of a Yaparr Cloud server (see its README, "Push notifications"): registering this device, getting a new wake address, and asking for someone's
/// phone to be woken. Registering needs the device to be signed in to the server (the same token as for backups); waking does not.
/// </summary>
public sealed class PushClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _token;
    private readonly Uri _server;

    public PushClient(Uri server, string token, HttpClient? http = null)
    {
        _server = server;
        _token = token;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_server, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        try
        {
            var response = await _http.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await BackupServerClient.FailureAsync(response, cancel);
                response.Dispose();
                throw failure;
            }
            return response;
        }
        catch (HttpRequestException ex) { throw new BackupServerException("Couldn't reach the server: " + ex.Message, inner: ex); }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested) { throw new BackupServerException("The server took too long to answer.", inner: ex); }
    }

    private sealed record OptionsJson(bool UnifiedPush, bool Fcm);
    private sealed record TokenJson(string? Token);

    /// <summary>Which ways of waking a phone the server has. A server without push (an older one) answers that it has none.</summary>
    public async Task<PushOptions> OptionsAsync(CancellationToken cancel = default)
    {
        try
        {
            using var response = await SendAsync(Request(HttpMethod.Get, "/v1/push/options"), cancel);
            var options = await response.Content.ReadFromJsonAsync<OptionsJson>(BackupServerClient.Json, cancel);
            return new PushOptions(options?.UnifiedPush ?? false, options?.Fcm ?? false);
        }
        catch (BackupServerException ex) when (ex.Status == HttpStatusCode.NotFound) { return new PushOptions(false, false); }
    }

    /// <summary>
    /// Registers this device (or changes where it is woken). A new device is given its wake token, and the wake address made from it is returned; a device the
    /// server already knows keeps its token, which this device must have kept, and null comes back for it (see <see cref="PushSetup"/>).
    /// </summary>
    public async Task<string?> RegisterAsync(string deviceId, PushRoute route, string endpoint, CancellationToken cancel = default)
    {
        var request = Request(HttpMethod.Put, "/v1/push");
        request.Content = JsonContent.Create(new { deviceId, kind = route == PushRoute.Fcm ? "fcm" : "unifiedpush", endpoint }, options: BackupServerClient.Json);
        using var response = await SendAsync(request, cancel);
        var token = (await response.Content.ReadFromJsonAsync<TokenJson>(BackupServerClient.Json, cancel))?.Token;
        return string.IsNullOrEmpty(token) ? null : WakeAddress.For(_server, token);
    }

    /// <summary>A new wake address, when the old one has reached someone who should not have it (the old one stops working at once).</summary>
    public async Task<string> RotateAsync(string deviceId, CancellationToken cancel = default)
    {
        using var response = await SendAsync(Request(HttpMethod.Post, "/v1/push/" + Uri.EscapeDataString(deviceId) + "/rotate"), cancel);
        var token = (await response.Content.ReadFromJsonAsync<TokenJson>(BackupServerClient.Json, cancel))?.Token;
        return string.IsNullOrEmpty(token) ? throw new BackupServerException("The server gave no new address.") : WakeAddress.For(_server, token);
    }

    /// <summary>Takes this device off push at the server. Not finding it there is not a failure.</summary>
    public async Task UnregisterAsync(string deviceId, CancellationToken cancel = default)
    {
        try { (await SendAsync(Request(HttpMethod.Delete, "/v1/push/" + Uri.EscapeDataString(deviceId)), cancel)).Dispose(); }
        catch (BackupServerException ex) when (ex.Status == HttpStatusCode.NotFound) { }
    }

    private static readonly HttpClient SharedWakeClient = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 16 * 1024 };

    /// <summary>
    /// Asks for the phone behind a wake address to be woken: a POST to the address with nothing in it. No sign-in is needed, the address is the permission. A
    /// <c>404</c> or <c>410</c> says nobody is there any more.
    /// </summary>
    public static async Task<WakeResult> WakeAsync(string address, HttpClient? http = null, CancellationToken cancel = default)
    {
        if (!WakeAddress.TryParse(address, out var uri)) return WakeResult.Gone;
        try
        {
            using var response = await (http ?? SharedWakeClient).PostAsync(uri, null, cancel);
            if (response.IsSuccessStatusCode) return WakeResult.Sent;
            return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone ? WakeResult.Gone : WakeResult.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return WakeResult.Failed; }
    }
}

/// <summary>What registering this device came to, to keep in <see cref="PushState"/> and tell the rooms.</summary>
public static class PushSetup
{
    /// <summary>
    /// Makes this device's registration at the server match how it is woken now. A device that is new (or whose address was lost) gets a wake address; one that
    /// is registered already keeps it, only where it is woken changes. Returns the state to keep: the address to give the rooms is in
    /// <see cref="PushState.WakeAddress"/>. Throws <see cref="BackupServerException"/> when the server cannot be reached or says no.
    /// </summary>
    public static async Task<PushState> EnsureAsync(PushClient client, Uri server, PushState current, PushRoute route, string endpoint, string distributor, CancellationToken cancel = default)
    {
        var state = new PushState
        {
            DeviceId = current.DeviceId.Length > 0 ? current.DeviceId : Guid.NewGuid().ToString("N"),
            Route = route,
            Endpoint = endpoint,
            Distributor = distributor,
            Server = server.ToString(),
            WakeAddress = current.Server == server.ToString() && current.DeviceId.Length > 0 ? current.WakeAddress : "",
        };
        var made = await client.RegisterAsync(state.DeviceId, route, endpoint, cancel);
        if (made != null) state.WakeAddress = made;
        // The server knows this device but this phone lost the address it was given (cleared data restored from a backup, say): ask for a new one.
        else if (state.WakeAddress.Length == 0) state.WakeAddress = await client.RotateAsync(state.DeviceId, cancel);
        return state;
    }

    /// <summary>Takes this device off push at the server (when it can be reached) and returns the state to keep: nothing.</summary>
    public static async Task<PushState> TurnOffAsync(PushClient? client, PushState current, CancellationToken cancel = default)
    {
        try { if (client != null && current.DeviceId.Length > 0) await client.UnregisterAsync(current.DeviceId, cancel); }
        catch (BackupServerException) { /* the server forgets a device whose address stops working */ }
        return new PushState { DeviceId = current.DeviceId };
    }
}
