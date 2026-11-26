using System.Net;
using System.Text;
using Yaparr.AppCore;

namespace Yaparr.Tests;

public class JellyfinClientTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();
        public readonly Dictionary<string, (HttpStatusCode Status, string Body)> Answers = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            var key = request.Method + " " + request.RequestUri!.PathAndQuery;
            foreach (var (prefix, answer) in Answers)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static JellyfinClient Client(Fake fake, string token = "", string user = "") =>
        new(new Uri("http://home:8096"), "device-1", token, user, new HttpClient(fake));

    [Fact]
    public void A_typed_address_is_tried_with_https_and_then_http_unless_it_says_which()
    {
        Assert.Equal(new[] { "https://jelly.example/", "http://jelly.example/" }, JellyfinClient.AddressesFor(" jelly.example ").Select(u => u.ToString()));
        Assert.Equal(new[] { "http://192.168.1.5:8096/" }, JellyfinClient.AddressesFor("http://192.168.1.5:8096/").Select(u => u.ToString()));
        Assert.Equal(new[] { "https://x.org/jellyfin/" }, JellyfinClient.AddressesFor("https://x.org/jellyfin").Select(u => u.ToString()));
        Assert.Empty(JellyfinClient.AddressesFor("  "));
        Assert.Empty(JellyfinClient.AddressesFor("ftp://x.org"));
    }

    [Fact]
    public async Task Every_request_says_who_the_device_is_and_after_signing_in_carries_the_token()
    {
        var fake = new Fake();
        fake.Answers["GET /System/Info/Public"] = (HttpStatusCode.OK, """{"ServerName":"Home","Version":"10.10.3","Id":"abc"}""");
        fake.Answers["POST /Users/AuthenticateByName"] = (HttpStatusCode.OK, """{"AccessToken":"tok123","User":{"Id":"u1","Name":"zach"}}""");
        fake.Answers["GET /UserViews"] = (HttpStatusCode.OK, """{"Items":[]}""");
        using var client = Client(fake);

        var info = await client.GetPublicInfoAsync();
        Assert.Equal(("Home", "10.10.3"), (info.Name, info.Version));
        var login = await client.SignInAsync("zach", "secret");
        Assert.Equal(("u1", "zach", "tok123"), (login.UserId, login.UserName, login.Token));
        await client.GetLibrariesAsync();

        Assert.DoesNotContain("Token=", fake.Seen[0].Headers.GetValues("Authorization").Single());
        Assert.Contains("DeviceId=\"device-1\"", fake.Seen[0].Headers.GetValues("Authorization").Single());
        Assert.Contains("Token=\"tok123\"", fake.Seen[2].Headers.GetValues("Authorization").Single());
        Assert.Contains("userId=u1", fake.Seen[2].RequestUri!.Query);
    }

    [Fact]
    public async Task A_wrong_password_is_said_plainly_and_something_that_is_not_jellyfin_is_not_taken_for_one()
    {
        var fake = new Fake();
        fake.Answers["POST /Users/AuthenticateByName"] = (HttpStatusCode.Unauthorized, "");
        fake.Answers["GET /System/Info/Public"] = (HttpStatusCode.OK, """{"hello":"world"}""");
        using var client = Client(fake);
        var wrong = await Assert.ThrowsAsync<JellyfinException>(() => client.SignInAsync("zach", "nope"));
        Assert.Equal("The name or the password is wrong.", wrong.Message);
        await Assert.ThrowsAsync<JellyfinException>(() => client.GetPublicInfoAsync());
    }

    [Fact]
    public async Task Quick_connect_gives_a_code_waits_for_the_answer_and_signs_in_with_the_secret()
    {
        var fake = new Fake();
        fake.Answers["POST /QuickConnect/Initiate"] = (HttpStatusCode.OK, """{"Code":"123456","Secret":"s3cret"}""");
        fake.Answers["GET /QuickConnect/Connect?secret=s3cret"] = (HttpStatusCode.OK, """{"Authenticated":false}""");
        using var client = Client(fake);
        var ticket = await client.StartQuickConnectAsync();
        Assert.Equal(("123456", "s3cret"), (ticket.Code, ticket.Secret));
        Assert.False(await client.QuickConnectApprovedAsync(ticket.Secret));

        fake.Answers["GET /QuickConnect/Connect?secret=s3cret"] = (HttpStatusCode.OK, """{"Authenticated":true}""");
        fake.Answers["POST /Users/AuthenticateWithQuickConnect"] = (HttpStatusCode.OK, """{"AccessToken":"t","User":{"Id":"u","Name":"n"}}""");
        Assert.True(await client.QuickConnectApprovedAsync(ticket.Secret));
        Assert.Equal("t", (await client.SignInWithQuickConnectAsync(ticket.Secret)).Token);
    }

    [Fact]
    public async Task Libraries_with_nothing_to_watch_are_left_out_and_items_are_read_with_their_details()
    {
        var fake = new Fake();
        fake.Answers["GET /UserViews"] = (HttpStatusCode.OK, """{"Items":[{"Id":"1","Name":"Films","CollectionType":"movies"},{"Id":"2","Name":"Music","CollectionType":"music"},{"Id":"3","Name":"Shows","CollectionType":"tvshows"}]}""");
        fake.Answers["GET /Items?"] = (HttpStatusCode.OK, """
            {"TotalRecordCount":90,"Items":[
              {"Id":"m1","Name":"Alien","Type":"Movie","ProductionYear":1979,"RunTimeTicks":71400000000,"Overview":"In space.","ImageTags":{"Primary":"tagA"},"UserData":{"PlayedPercentage":25.5}},
              {"Id":"s1","Name":"Severance","Type":"Series","ProductionYear":2022},
              {"Name":"no id","Type":"Movie"}]}
            """);
        using var client = Client(fake, "t", "u1");
        var libraries = await client.GetLibrariesAsync();
        Assert.Equal(new[] { "Films", "Shows" }, libraries.Select(l => l.Name));

        var page = await client.GetItemsAsync("1", "ali en", start: 60);
        Assert.Equal(90, page.Total);
        Assert.Equal(2, page.Items.Count);
        var alien = page.Items[0];
        Assert.Equal((JellyfinKind.Movie, 1979, 119, "tagA", 25.5), (alien.Kind, alien.Year, alien.RuntimeMinutes, alien.ImageTag, alien.PlayedPercent));
        Assert.Equal("1979", alien.Subtitle);
        Assert.Equal(JellyfinKind.Series, page.Items[1].Kind);
        var query = fake.Seen.Last().RequestUri!.Query;
        Assert.Contains("parentId=1", query);
        Assert.Contains("searchTerm=ali%20en", query);
        Assert.Contains("startIndex=60", query);
    }

    [Fact]
    public async Task An_episode_says_where_it_belongs_and_continue_watching_falls_back_to_the_older_address()
    {
        var fake = new Fake();
        fake.Answers["GET /Users/u1/Items/Resume"] = (HttpStatusCode.OK, """{"Items":[{"Id":"e1","Name":"Hello","Type":"Episode","SeriesName":"Severance","SeriesId":"s1","ParentIndexNumber":1,"IndexNumber":2}]}""");
        using var client = Client(fake, "t", "u1");
        var page = await client.GetResumeAsync();   // GET /UserItems/Resume is a 404 here
        var episode = Assert.Single(page.Items);
        Assert.Equal("S1E2 · Severance", episode.Subtitle);
        Assert.Equal("s1", episode.SeriesId);
    }

    [Fact]
    public async Task A_token_the_server_no_longer_accepts_is_reported_as_such()
    {
        var fake = new Fake();
        fake.Answers["GET /UserViews"] = (HttpStatusCode.Unauthorized, "");
        using var client = Client(fake, "old", "u1");
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => client.GetLibrariesAsync());
        Assert.True(ex.Unauthorized);
    }

    [Fact]
    public async Task A_poster_is_fetched_only_for_an_item_that_has_one()
    {
        var fake = new Fake();
        fake.Answers["GET /Items/m1/Images/Primary"] = (HttpStatusCode.OK, "PNG");
        using var client = Client(fake, "t", "u1");
        Assert.Null(await client.GetPosterAsync("m1", null, 200));
        Assert.Equal("PNG", Encoding.UTF8.GetString((await client.GetPosterAsync("m1", "tagA", 200))!));
        Assert.Null(await client.GetPosterAsync("m2", "tagB", 200));   // 404
    }

    [Fact]
    public void The_sign_in_is_kept_sealed_on_this_device_and_left_out_of_a_backup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-jellyfin-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new AppData(dir);
            Assert.False(data.LoadJellyfin().SignedIn);
            data.SaveJellyfin(new JellyfinState { Url = "http://home:8096/", ServerName = "Home", UserId = "u1", UserName = "zach", Token = "tok-secret-123", DeviceId = "d" });

            var back = data.LoadJellyfin();
            Assert.True(back.SignedIn);
            Assert.Equal(("Home", "zach", "tok-secret-123"), (back.ServerName, back.UserName, back.Token));
            // The token is not readable in the file, and a backup of the profile does not carry it.
            Assert.DoesNotContain("tok-secret-123", File.ReadAllText(Path.Combine(dir, "jellyfin.json")));
            var zip = Path.Combine(dir, "backup.zip");
            data.ExportProfile(zip);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("jellyfin", StringComparison.OrdinalIgnoreCase));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
