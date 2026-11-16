using System.Net;
using System.Text.Json;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Tests;

public class PushClientTests
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";
    private static readonly Uri Server = new("https://cloud.example.org/");

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, string? Auth)> Seen { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel), request.Headers.Authorization?.ToString()));
            return Answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json) };

    [Fact]
    public async Task A_new_device_registers_with_a_signed_in_token_and_gets_a_wake_address()
    {
        var handler = new Handler { Answer = _ => Json(HttpStatusCode.Created, $"{{\"token\":\"{Token}\"}}") };
        using var client = new PushClient(Server, "sign-in-token", new HttpClient(handler));
        var address = await client.RegisterAsync("dev1", PushRoute.UnifiedPush, "https://ntfy.example.org/up123");
        Assert.Equal("https://cloud.example.org/v1/push/wake/" + Token, address);
        Assert.True(WakeAddress.TryParse(address, out _));
        var seen = Assert.Single(handler.Seen);
        Assert.Equal((HttpMethod.Put, "/v1/push", "Bearer sign-in-token"), (seen.Method, seen.Path, seen.Auth));
        var body = JsonDocument.Parse(seen.Body).RootElement;
        Assert.Equal("dev1", body.GetProperty("deviceId").GetString());
        Assert.Equal("unifiedpush", body.GetProperty("kind").GetString());
        Assert.Equal("https://ntfy.example.org/up123", body.GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task A_device_the_server_knows_gets_no_new_address()
    {
        var handler = new Handler { Answer = _ => Json(HttpStatusCode.OK, "{\"token\":null}") };
        using var client = new PushClient(Server, "t", new HttpClient(handler));
        Assert.Null(await client.RegisterAsync("dev1", PushRoute.Fcm, "fcm-token"));
        Assert.Equal("fcm", JsonDocument.Parse(handler.Seen[0].Body).RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Setup_keeps_the_address_it_has_when_only_where_the_phone_is_woken_changes()
    {
        var handler = new Handler { Answer = _ => Json(HttpStatusCode.OK, "{\"token\":null}") };
        using var client = new PushClient(Server, "t", new HttpClient(handler));
        var before = new PushState { DeviceId = "dev1", Route = PushRoute.UnifiedPush, Endpoint = "https://ntfy.example.org/old", Server = Server.ToString(), WakeAddress = "https://cloud.example.org/v1/push/wake/" + Token };
        var after = await PushSetup.EnsureAsync(client, Server, before, PushRoute.UnifiedPush, "https://ntfy.example.org/new", "io.heckel.ntfy");
        Assert.Equal(before.WakeAddress, after.WakeAddress);
        Assert.Equal("https://ntfy.example.org/new", after.Endpoint);
        Assert.Equal("dev1", after.DeviceId);
        Assert.True(after.Registered);
        Assert.Single(handler.Seen);   // no rotation needed
    }

    [Fact]
    public async Task Setup_asks_for_a_new_address_when_the_server_knows_the_device_and_the_phone_lost_it()
    {
        var handler = new Handler
        {
            Answer = request => request.RequestUri!.AbsolutePath.EndsWith("/rotate") ? Json(HttpStatusCode.OK, $"{{\"token\":\"{Token}\"}}") : Json(HttpStatusCode.OK, "{\"token\":null}"),
        };
        using var client = new PushClient(Server, "t", new HttpClient(handler));
        var lost = new PushState { DeviceId = "dev1" };
        var state = await PushSetup.EnsureAsync(client, Server, lost, PushRoute.Fcm, "tok", "");
        Assert.Equal("https://cloud.example.org/v1/push/wake/" + Token, state.WakeAddress);
        Assert.Equal(new[] { "/v1/push", "/v1/push/dev1/rotate" }, handler.Seen.Select(s => s.Path));
    }

    [Fact]
    public async Task Setup_for_another_server_does_not_carry_the_old_address_over_and_makes_a_device_id_when_there_is_none()
    {
        var handler = new Handler { Answer = _ => Json(HttpStatusCode.Created, $"{{\"token\":\"{Token}\"}}") };
        using var client = new PushClient(Server, "t", new HttpClient(handler));
        var elsewhere = new PushState { DeviceId = "dev1", Server = "https://other.example.org/", WakeAddress = "https://other.example.org/v1/push/wake/" + Token };
        var state = await PushSetup.EnsureAsync(client, Server, elsewhere, PushRoute.UnifiedPush, "https://ntfy.example.org/x", "d");
        Assert.StartsWith("https://cloud.example.org/", state.WakeAddress);

        var fresh = await PushSetup.EnsureAsync(client, Server, new PushState(), PushRoute.UnifiedPush, "https://ntfy.example.org/x", "d");
        Assert.Equal(32, fresh.DeviceId.Length);
    }

    [Fact]
    public async Task The_server_refusing_is_told_in_its_words_and_a_signed_out_device_is_noticed()
    {
        var handler = new Handler { Answer = _ => Json(HttpStatusCode.Conflict, "{\"error\":\"This account already has 5 devices.\"}") };
        using var client = new PushClient(Server, "t", new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => client.RegisterAsync("d", PushRoute.UnifiedPush, "https://ntfy.example.org/x"));
        Assert.Equal("This account already has 5 devices.", ex.Message);

        handler.Answer = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        Assert.True((await Assert.ThrowsAsync<BackupServerException>(() => client.RegisterAsync("d", PushRoute.UnifiedPush, "https://ntfy.example.org/x"))).SignedOut);

        handler.Answer = _ => throw new HttpRequestException("down");
        Assert.Contains("reach", (await Assert.ThrowsAsync<BackupServerException>(() => client.RegisterAsync("d", PushRoute.UnifiedPush, "https://ntfy.example.org/x"))).Message);
    }

    [Fact]
    public async Task A_server_without_push_says_so_and_turning_off_is_quiet_when_the_server_cannot_be_reached()
    {
        var handler = new Handler { Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        using var client = new PushClient(Server, "t", new HttpClient(handler));
        Assert.Equal(new PushOptions(false, false), await client.OptionsAsync());

        handler.Answer = _ => Json(HttpStatusCode.OK, "{\"unifiedPush\":true,\"fcm\":true}");
        Assert.Equal(new PushOptions(true, true), await client.OptionsAsync());

        handler.Answer = _ => throw new HttpRequestException("down");
        var off = await PushSetup.TurnOffAsync(client, new PushState { DeviceId = "dev1", Route = PushRoute.Fcm, Endpoint = "x", WakeAddress = "y" });
        Assert.False(off.Registered);
        Assert.Equal("dev1", off.DeviceId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, WakeResult.Sent)]
    [InlineData(HttpStatusCode.NotFound, WakeResult.Gone)]
    [InlineData(HttpStatusCode.Gone, WakeResult.Gone)]
    [InlineData(HttpStatusCode.TooManyRequests, WakeResult.Failed)]
    [InlineData(HttpStatusCode.BadGateway, WakeResult.Failed)]
    public async Task Waking_says_what_came_of_it_and_sends_nothing_with_it(HttpStatusCode answer, WakeResult expected)
    {
        var handler = new Handler { Answer = _ => new HttpResponseMessage(answer) };
        Assert.Equal(expected, await PushClient.WakeAsync("https://cloud.example.org/v1/push/wake/" + Token, new HttpClient(handler)));
        var seen = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("", seen.Body);
        Assert.Null(seen.Auth);   // no sign-in goes to whoever the address leads to
    }

    [Fact]
    public async Task Waking_an_address_that_is_not_one_asks_nobody_and_a_server_that_is_down_is_a_failure()
    {
        var handler = new Handler();
        Assert.Equal(WakeResult.Gone, await PushClient.WakeAsync("http://127.0.0.1/v1/push/wake/" + Token, new HttpClient(handler)));
        Assert.Empty(handler.Seen);
        handler.Answer = _ => throw new HttpRequestException("down");
        Assert.Equal(WakeResult.Failed, await PushClient.WakeAsync("https://cloud.example.org/v1/push/wake/" + Token, new HttpClient(handler)));
    }

    [Fact]
    public void The_registration_is_kept_sealed_and_comes_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-push-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new AppData(dir);
            Assert.False(data.LoadPush().Registered);
            data.SavePush(new PushState { DeviceId = "dev1", Route = PushRoute.UnifiedPush, Endpoint = "https://ntfy.example.org/x", WakeAddress = "https://cloud.example.org/v1/push/wake/" + Token, Server = "https://cloud.example.org/" });
            var back = data.LoadPush();
            Assert.True(back.Registered);
            Assert.Equal("dev1", back.DeviceId);
            Assert.DoesNotContain("ntfy.example.org", File.ReadAllText(Path.Combine(dir, "push.json")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
