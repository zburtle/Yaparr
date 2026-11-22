using System.Net;
using System.Text;
using Yaparr.AppCore;

namespace Yaparr.Tests;

public sealed class CloudPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

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

    private static async Task<CloudPlan> Ask(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var client = new CloudAccountClient(new Uri("https://cloud.test"), "tok", new HttpClient(new Handler(_ => Json(body, status))));
        return await client.PlanAsync();
    }

    [Fact]
    public async Task A_server_with_billing_off_or_from_before_paid_plans_asks_for_nothing()
    {
        Assert.False((await Ask("""{"billing":"off"}""")).BillingOn);
        Assert.False((await Ask("""{"error":"not found"}""", HttpStatusCode.NotFound)).BillingOn);
        Assert.False((await Ask("not json at all")).BillingOn);
        Assert.Null(CloudPlanText.Line(CloudPlan.Off, Now));
    }

    [Fact]
    public async Task The_plan_is_read_with_the_devices_sign_in()
    {
        var handler = new Handler(_ => Json("""{"billing":"on","state":"subscribed","plan":"cloud","endsAt":"2026-12-03T10:00:00+00:00","willRenew":true}"""));
        using var client = new CloudAccountClient(new Uri("https://cloud.test"), "tok", new HttpClient(handler));
        var plan = await client.PlanAsync();

        Assert.True(plan.BillingOn);
        Assert.Equal("subscribed", plan.State);
        Assert.Equal("cloud", plan.Plan);
        Assert.Equal(new DateTimeOffset(2026, 12, 3, 10, 0, 0, TimeSpan.Zero), plan.EndsAt);
        Assert.True(plan.WillRenew);
        Assert.False(plan.PastDue);       // a server that does not say is not failing
        Assert.False(plan.Lapsed);
        Assert.Equal("/v1/plan", handler.Seen[0].RequestUri!.AbsolutePath);
        Assert.Equal("Bearer tok", handler.Seen[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_signed_out_device_is_still_told_so()
    {
        using var client = new CloudAccountClient(new Uri("https://cloud.test"), "tok", new HttpClient(new Handler(_ => Json("""{"error":"no"}""", HttpStatusCode.Unauthorized))));
        Assert.True((await Assert.ThrowsAsync<BackupServerException>(() => client.PlanAsync())).SignedOut);
    }

    [Fact]
    public async Task Each_state_has_one_line_and_a_lifetime_account_has_none()
    {
        var ends = "2026-12-03T10:00:00+00:00";
        Assert.Null(CloudPlanText.Line(await Ask("""{"billing":"on","state":"lifetime"}"""), Now));

        var active = CloudPlanText.Line(await Ask($$"""{"billing":"on","state":"subscribed","plan":"cloud","endsAt":"{{ends}}","willRenew":true}"""), Now)!;
        Assert.StartsWith("Active, renews ", active);
        Assert.Contains("3", active);

        var ending = CloudPlanText.Line(await Ask($$"""{"billing":"on","state":"subscribed","plan":"cloud","endsAt":"{{ends}}","willRenew":false}"""), Now)!;
        Assert.StartsWith("Ends ", ending);

        var failing = CloudPlanText.Line(await Ask($$"""{"billing":"on","state":"subscribed","plan":"cloud","endsAt":"{{ends}}","willRenew":false,"pastDue":true}"""), Now)!;
        Assert.StartsWith("Payment failed: update your card", failing);

        var lapsed = await Ask("""{"billing":"on","state":"lapsed","plan":"cloud","endsAt":"2026-11-01T00:00:00+00:00","willRenew":false}""");
        Assert.True(lapsed.Lapsed);
        Assert.Equal(CloudPlanText.Ended, CloudPlanText.Line(lapsed, Now));
    }

    [Fact]
    public async Task No_line_holds_a_price_or_an_address_or_a_word_that_sells_so_the_play_build_can_show_all_of_them()
    {
        var lines = new List<string>();
        foreach (var body in new[]
        {
            """{"billing":"on","state":"subscribed","endsAt":"2026-12-03T10:00:00+00:00","willRenew":true}""",
            """{"billing":"on","state":"subscribed","endsAt":"2026-12-03T10:00:00+00:00","willRenew":false}""",
            """{"billing":"on","state":"subscribed","endsAt":"2026-12-03T10:00:00+00:00","willRenew":true,"pastDue":true}""",
            """{"billing":"on","state":"lapsed"}""",
        })
            lines.Add(CloudPlanText.Line(await Ask(body), Now)!);
        foreach (var line in lines)
        {
            Assert.DoesNotContain("$", line);
            Assert.DoesNotContain("http", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("subscribe", line.Replace("Subscription ended", ""), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("buy", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("price", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_402_is_a_subscription_that_has_ended_with_the_servers_own_sentence()
    {
        using var client = new CloudAccountClient(new Uri("https://cloud.test"), "tok", new HttpClient(new Handler(_ =>
            Json("""{"error":"This account's Yaparr Cloud subscription has ended.","code":"subscription_required"}""", HttpStatusCode.PaymentRequired))));
        var failure = await Assert.ThrowsAsync<BackupServerException>(() => client.UsageAsync());
        Assert.True(failure.SubscriptionEnded);
        Assert.False(failure.SignedOut);
        Assert.Contains("subscription has ended", failure.Message);
    }

    [Fact]
    public void The_pause_after_a_402_lasts_hours_ends_when_the_plan_is_covered_and_never_touches_another_note()
    {
        var state = new BackupServerState { Url = "https://cloud.test", Token = "t" };
        Assert.False(state.SubscriptionPaused(Now));

        state.PauseForSubscription(Now);
        Assert.Equal(CloudPlanText.Ended, state.AutoNote);
        Assert.True(state.SubscriptionPaused(Now.AddHours(5)));
        Assert.False(state.SubscriptionPaused(Now.AddHours(7)));      // asks again after the pause

        state.PauseForSubscription(Now);
        state.SubscriptionResolved();
        Assert.False(state.SubscriptionPaused(Now));
        Assert.Equal("", state.AutoNote);
        Assert.Null(state.RetryAfter);

        // The Sync key having been replaced is the more useful note to keep.
        state.AutoNote = AccountCloudSync.KeyChangedNote;
        state.PauseForSubscription(Now);
        Assert.Equal(AccountCloudSync.KeyChangedNote, state.AutoNote);
        state.AutoNote = "";

        // Someone else's note and back-off are not the subscription's to clear.
        state.AutoNote = "Couldn't sync: the server took too long to answer. Trying again later.";
        state.RetryAfter = Now.AddMinutes(10);
        state.SubscriptionResolved();
        Assert.StartsWith("Couldn't sync", state.AutoNote);
        Assert.Equal(Now.AddMinutes(10), state.RetryAfter);
    }

    [Fact]
    public void The_page_for_the_subscription_is_on_the_server()
    {
        Assert.Equal("https://cloud.test/account/subscription", BackupServerClient.SubscriptionPage(new Uri("https://cloud.test")).ToString());
    }

    [Fact]
    public async Task While_the_subscription_is_paused_the_account_sync_does_not_ask_the_server_at_all_and_the_settings_ending_the_pause_lets_it_through()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new AppData(dir);
            var state = new BackupServerState { Url = "https://cloud.test", Username = "sam@example.com", Token = "ycl_x", RecoveryCode = "K7QD-M3XF-9PLR-2WTE-HV8N-C4YB" };
            state.PauseForSubscription(DateTimeOffset.UtcNow);
            data.SaveBackupServer(state);
            var handler = new Handler(_ => Json("""{"error":"no"}""", HttpStatusCode.PaymentRequired));

            // The node is not used before the gate answers.
            Assert.Equal(AccountSyncResult.Failed, await AccountCloudSync.RunAsync(data, null!, new HttpClient(handler)));
            Assert.Empty(handler.Seen);

            var resolved = data.LoadBackupServer();
            resolved.SubscriptionResolved();
            Assert.False(resolved.SubscriptionPaused(DateTimeOffset.UtcNow));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
