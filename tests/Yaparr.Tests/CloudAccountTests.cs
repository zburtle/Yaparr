using System.Net;
using System.Text;
using Yaparr.AppCore;

namespace Yaparr.Tests;

public sealed class CloudAccountTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_usage_and_the_devices_are_read_with_the_devices_sign_in()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/usage" => Json("""{"kept":{"usedBytes":1000,"quotaBytes":536870912},"pictures":{"usedBytes":500,"quotaBytes":268435456,"keptDays":7},"backups":{"count":2,"bytes":4096},"gateway":{"enabled":true,"usedTodayBytes":2048,"perDayBytes":2147483648,"bytesPerSecond":24576}}"""),
            "/v1/devices" => Json("""{"devices":[{"id":"aaaaaaaaaaaaaaaa","name":"laptop","created":"2026-11-19T10:00:00+00:00","current":true},{"id":"bbbbbbbbbbbbbbbb","name":"phone","created":"2026-11-18T10:00:00+00:00","current":false}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new CloudAccountClient(new Uri("https://cloud.test"), "tok", new HttpClient(handler));

        var usage = await client.UsageAsync();
        Assert.Equal(1000, usage.Kept.UsedBytes);
        Assert.Equal(7, usage.Pictures.KeptDays);
        Assert.Equal(2, usage.Backups.Count);
        Assert.True(usage.Gateway.Enabled);

        var devices = await client.DevicesAsync();
        Assert.Equal(new[] { "laptop", "phone" }, devices.Select(d => d.Name));
        Assert.True(devices[0].Current);
        Assert.All(handler.Seen, r => Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task A_device_is_signed_out_by_its_id_and_a_server_that_has_forgotten_this_device_says_so()
    {
        var handler = new Handler(r => r.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json("""{"error":"This device is not signed in."}""", HttpStatusCode.Unauthorized));
        using var client = new CloudAccountClient(new Uri("https://cloud.test"), "tok", new HttpClient(handler));
        await client.RevokeDeviceAsync("bbbbbbbbbbbbbbbb");
        Assert.Equal("/v1/devices/bbbbbbbbbbbbbbbb", handler.Seen.Single().RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.Seen.Single().Method);

        var ex = await Assert.ThrowsAsync<BackupServerException>(() => client.UsageAsync());
        Assert.True(ex.SignedOut);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1000L, "1000 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(536_870_912L, "512 MB")]
    [InlineData(2_147_483_648L, "2 GB")]
    public void Sizes_are_written_as_people_read_them(long bytes, string text) => Assert.Equal(text, CloudUsageText.Bytes(bytes));

    [Fact]
    public void The_lines_say_how_full_each_thing_is_and_warn_when_it_is_nearly_full_or_used_up()
    {
        var calm = new CloudUsage(new StoreUse(1024, 536_870_912), new PictureUse(0, 268_435_456, 7), new BackupUse(0, 0), new GatewayUse(true, 1_048_576, 2_147_483_648, 24576));
        var lines = CloudUsageText.Lines(calm);
        Assert.Equal(4, lines.Count);
        Assert.All(lines, l => Assert.False(l.Warn));
        Assert.Contains("None yet", lines.Single(l => l.Label == "Backups").Value);
        Assert.Contains("1 MB of 2 GB", lines.Single(l => l.Label.StartsWith("Yaparr Cloud as gateway", StringComparison.Ordinal)).Value);

        var tight = new CloudUsage(new StoreUse(500_000_000, 536_870_912), new PictureUse(268_000_000, 268_435_456, 7), new BackupUse(2, 4096), new GatewayUse(true, 2_100_000_000, 2_147_483_648, 24576));
        var warned = CloudUsageText.Lines(tight);
        Assert.True(warned[0].Warn);
        Assert.Contains("Almost full", warned[0].Value);
        Assert.True(warned[1].Warn);
        Assert.True(warned[3].Warn);
        Assert.Contains("midnight UTC", warned[3].Value);

        var off = CloudUsageText.Lines(calm with { Gateway = new GatewayUse(false, 0, 0, 0) });
        Assert.Contains("Not offered", off[3].Value);
    }
}
