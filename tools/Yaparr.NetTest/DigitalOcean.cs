using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>The few DigitalOcean calls the tool needs: find your key, create machines, wait for them, delete them by tag.</summary>
internal sealed class DigitalOcean : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.digitalocean.com/v2/"), Timeout = TimeSpan.FromSeconds(60) };

    public DigitalOcean(string token)
    {
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Set the DIGITALOCEAN_TOKEN environment variable to a personal access token with read and write access.");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public sealed record DropletInfo(long Id, string Name, string Status, string? Ip, string Region);

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string message = text;
            try { message = JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text; } catch (JsonException) { }
            throw new InvalidOperationException($"DigitalOcean answered {(int)response.StatusCode}: {message}");
        }
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    public async Task<(int DropletLimit, string Email)> AccountAsync(CancellationToken ct)
    {
        var json = await ReadAsync(await _http.GetAsync("account", ct), ct);
        return (json["account"]?["droplet_limit"]?.GetValue<int>() ?? 0, json["account"]?["email"]?.GetValue<string>() ?? "");
    }

    public sealed record KeyInfo(long Id, string Name, string Fingerprint, string PublicKey);

    public async Task<List<KeyInfo>> ListKeysAsync(CancellationToken ct)
    {
        var json = await ReadAsync(await _http.GetAsync("account/keys?per_page=200", ct), ct);
        return json["ssh_keys"]!.AsArray().Select(k => new KeyInfo(
            k!["id"]!.GetValue<long>(), k["name"]!.GetValue<string>(), k["fingerprint"]?.GetValue<string>() ?? "", k["public_key"]?.GetValue<string>() ?? "")).ToList();
    }

    public async Task<KeyInfo> AddKeyAsync(string name, string publicKey, CancellationToken ct)
    {
        var body = new JsonObject { ["name"] = name, ["public_key"] = publicKey };
        var json = await ReadAsync(await _http.PostAsync("account/keys", JsonContent.Create(body), ct), ct);
        var k = json["ssh_key"]!;
        return new KeyInfo(k["id"]!.GetValue<long>(), k["name"]!.GetValue<string>(), k["fingerprint"]?.GetValue<string>() ?? "", k["public_key"]?.GetValue<string>() ?? publicKey);
    }

    public async Task<long> FindKeyAsync(string name, CancellationToken ct)
    {
        var json = await ReadAsync(await _http.GetAsync("account/keys?per_page=200", ct), ct);
        var keys = json["ssh_keys"]!.AsArray();
        var match = keys.FirstOrDefault(k => string.Equals(k!["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new InvalidOperationException($"No SSH key named '{name}' in your DigitalOcean account. Keys there: {string.Join(", ", keys.Select(k => k!["name"]))}.");
        return match["id"]!.GetValue<long>();
    }

    public async Task<DropletInfo> CreateAsync(string name, string region, string size, string image, long keyId, string[] tags, string userData, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = name, ["region"] = region, ["size"] = size, ["image"] = image,
            ["ssh_keys"] = new JsonArray(keyId), ["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()),
            ["user_data"] = userData, ["monitoring"] = false, ["ipv6"] = false,
        };
        var json = await ReadAsync(await _http.PostAsync("droplets", JsonContent.Create(body), ct), ct);
        return Parse(json["droplet"]!);
    }

    public async Task<DropletInfo> GetAsync(long id, CancellationToken ct) =>
        Parse((await ReadAsync(await _http.GetAsync($"droplets/{id}", ct), ct))["droplet"]!);

    public async Task<List<DropletInfo>> ListByTagAsync(string tag, CancellationToken ct)
    {
        var json = await ReadAsync(await _http.GetAsync($"droplets?tag_name={Uri.EscapeDataString(tag)}&per_page=200", ct), ct);
        return json["droplets"]!.AsArray().Select(d => Parse(d!)).ToList();
    }

    public async Task DeleteByTagAsync(string tag, CancellationToken ct)
    {
        var response = await _http.DeleteAsync($"droplets?tag_name={Uri.EscapeDataString(tag)}", ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent) await ReadAsync(response, ct);
    }

    /// <summary>Waits until the machine is running and has a public address.</summary>
    public async Task<DropletInfo> WaitActiveAsync(long id, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            var d = await GetAsync(id, ct);
            if (d.Status == "active" && d.Ip != null) return d;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Droplet {d.Name} was not active after 5 minutes.");
            await Task.Delay(3000, ct);
        }
    }

    private static DropletInfo Parse(JsonNode d) => new(
        d["id"]!.GetValue<long>(), d["name"]!.GetValue<string>(), d["status"]!.GetValue<string>(),
        d["networks"]?["v4"]?.AsArray().FirstOrDefault(n => n!["type"]?.GetValue<string>() == "public")?["ip_address"]?.GetValue<string>(),
        d["region"]?["slug"]?.GetValue<string>() ?? "");

    public void Dispose() => _http.Dispose();
}
