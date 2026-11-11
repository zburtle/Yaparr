using System.Net;
using System.Text;
using System.Text.Json;
using Yaparr.AppCore;
using Yaparr.Desktop;

namespace Yaparr.Tests;

public sealed class BackupSignInLinkTests
{
    private const string Code = "Zm9yLXRlc3RpbmctYS1vbmUtdGltZS1jb2RlLTEyMzQ";   // 43 characters of base64url, as the server makes them
    private static readonly string Link = "yaparr://backup-signin/" + Code + "/" + Uri.EscapeDataString("https://cloud.yaparr.com");

    // ---- the link ----

    [Fact]
    public void The_button_on_the_servers_page_makes_a_link_the_app_understands()
    {
        var link = DeepLink.Parse(Link);
        Assert.NotNull(link);
        Assert.Equal(DeepLinkKind.BackupSignIn, link!.Kind);
        Assert.True(link.TryGetBackupSignIn(out var code, out var server));
        Assert.Equal(Code, code);
        Assert.Equal("https://cloud.yaparr.com/", server.ToString());
    }

    [Fact]
    public void A_link_survives_being_handed_from_one_copy_of_the_app_to_another()
    {
        var link = DeepLink.Parse(Link)!;
        Assert.Equal(Link, link.ToAppUri());
        Assert.Equal(link, DeepLink.Parse(link.ToAppUri()));
        Assert.Equal(link, DeepLink.FromArgs(["--data", "x", Link]));
    }

    [Theory]
    [InlineData("https://cloud.yaparr.com:8443", "https://cloud.yaparr.com:8443/")]
    [InlineData("http://localhost:5000", "http://localhost:5000/")]
    [InlineData("cloud.yaparr.com", "https://cloud.yaparr.com/")]
    [InlineData("https://cloud.yaparr.com/some/path?x=1", "https://cloud.yaparr.com/")]    // nothing after the host survives
    public void The_server_is_a_bare_address(string typed, string expected)
    {
        var link = DeepLink.Parse($"yaparr://backup-signin/{Code}/{Uri.EscapeDataString(typed)}");
        Assert.NotNull(link);
        Assert.True(link!.TryGetBackupSignIn(out _, out var server));
        Assert.Equal(expected, server.ToString());
    }

    [Theory]
    [InlineData("http://cloud.yaparr.com")]                 // a one-time code does not cross the internet in the clear
    [InlineData("ftp://cloud.yaparr.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    [InlineData("")]
    public void A_server_that_cannot_be_used_makes_no_link(string server) =>
        Assert.Null(DeepLink.Parse($"yaparr://backup-signin/{Code}/{Uri.EscapeDataString(server)}"));

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces in it and is long enough to pass the length")]
    [InlineData("<script>alert(1)</script>-----------------")]
    public void A_code_that_is_not_the_shape_the_server_makes_is_refused(string code) =>
        Assert.Null(DeepLink.Parse($"yaparr://backup-signin/{code}/{Uri.EscapeDataString("https://cloud.yaparr.com")}"));

    [Fact]
    public void Only_the_app_form_exists_and_nothing_malformed_or_oversized_gets_through()
    {
        // There is no page on yaparr.com for it: a shared web link cannot sign anyone in.
        Assert.Null(DeepLink.Parse("https://yaparr.com/backup-signin#" + Code + "/" + Uri.EscapeDataString("https://cloud.yaparr.com")));
        Assert.Null(DeepLink.Parse("yaparr://backup-signin"));
        Assert.Null(DeepLink.Parse("yaparr://backup-signin/"));
        Assert.Null(DeepLink.Parse("yaparr://backup-signin/" + Code));                       // no server
        Assert.Null(DeepLink.Parse("yaparr://backup-signin/" + Code + "/%zz"));              // a bad escape
        Assert.Null(DeepLink.Parse(Link + new string('a', 400)));                            // too long to be one of ours
        Assert.NotNull(DeepLink.Parse("YAPARR://BACKUP-SIGNIN/" + Code + "/" + Uri.EscapeDataString("https://cloud.yaparr.com")));   // the scheme is not case sensitive
    }

    [Fact]
    public void Other_links_are_not_taken_for_it_and_it_is_not_taken_for_them()
    {
        Assert.False(DeepLink.Parse("yaparr://chat/" + new string('a', 64))!.TryGetBackupSignIn(out _, out _));
        Assert.Equal(DeepLinkKind.Chat, DeepLink.Parse("yaparr://chat/" + new string('a', 64))!.Kind);
        Assert.NotEqual(DeepLinkKind.BackupSignIn, DeepLink.Parse("yaparr://join/" + Yaparr.Chat.RoomKey.Generate())!.Kind);
    }

    // ---- trading the code ----

    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public string? RequestPath, RequestBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            RequestPath = request.RequestUri!.PathAndQuery;
            RequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancel);
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_code_is_traded_for_a_token_and_nothing_else_is_sent()
    {
        var server = new FakeServer(_ => Json(HttpStatusCode.OK, new { token = "ycl_abc", username = "sam@example.com" }));
        var (token, username) = await BackupServerClient.TradeHandoffCodeAsync(new Uri("https://cloud.yaparr.com"), Code, new HttpClient(server));
        Assert.Equal("ycl_abc", token);
        Assert.Equal("sam@example.com", username);
        Assert.Equal("/v1/token/handoff", server.RequestPath);
        Assert.Equal($$"""{"code":"{{Code}}"}""", server.RequestBody);
    }

    [Fact]
    public async Task A_code_the_server_refuses_gives_its_own_words()
    {
        var server = new FakeServer(_ => Json(HttpStatusCode.BadRequest, new { error = "That sign-in has expired or was already used. Sign in from Yaparr's settings instead." }));
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => BackupServerClient.TradeHandoffCodeAsync(new Uri("https://cloud.yaparr.com"), Code, new HttpClient(server)));
        Assert.Contains("expired or was already used", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_or_answers_nothing_useful_is_reported_plainly()
    {
        var down = new FakeServer(_ => throw new HttpRequestException("connection refused"));
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => BackupServerClient.TradeHandoffCodeAsync(new Uri("https://cloud.yaparr.com"), Code, new HttpClient(down)));
        Assert.StartsWith("Couldn't reach the backup server", ex.Message);

        var empty = new FakeServer(_ => Json(HttpStatusCode.OK, new { username = "sam@example.com" }));   // no token
        await Assert.ThrowsAsync<BackupServerException>(() => BackupServerClient.TradeHandoffCodeAsync(new Uri("https://cloud.yaparr.com"), Code, new HttpClient(empty)));
    }
}
