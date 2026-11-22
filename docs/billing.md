# Subscriptions for Yaparr Cloud (design)

Status: **the server side is built and was tried in Polar's sandbox; the apps and the website are not.** It says how a paid plan for Yaparr Cloud would work, with [Polar](https://polar.sh) taking the payments and the design kept generic enough that Stripe (or anyone else) can replace it by writing one adapter. The server is [Yaparr.Cloud](https://github.com/zburtle/Yaparr.Cloud); the website is [Yaparr.Web](https://github.com/zburtle/Yaparr.Web). See [Yaparr Cloud](guides/yaparr-cloud.md) for what the service does and [Mailbox](mailbox.md) for the rules it already follows.

## What is sold, and what is not

- **Chat is free and stays free.** Yaparr works fully with no account anywhere ([Mailbox](mailbox.md#what-yaparr-cloud-is-and-is-not): nothing in the client may depend on the cloud or be held back without it). A subscription is for the *service*: Yaparr Cloud's sync, kept history, mailbox and pictures for people who are away, push wake-ups and the UDP gateway, which cost real storage and bandwidth.
- **One plan, `cloud`**: **$4 a month or $40 a year** (the yearly price is two months free). No trial. More plans can be added without changing the design: a plan is a name and a set of limits.
- **No free tier of the cloud.** Accounts are still made by hand or by signup code today ([README](https://github.com/zburtle/Yaparr.Cloud#accounts-a-password-to-sign-in-a-sync-key-to-open-what-is-stored)); that stays, and a signup code keeps giving the `lifetime` pass it gives now, so the friends who already have an account are untouched, and **every account that exists stays free for life**. Anyone given one of those codes later is free for life too, and an admin can give any account the pass (see [Lifetime passes](#lifetime-passes)).

## Principles

1. **Billing is optional in the server and off by default.** A self-hosted Yaparr Cloud never has it, the apps work with a server that does not, and `GET /v1/plan` on such a server says billing is off (the apps show nothing). Only a deployment that sets `Service__Billing__Provider` has it. The AGPL code is all in the open; the secrets are not in the code.
2. **The provider is an adapter, not a concept.** The server reasons about an **entitlement** (this account may use the cloud until this date). Polar's products, customers and event names appear in exactly one place, `PolarBilling`. Everything else, the database, the apps, the website, the admin portal, would be identical under Stripe.
3. **An entitlement is a lease.** What a subscription grants is "until the end of the period paid for, and the grace days", and the server decides from that date. A missed webhook can then never cause a wrong lapse or a free ride for long: the lease runs out on its own, and a renewal only extends it.
4. **Webhooks say "look", they do not say "what".** A webhook is verified, de-duplicated, and then the server **asks the provider for the subscription's current state** and stores that. Order, repeats and the exact meaning of each event name (which differ between providers and change between versions) stop mattering.
5. **The provider's id for a person is ours.** Each account gets a random `billing_id`; that is what the provider stores as its customer reference (`external_customer_id` on Polar). The account's name (its email address) is never the key, and the provider never learns anything else about the account.
6. **The apps never know the provider.** They ask the server for the plan, and for a page to manage it; the server redirects to Polar. Nothing in an app, the website or a guide needs to change when the provider does.
7. **Yaparr emails only the links a person asked for** (to choose a password and to reset one; see [email](email.md)), never marketing and never a notice. Receipts, failed-payment and renewal notices come from the provider, which is the seller and already has the address. Yaparr shows the state in the app (below).

## The pieces

```
 yaparr.com/subscribe ──POST email──▶ cloud.yaparr.com /billing/start ──▶ IBillingProvider.StartCheckout ──▶ Polar checkout
                                                                                                                 │ pays
 Polar ──webhook (signed)──▶ /billing/webhook/polar ──▶ verify, de-duplicate, GetSubscription ──▶ store ◀────────┘
                                                                                                    │
 browser back to /billing/done?checkout_id=… ──▶ entitlement active? ──▶ choose a password (the existing sign-up page)
                                                                                                    │
 apps: GET /v1/plan  ·  PUT /v1/backup, /v1/sync, … ──▶ PlanGate: writable? ──▶ 402 when the lease has run out
```

## The model (provider-neutral)

```csharp
// One row for each subscription the account has had. The server's rules read only these.
record Subscription(
    string Account,                 // the account name (email)
    string Provider,                // "polar", later "stripe"
    string ProviderCustomerId,
    string ProviderSubscriptionId,
    string Plan,                    // "cloud"
    SubscriptionStatus Status,      // Active | PastDue | Canceling | Ended
    DateTimeOffset PeriodEnd,       // the end of the period paid for; the grace days are added when it is asked, not stored
    DateTimeOffset UpdatedAt);
```

| Provider's word (Polar) | Our status | Meaning |
|---|---|---|
| `active` | `Active` | paid, renews |
| `active` with cancel-at-period-end | `Canceling` | paid until `PeriodEnd`, then ends |
| `past_due` | `PastDue` | a payment failed and is being retried; still has access |
| `canceled` (after the period), `revoked`, `unpaid`, `incomplete_expired` | `Ended` | no access once it is `Ended`, whatever its dates say |

`PeriodEnd` is the provider's `current_period_end`. What an account is allowed is decided from it when asked: a subscription **covers** now unless it is `Ended` or now is more than `Billing__GraceDays` (3) past its `PeriodEnd`, so a payment that lands a day late does not lock anyone out, the date the app shows is the real end of the period, and changing the grace days applies to subscriptions already stored. `Ended` is how a refund or a chargeback ends access at once (`Ended` gives nothing, whatever its dates say).

**What an account is, now** (`AccountPlan.Of(account, now)`; the only place that decides):

| State | When | Signing in on a device | Syncing and the cloud's services | Taking data away, deleting, the account pages |
|---|---|---|---|---|
| `Lifetime` | the account's `pass` is `lifetime` (everyone so far, and anyone given the pass) | yes | yes | yes |
| `Subscribed` | some subscription covers now (not `Ended`, and not more than the grace days past its `PeriodEnd`) | yes | yes | yes |
| `Lapsed` | had a subscription, none is valid now | **no** | **no** (`402`) | yes |

"Signing in" is what makes a device token (`/login` for an app, `/v1/token`, the hand-off after sign-up): a lapsed account cannot make one, so no new device and no re-sign-in. "Syncing and the cloud's services" is everything the subscription pays for: `GET` and `PUT /v1/sync`, `PUT /v1/backup`, `GET` and `POST /v1/mailbox/{id}`, `PUT /v1/blob/{id}`, `PUT /v1/keep/{id}`, `PUT /v1/push`, and the gateway. All of those answer a lapsed account `402` `{"error": "<a sentence for people>", "code": "subscription_required"}`; a wake for its push token answers as an unknown token does. **Nothing is ever removed**, and renewing brings everything back: the devices go on from their own sync cursors, and nothing has to be set up again.

What a lapsed account can still do, so that it can leave or come back: `GET /v1/backup` and `GET /v1/keep/{id}` (the person can still take their own locked data away; this is the one exception to "no syncing", and the easiest to remove), every `DELETE`, `GET /v1/devices`, `/v1/usage`, `/v1/plan` and the list at `/v1/push`, and the `/account/*` pages (renew, change the password, delete the account).

The account row keeps `pass`; `lifetime` is never changed by billing. The `billing_id` (random, 128 bits, made when the account first needs one) is kept in its own table beside the subscriptions (`billing_accounts`), not as a column of `accounts`, so that `IAccounts` knows nothing of billing beyond the pass; it is the provider's customer reference.

## The provider interface

```csharp
public interface IBillingProvider
{
    string Name { get; }    // "polar": stored on every Subscription, and the {provider} in the webhook route

    // A page where the person pays. accountRef is the account's billing_id.
    Task<Uri> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancel);
    // A page where they update the card, see invoices, cancel. Made fresh at each click (it expires).
    Task<Uri> PortalAsync(string providerCustomerId, Uri returnTo, CancellationToken cancel);

    // Checks the signature on the raw body and says what the event is about, or throws BadSignature.
    // It never trusts the body for the state: the server asks GetSubscription for that.
    BillingEvent VerifyWebhook(IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> rawBody);

    Task<SubscriptionState?> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken cancel);
    Task<IReadOnlyList<SubscriptionState>> FindAsync(string accountRef, CancellationToken cancel);   // the done page, reconciling
    Task CancelAsync(string providerSubscriptionId, bool immediately, CancellationToken cancel);     // deleting an account
}

record CheckoutRequest(string AccountRef, string Email, string Plan, BillingInterval Interval, Uri SuccessUrl, Uri ReturnUrl);
record BillingEvent(string EventId, string? ProviderSubscriptionId, string? AccountRef);
record SubscriptionState(string ProviderCustomerId, string ProviderSubscriptionId, string AccountRef, string Plan,
                         SubscriptionStatus Status, DateTimeOffset? CurrentPeriodEnd, bool CancelAtPeriodEnd);
```

The catalog (which Polar product is `cloud` monthly) is configuration, in the server's settings, one block per provider, so a second provider is a second block.

## Flows

**A new subscriber** (no account yet)

1. `yaparr.com/subscribe` says what the plan is and costs and has a form (an email address and the interval) that posts straight to `cloud.yaparr.com/billing/start`, an ordinary form like [`/signup`](https://github.com/zburtle/Yaparr.Cloud#signup-codes-friends-sign-themselves-up) (no script, no CORS).
2. The server makes a **pending account** for the address (as signing up with a code does: it exists, has no password, and cannot sign in), gives it a `billing_id`, and asks the provider for a checkout with that as the customer reference, the address as the customer's email, and a success address of `https://cloud.yaparr.com/billing/done?checkout_id={CHECKOUT_ID}`. It redirects there.
3. The person pays on Polar.
4. Polar sends webhooks; the server stores the subscription (see below).
5. The browser returns to `/billing/done`. If the subscription is already stored (or `FindAsync` finds it), the page goes on to the existing "choose a password" page. If not yet, it is a plain page that refreshes itself every two seconds for up to 30 (no script: a `<meta http-equiv="refresh">`), then says "payment received, this can take a minute, come back to this link".
6. A pending account that never gets a subscription is removed after 24 hours, so a wrong address or an abandoned checkout does not hold a name.

**An account that exists subscribes or renews:** the same, started from the account page (below) instead of the website form, with the existing `billing_id`. If the address has an account with a password, `/billing/start` sends the person to sign in first, so nobody can attach a payment to an account that is not theirs.

**Managing it:** `cloud.yaparr.com/account/subscription` (signed in, like [`/account/password`](https://github.com/zburtle/Yaparr.Cloud#api)) shows the plan, the state and the date, with **Subscribe or renew** (checkout) and **Manage payment and invoices** (a fresh portal link). Cancelling is done in the provider's portal and arrives as a webhook.

**A payment fails:** the provider retries and emails the customer; the subscription is `PastDue` and keeps access until the grace days after its `PeriodEnd` have passed. If the retries give up, it becomes `Ended`.

**A refund:** Polar refunds the money and **leaves the subscription running** (seen in its sandbox: still `active`, and no subscription event). What arrives is `refund.created` and `refund.updated`; when a refund has gone through, unless it only corrects tax, the service revokes the subscription at Polar at once and then looks at it again, so it is `Ended` and the account is `Lapsed` within seconds. The two refund events have to be selected on the webhook endpoint. **A chargeback:** not seen yet; it comes with a refund that carries a dispute, so it should end the subscription the same way.

**Deleting an account** ([`/account/delete`](https://github.com/zburtle/Yaparr.Cloud#deleting-an-account)) cancels the account's subscriptions at the provider first (`CancelAsync`, immediately, so nothing is billed again), then removes everything as it does now. The page says so. If the provider cannot be reached the delete waits and says why: an account is not erased while it is still being charged.

**The webhook** (`POST /billing/webhook/{provider}`, public, so it does very little):

1. Refuse a body over 256 KB; per-address limits like the rest of the service.
2. `VerifyWebhook(headers, rawBody)`: the raw bytes, never re-serialized JSON. A bad or missing signature, or a timestamp outside the five-minute window, is `400` and logged as "bad signature" with no body.
3. Insert `(provider, event_id)` into `billing_events`; if it was there already, `200` and stop (the same event again).
4. If the event names no subscription, `200` and stop (an event type this version does not use is not an error, so the provider does not retry it).
5. `GetSubscriptionAsync(id)`; if the provider cannot be reached, remove the event row and answer `503`, so the provider retries.
6. Map it to a `Subscription` (the table above), upsert it by `(provider, subscription id)`, `200`.

The route has the provider's name in it so that **two providers can be live at once** while moving from one to the other.

**Reconciling** (a daily job, and `accounts billing sync <email>` by hand): for each subscription that is `Active`, `PastDue` or `Canceling` and has not been updated for three days, or whose `PeriodEnd` has passed by a day, ask the provider and store the answer. This is the safety net under lost webhooks.

**When it lapses:** the account is `Lapsed`: no new sign-in and no syncing (above), and nothing removed. The sign-in page, after the password is right, says "Your Yaparr Cloud subscription has ended" with **Renew** (a checkout, as above) instead of handing the app a code. Devices that are signed in see `402` from the services and show the same sentence (see *The apps*); once the subscription is paid, everything resumes without anyone signing in again. Nobody is emailed by Yaparr: the apps show the state and the provider has sent its own notices.

## The server (Yaparr.Cloud) changes

- **Schema 6** (`PostgresDatabase`, and the file store alongside, behind a new `ISubscriptions` held to the same behaviour by one contract test, as the other stores are): `billing_accounts(account, billing_id)`, `subscriptions(account, provider, provider_customer_id, provider_subscription_id, plan, status, period_end, updated)` keyed by `(provider, provider_subscription_id)`, and `billing_events(provider, event_id, received_at)` (purged after 30 days).
- **`AccountPlan`** and **`PlanGate`**: one function for the state, one check. It is applied where a device token is made (`POST /login`, `POST /v1/token`, `POST /v1/token/handoff`) and on each service listed under *What an account is, now*; not to `UserOfToken` (an existing token still identifies its account, which is how a lapsed device can read the plan, take its backup and delete), and not to the `/account/*` pages.
- **A refused sign-in** is a page in the browser (the app starts the browser, so there is nothing for the app to parse): it says the subscription ended and offers **Renew**. Everything else is the `402` above, which the apps treat as a state and never as a failure to retry.
- **Endpoints:** `GET /v1/plan` (bearer): `{billing:"off"}` when disabled, else `{billing:"on", state, plan, endsAt, willRenew}`; the account pages above; `POST /billing/start`, `GET /billing/done`, `POST /billing/webhook/{provider}`. No endpoint under `/v1` returns a provider link: those are pages in a browser, behind the sign-in.
- **Admin:** the portal and `accounts list` show the plan state; `accounts pass <email> lifetime` (see [Lifetime passes](#lifetime-passes)), `accounts grant <email> --days N` (a comp, a subscription with provider `manual`), `accounts billing sync`. The admin portal can never see a card or an invoice: those are the provider's.
- **Settings** (`Service__Billing__…`, nothing by default): `Provider` (`polar`), `GraceDays` (3), `PendingHours` (24), and for Polar `Server` (`sandbox` or `production`), `AccessToken`, `WebhookSecret`, and the products (`Plans__cloud__Monthly`, `Plans__cloud__Yearly`). The token and the secret come from `.env` (`chmod 600`), as the other secrets do, and are never logged, printed or committed.
- **Logging:** outcomes only (`billing: webhook subscription (polar)`, `billing: bad signature (polar)`, `billing: reconciled 3`), never an address, an id or a body, like [`PushApi`](https://github.com/zburtle/Yaparr.Cloud#push-notifications-pushapi).

## Lifetime passes

- Every account that exists is `lifetime` and stays free for life; billing never changes a `lifetime` pass.
- **Signup codes** keep doing what they do now: a code the owner makes by hand gives its people the `lifetime` pass. Public sign-up is by payment; codes are the invitation. Anyone given such a code later is free for life too.
- **`accounts pass <email> lifetime`** gives an existing account the pass. If that account has a live subscription, the command cancels it at the provider at once (`CancelAsync`) so it is not billed again, and prints a reminder to refund the unused part by hand (see below). `accounts pass <email> none` takes the pass away and the account then follows the ordinary rules.

## Refunds

The policy, as decided:

- **Monthly:** a full refund if asked within **48 hours of the payment date**. After that, none.
- **Annual:** a refund of the **unused time, prorated, whenever it is asked**: the price times the days left in the period over the days in it, counted from the day of the request, rounded down to the cent.
- **Otherwise:** no refund, unless there are special circumstances, which the owner decides case by case.
- **Where the law gives more, the law applies** (below), and `/subscribe` says so.

How it works: the owner refunds in Polar's dashboard, which can refund all or part of an order. Polar does not end the subscription itself, but it sends the refund events and the service ends it (see *Flows*, *A refund*), so the account is `Lapsed` within seconds and nothing has to be cancelled by hand. Nothing in Yaparr issues a refund, so Yaparr never holds a payment credential. A "request a refund" button could compute the amount, since the rule is mechanical; it is not in the first version.

What I found when I checked (Polar's [refund documentation](https://polar.sh/docs/features/refunds) and [buyer terms](https://polar.sh/legal/checkout-buyer-terms), and the law as described by secondary sources):

- **A refund costs the fee.** Polar does not return its original payment fees on a refund. A refunded $4 month costs $0.70 on top of the $4; a refunded $40 year costs $2.50 on top of what is returned. A chargeback costs $15 whatever the outcome, which is the reason to refund early and easily.
- **Polar may refund on its own.** It reserves the right to refund within 60 days of a purchase at its discretion, to head off chargebacks, **even where the seller has a "no refunds" policy**. The policy above cannot promise fewer refunds than Polar might give.
- **Polar is the seller, and its terms defer to the law.** Refunds are judged under "applicable law, the applicable refund policy and the card network rules", and the agreement "does not limit any non-waivable consumer rights". A buyer's right to a chargeback is not restricted.
- **In the EU and the UK a buyer has a 14-day right to withdraw** from an online purchase. For a *service* it is lost only once the service has been fully performed, after the buyer's prior express consent and acknowledgement, which a subscription that is still running has not been; the buyer may owe a proportionate payment for what they used. A ruling of the EU Court of Justice in July 2026, as reported by law firms, held that a streaming subscription that is a digital *service* cannot be made to lose the right by a term at sign-up. I read this from secondary sources and not from the judgment.
- **What follows for the policy:** for a buyer in the EU or the UK the 48-hour monthly rule is stricter than the law, so the law wins. The wording to use on `/subscribe` is the three rules above plus: *"If you live where the law gives you a right to withdraw from an online purchase (the EU and the UK give 14 days), that right applies, and we refund the unused part of what you paid."* In practice that is a prorated refund inside 14 days of the first payment. Whether Polar's checkout records the consent that the law asks for, and how Polar handles an EU withdrawal request on a subscription, is not something I could find in its documentation: ask Polar's support before `/subscribe` goes live.

## The apps (Yaparr)

- `BackupServerClient.PlanAsync()` for `GET /v1/plan`, and `BackupServerClient.SubscriptionPage(server)` for the page, beside `PasswordPage`.
- **Settings > Yaparr Cloud**, one line under the sign-in: nothing when billing is off or the account is `Lifetime`; "Active, renews 3 Dec", "Ends 3 Dec", "Payment failed: update your card", or "Subscription ended: syncing is paused and you can't sign in on a new device until it is renewed. Your data is kept." In builds that may (below) one button, **Manage subscription**, opens the account page in the browser like **Change password** does. A "Learn more about Yaparr Cloud" link as for every other setting ([Learn more](development.md#keeping-the-apps-short)).
- **A `402`** from sign-in, sync, the mailbox, pictures or the gateway sets that state, stops that work (no retry loop) and shows the sentence above, not an unexplained failure.

### Android and Google Play

Checked against Google's [Payments policy](https://support.google.com/googleplay/android-developer/answer/9858738) and its [update for the US](https://support.google.com/googleplay/android-developer/answer/15582165) in November 2026:

- The policy lists "subscription services" and "cloud software and services (such as data storage services)" among what an app must sell through Google Play's billing when it "require[s] or accept[s] payment for access to in-app features or services". **The page has no exemption for a service bought on a website and used in the app.** Yaparr Cloud is such a service, so this is a real risk for a Play build, not only a matter of wording.
- Apart from the programs below, "apps may not lead users to a payment method other than Google Play's billing system", including through the store listing, in-app promotions, "webviews, buttons, links, messaging, advertisements, or other calls to action", and sign-up flows.
- Alternative billing, and links out, are allowed in some regions through programs that need enrolment and have their own terms (the UK, the EEA and the US; in the US, enrolled developers report transactions and pay service fees to Google from 1 October 2026).
- **In the US**, since a court injunction on 29 October 2025, Google "will not require the use of Google Play Billing" and will not stop a developer telling users about availability or pricing outside Play, or linking to transactions. A settlement of 4 March 2026 is waiting for a revised order, so this can change.
- A listing that mentions a feature that may need a charge "must clearly notify users that payment is required".

What the design does about it:

1. **The Play build** (the `-p:PlayStore=true` switch that already removes the self-updater) shows **no price, no Subscribe or Manage button, no link to a page that sells, and no "renew at …" words, anywhere**, and its "Learn more" links go only to guides with no purchase content (so the guides get no plans section, and the plan page is not linked from them). It shows the state sentence and nothing else. That meets the rule against steering everywhere. The listing says that Yaparr Cloud needs a paid subscription (the rule above) and nothing about where to get one.
2. **That does not settle the first rule outside the US:** a Play user who bought on the website and syncs in the app may still be seen as paying for an in-app service outside Google Play's billing. Reading the policy cannot answer it. **Ask Google** (Play Console, Help, contact policy support) with this design, before the first Play release that has billing on. If the answer is no, the fallback that keeps the design is a third provider, `GooglePlayBilling : IBillingProvider` (a purchase token checked with the Play Developer API, Google's real-time notifications as the webhook), for Play users only, at Google's fee: the lease model and the `provider` on each subscription are what let it sit beside Polar.
3. **The direct download** (the GitHub release, the build without `PlayStore`) has none of these rules and shows **Manage subscription**.
4. In the US the Play build could show the button too, but not while the order that allows it is being revised.

## The website (Yaparr.Web)

`/subscribe` (the plan, what it holds, the price, what happens when it ends, a link to the guide, and the form), and a short `/subscribe/thanks` for the `/billing/done` fallback. The guides stay free of prices and of how to subscribe (the Play build links to them, see *Android and Google Play*); everything about the plan lives on `/subscribe`, which the Play build never links to. The [privacy policy](https://yaparr.com/privacy) must say that payments are handled by Polar as the seller, that Polar sees the email address, card and country, that Yaparr Cloud stores only the subscription's state and ids, and what is removed when an account is deleted.

## Polar specifics

What the adapter uses (checked against [Polar's documentation](https://polar.sh/docs) in November 2026; confirm each against the version you pin, since Polar versions its API):

- **API:** `https://api.polar.sh/v1` (production) and `https://sandbox-api.polar.sh/v1` (sandbox; separate tokens). Build and test everything in the sandbox first. Pin the API version.
- **Checkout:** [`POST /v1/checkouts/`](https://polar.sh/docs/api-reference/checkouts/create-session) with `products`, `external_customer_id` (the account's `billing_id`), `customer_email`, `success_url` (with `checkout_id={CHECKOUT_ID}`) and `return_url`. For a customer made by a checkout, Polar creates the customer with that external id.
- **Portal:** a customer session gives a short-lived `customer_portal_url`; make a new one at each click, never store it. The documentation shows it made from the customer id; the adapter keeps `ProviderCustomerId` for this, and checks whether the external id also works on the version it pins.
- **Webhooks:** [Polar's](https://polar.sh/docs/integrate/webhooks/events) events include `subscription.created`, `.updated`, `.canceled`, `.revoked`, `.past_due` and `customer.state_changed`. Secrets made since 8 September 2026 use **Standard Webhooks** (the `webhook-id`, `webhook-timestamp` and `webhook-signature` headers, HMAC-SHA256 over id, timestamp and the raw body); older ones use Polar's own variant. `PolarBilling.VerifyWebhook` does the first and refuses the rest. The design deliberately depends on **none** of the event names beyond "this names a subscription" (principle 4), because their exact meaning is the part that varies.
- **Fees** (as published when this was written; check the dashboard for the rate the organization is actually on): the free plan is **5% + 50¢** a transaction, 1.5% more for international cards, and payouts go through Stripe with its own small fees; the paid tiers lower the percentage for a monthly fee. A fixed 50¢ weighs most on a small monthly price: at **$4 a month** it is about 17.5% of the charge (70¢), and on **$40 a year** about 6% ($2.50), before the 1.5% extra on international cards and the payout fees. So a yearly subscriber is worth a good deal more to you than a monthly one; the paid Polar tiers only start to pay off at about $1,400 a month in sales.
- **SDK:** I found none for .NET (Polar's are for JavaScript, Python, PHP and Go), and none is needed: the adapter is four calls over `HttpClient` and one signature check, with the responses mapped through small records and tested against recorded samples.

## Moving to Stripe later

1. Write `StripeBilling : IBillingProvider` (`Stripe.net` is first-class for .NET): Checkout Sessions with `client_reference_id` and customer metadata carrying the `billing_id`, the Billing Portal, `Stripe-Signature` verification, `subscriptions.retrieve`. Nothing else in the server changes.
2. Add its settings block and webhook endpoint (`/billing/webhook/stripe`); both routes run together.
3. Switch `Provider` to `stripe`: **new** checkouts use Stripe. Subscriptions already on Polar keep working, each row knows its provider, the portal link goes to the provider that row names, and the lease ends them naturally.
4. Existing subscribers: let them run out and re-subscribe, or ask Stripe to import the cards from Polar (a processor-to-processor transfer that has to be requested and is not guaranteed; do not plan on it).
5. **What really changes is not code:** with Stripe you become the seller, so VAT/sales-tax registration and filing (Stripe Tax calculates, it does not file), invoices, and chargeback handling are yours. That is the reason to start with a merchant of record and move only when the volume pays for it.

## Security and privacy

- The webhook endpoint is public, so everything about it is defensive: raw-body signature check in constant time, a five-minute timestamp window, de-duplication, a size cap, rate limits, no state taken from the body, `200` for what it does not understand and `503` only when the provider cannot be asked.
- A card number, a billing address or an invoice never reaches Yaparr; the checkout and the portal are Polar's pages.
- The provider learns an email address and a payment. Yaparr Cloud learns the state of a subscription. Neither learns what is in the account: the cloud only ever holds locked data, and `billing_id` is random, so the provider's records cannot be tied to a room, a device or a message.
- An email address that is typed at checkout is the account's name, as it is for every account, and is not verified before the payment. Someone could pay for an account named with someone else's address; with email off, the payer, not the address's owner, is the one who then sets the password. **With email on (see [email](email.md)) the link to choose the password goes to the address**, so only the owner of the address can finish the account.
- Deleting an account cancels what it pays for (above).

## Tests

- `ISubscriptions`: file and Postgres through one contract test (upsert, status mapping, `PeriodEnd`, purging old events).
- `AccountPlan`: every row of the state table, with the clock injected, and `Lifetime` never changed by anything.
- `PlanGate`: a `Lapsed` account is refused a new token at each place one is made and gets `402` from each service in the list (and `Lifetime`, `Subscribed` and a subscription within its grace days get neither), it can still read its plan and usage, take its backup and kept pieces, delete things and use the account pages, nothing is ever removed, and renewing brings the services back with no sign-in.
- The webhook: a bad signature, a stale timestamp, a repeated event, an event naming no subscription, events out of order, the provider failing (`503`, retried), and the same set against a **fake provider** so the flow is tested end to end without Polar.
- `PolarBilling`: recorded sandbox samples of the webhook and API responses; the signature check against signatures made by a Standard Webhooks reference library, so it is tested against someone else's implementation and not only our own.
- The flows: new subscriber to a working account, the done page before and after the webhook, a pending account expiring, deleting a subscribed account.

## Decisions

Settled by the owner:

| | |
|---|---|
| Price | **$4 a month or $40 a year** |
| Trial | none |
| When it lapses | no new device can sign in and **syncing stops**; nothing is ever removed (data is not purged today and stays so); renewing brings it all back |
| Existing accounts | free for life, and so is anyone given one of the owner's codes later |
| Refunds | monthly: full, within 48 hours of payment; annual: prorated for the unused time, whenever it is asked; otherwise none unless there are special circumstances; the law applies where it gives more |
| Seller and payouts | Polar, paying out through Stripe to the owner's country and bank |
| Android and the stores | state only in the Play build, the button in the direct build (*Android and Google Play*) |

Checked (see *Refunds* and *Android and Google Play*): the refund policy against Polar's terms and consumer law, and the Android design against Google's Payments policy. Both found something to act on, and one thing that reading cannot settle:

1. **Before `/subscribe` goes live:** ask Polar's support how its checkout records the consent that EU withdrawal law asks for and how it handles a withdrawal on a subscription, and publish the refund wording from *Refunds*.
2. **Before the first Play release that has billing on:** ask Google's policy support whether a service bought on the website and used in the app is allowed without Google Play's billing. If not, add the `GooglePlayBilling` provider, or leave the plan out of the Play build.
3. **At that release:** read the Payments policy and the US update again; both change.

## Order of work

0. **The owner, in Polar:** the organization, the payout account, the sandbox, and the two products ($4 a month, $40 a year); nothing is needed in code for this.
1. **Yaparr.Cloud (built):** the neutral model, `ISubscriptions` (file and Postgres, schema 6), `AccountPlan`, `PlanGate` on token issuing and on the sync services, `GET /v1/plan`, `accounts pass`, all with billing off by default and a **fake provider**. Nothing changes for anyone yet.
2. **Yaparr.Cloud (built):** the webhook, reconciling, the account pages and the pending-account flow, tested against the fake provider.
3. **Yaparr.Cloud (built, tried in the sandbox and against the running service):** `PolarBilling`, with the settings `Service__Billing__Polar__…` (README, *Paid plans*). Tried through the adapter itself in Polar's sandbox: a checkout through the service's own `/billing/start`, a test-card payment, finding and reading the subscription (`Active`, the account's random billing id as its reference), the customer portal, cancelling at period end (`Canceling`), revoking at once (`Ended`, and again with no error), an unknown id (nothing). Tried with the real webhooks reaching the running service (`cloud.yaparr.com` with sandbox settings), about forty deliveries and every one answered `200`, so the real Standard Webhooks signatures check out:
   - **Paying first:** pay, land on the password page, choose a password; the address then refuses a second sign-up.
   - **A refund:** left the subscription `active` with no subscription event. Fixed (see *Flows*): after the fix a refund ended it at Polar and the service saw it, within two seconds.
   - **A failed payment:** Polar tried the renewal some minutes after the period end was moved (it is not immediate), the order stayed `pending`, the subscription became `past_due`, and the service accepted `subscription.past_due`.
   - **A revoke** arrives as `customer.state_changed` alone, which is enough because the service looks the subscription up again.
   - **Polar refuses `example.com`** as a customer email (it does not accept mail), so tests use a real-looking address.
   - **Not seen from here:** what the service stored (its database is not readable from the sandbox session), and the admin portal in a browser.
   Also built: an address that has paid is never sent to pay again and its link is not replaced (`accounts setup-link` for someone who lost theirs); the subscription **sign-in page** (`/account/subscription`) is the one front door, it also starts a payment for someone with no account; and the **admin portal** shows each account's plan with a Payments card and gives the pass, a new sign-in link, a check with the provider, and ending a subscription.
4. **Yaparr (built, not tried on a phone):** the plan line and the `402` handling in `Yaparr.AppCore` (`CloudAccountClient.PlanAsync`, `CloudPlanText`, `BackupServerException.SubscriptionEnded`, `BackupServerClient.SubscriptionPage`), tested; shown in the desktop's Yaparr Cloud account window and the phone app's account panel; the **Manage subscription** button in the desktop build and the GitHub build of the phone app only (the Play build shows the line, never a button or an address). A `402` from the backup, the account sync or the mailbox is said once in the settings note (not as a failure), and that server is left alone for six hours; opening the account window asks the server and ends the pause at once if the account is covered again. `GET /v1/plan` gained `pastDue` for "Payment failed: update your card". Not done: the line under the sign-in in the main settings page (it is in the account window, one tap away), and the pictures and gateway services' own `402` notes (they show the server's sentence as the error). The Android project could not be built in the session that wrote it: CI is its check.
5. **Yaparr.Web:** `/subscribe` with the refund wording, the privacy policy. (No plan content in the guides.)
6. **Before going live:** the two questions in *Decisions* (to Polar about EU withdrawal, to Google about the Play build).
7. Turn billing on in production, with the first checkout done by the owner.
