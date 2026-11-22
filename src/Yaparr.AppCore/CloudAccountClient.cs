using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;

namespace Yaparr.AppCore;

public sealed record StoreUse(long UsedBytes, long QuotaBytes);

public sealed record PictureUse(long UsedBytes, long QuotaBytes, int KeptDays);

public sealed record BackupUse(int Count, long Bytes);

public sealed record GatewayUse(bool Enabled, long UsedTodayBytes, long PerDayBytes, long BytesPerSecond);

/// <summary>What the account holds on a Yaparr Cloud server, against its limits (<c>GET /v1/usage</c>, see the Yaparr.Cloud README, "Devices and usage").</summary>
public sealed record CloudUsage(StoreUse Kept, PictureUse Pictures, BackupUse Backups, GatewayUse Gateway);

/// <summary>A device signed in as the account. <see cref="Current"/> is the one that asked.</summary>
public sealed record CloudDevice(string Id, string Name, DateTimeOffset Created, bool Current);

/// <summary>
/// What a signed-in device can ask a Yaparr Cloud server about its own account: what it holds against its limits, and which devices are signed in (and signing one out). Failures
/// are <see cref="BackupServerException"/> (<c>SignedOut</c> when the server no longer knows this device).
/// </summary>
public sealed class CloudAccountClient : IDisposable
{
    private sealed record DevicesJson(List<CloudDevice>? Devices);

    private readonly Uri _server;
    private readonly string _token;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public CloudAccountClient(Uri server, string token, HttpClient? http = null)
    {
        _server = server;
        _token = token;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancel)
    {
        var request = new HttpRequestMessage(method, new Uri(_server, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try
        {
            var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            var failure = await BackupServerClient.FailureAsync(response, cancel).ConfigureAwait(false);
            response.Dispose();
            throw failure;
        }
        catch (HttpRequestException ex) { throw new BackupServerException("Couldn't reach the server: " + ex.Message, inner: ex); }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested) { throw new BackupServerException("The server took too long to answer.", inner: ex); }
    }

    public async Task<CloudUsage> UsageAsync(CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/usage", cancel).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<CloudUsage>(BackupServerClient.Json, cancel).ConfigureAwait(false)
               ?? throw new BackupServerException("The server answered with nothing.");
    }

    /// <summary>Where the account stands on payments. A server with billing off (or one too old to know) says nothing is asked: <see cref="CloudPlan.Off"/>.</summary>
    public async Task<CloudPlan> PlanAsync(CancellationToken cancel = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "/v1/plan", cancel).ConfigureAwait(false);
            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(BackupServerClient.Json, cancel).ConfigureAwait(false);
            if (json.ValueKind != System.Text.Json.JsonValueKind.Object || Text(json, "billing") != "on") return CloudPlan.Off;
            return new CloudPlan(true, Text(json, "state") ?? "lapsed", Text(json, "plan"),
                DateTimeOffset.TryParse(Text(json, "endsAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) ? end : null,
                Flag(json, "willRenew"), Flag(json, "pastDue"));
        }
        catch (BackupServerException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { return CloudPlan.Off; }   // a server from before paid plans
        catch (System.Text.Json.JsonException) { return CloudPlan.Off; }
    }

    private static string? Text(System.Text.Json.JsonElement from, string name) =>
        from.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(System.Text.Json.JsonElement from, string name) =>
        from.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

    public async Task<IReadOnlyList<CloudDevice>> DevicesAsync(CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/devices", cancel).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<DevicesJson>(BackupServerClient.Json, cancel).ConfigureAwait(false);
        return body?.Devices ?? new List<CloudDevice>();
    }

    /// <summary>Signs a device out (by its id). The device may be this one.</summary>
    public async Task RevokeDeviceAsync(string id, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, "/v1/devices/" + Uri.EscapeDataString(id), cancel).ConfigureAwait(false);
    }
}

/// <summary>One line of the storage and limits screen: what it is about, how it stands, how full (0 to 1, or null when it has no limit), and whether it needs the person's eye.</summary>
public sealed record UsageLine(string Label, string Value, double? Fraction = null, bool Warn = false);

/// <summary>The words of the storage and limits screen, the same on both apps.</summary>
public static class CloudUsageText
{
    /// <summary>A size as people read it: 0 B, 12 KB, 3.4 MB, 1.2 GB.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double value = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int unit = -1;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (value >= 100 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture)) + " " + units[unit];
    }

    private static double Fraction(long used, long limit) => limit <= 0 ? 0 : Math.Clamp((double)used / limit, 0, 1);

    public static IReadOnlyList<UsageLine> Lines(CloudUsage usage)
    {
        var kept = Fraction(usage.Kept.UsedBytes, usage.Kept.QuotaBytes);
        var pictures = Fraction(usage.Pictures.UsedBytes, usage.Pictures.QuotaBytes);
        var lines = new List<UsageLine>
        {
            new("Kept in sync", $"{Bytes(usage.Kept.UsedBytes)} of {Bytes(usage.Kept.QuotaBytes)}" + (kept >= 0.9 ? ". Almost full: new history and shelves stop syncing when it is." : "") + " (message history, emoji and GIFs)", kept, kept >= 0.9),
            new("Left for people who are away", $"{Bytes(usage.Pictures.UsedBytes)} of {Bytes(usage.Pictures.QuotaBytes)}, kept {usage.Pictures.KeptDays} days", pictures, pictures >= 0.9),
            new("Backups", usage.Backups.Count == 0 ? "None yet" : $"{usage.Backups.Count} saved, {Bytes(usage.Backups.Bytes)}"),
        };
        if (usage.Gateway.Enabled)
        {
            var day = Fraction(usage.Gateway.UsedTodayBytes, usage.Gateway.PerDayBytes);
            lines.Add(new("Yaparr Cloud as gateway, today",
                $"{Bytes(usage.Gateway.UsedTodayBytes)} of {Bytes(usage.Gateway.PerDayBytes)}" + (day >= 0.9 ? ". Nearly used up for today; it starts again at midnight UTC." : "")
                + " (only used when a network blocks UDP)", day, day >= 0.9));
        }
        else lines.Add(new("Yaparr Cloud as gateway", "Not offered by this server"));
        return lines;
    }
}
