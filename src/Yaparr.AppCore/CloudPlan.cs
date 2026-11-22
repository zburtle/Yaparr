using System.Globalization;

namespace Yaparr.AppCore;

/// <summary>
/// Where the account stands on payments at a Yaparr Cloud server (<c>GET /v1/plan</c>, see the Yaparr.Cloud README, "Paid plans"). With billing off on that server nothing is paid
/// for and nothing is shown. The app never knows who takes the payments.
/// </summary>
/// <param name="BillingOn">False: this server asks for nothing.</param>
/// <param name="State"><c>lifetime</c> (never asked to pay), <c>subscribed</c> or <c>lapsed</c>.</param>
/// <param name="EndsAt">The end of the paid period (subscribed), or when the last one ended (lapsed).</param>
/// <param name="WillRenew">False when it was set to end with the period.</param>
/// <param name="PastDue">A payment failed and is being retried: still paid for until the end of the period.</param>
public sealed record CloudPlan(bool BillingOn, string State, string? Plan, DateTimeOffset? EndsAt, bool WillRenew, bool PastDue)
{
    public static CloudPlan Off { get; } = new(false, "off", null, null, false, false);

    /// <summary>Billing is on and nothing covers the account: no sign-in on a new device and no syncing, nothing deleted.</summary>
    public bool Lapsed => BillingOn && State == "lapsed";
}

/// <summary>
/// The words for it. They hold no price, no "renew at …" and no address to pay at, so that the build for Google Play can show them (its payments policy rules those out; see
/// docs/billing.md, "Android and Google Play"): the state, and nothing that sells.
/// </summary>
public static class CloudPlanText
{
    /// <summary>What a server's <c>402</c> means, said the same everywhere the app can run into it.</summary>
    public const string Ended = "Subscription ended: syncing is paused and you can't sign in on a new device until it is renewed. Your data is kept.";

    /// <summary>One line under the sign-in, or null when there is nothing to say (billing off, or a lifetime account).</summary>
    public static string? Line(CloudPlan plan, DateTimeOffset now)
    {
        if (!plan.BillingOn || plan.State == "lifetime") return null;
        if (plan.State == "lapsed") return Ended;
        if (plan.PastDue) return "Payment failed: update your card" + Until(plan, now, ". Paid until ");
        if (plan.WillRenew) return "Active" + Until(plan, now, ", renews ");
        return "Ends" + Until(plan, now, " ");
    }

    private static string Until(CloudPlan plan, DateTimeOffset now, string lead)
    {
        if (plan.EndsAt is not { } end) return "";
        var local = end.ToLocalTime();
        var date = local.ToString(local.Year == now.ToLocalTime().Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);
        return lead + date;
    }
}
