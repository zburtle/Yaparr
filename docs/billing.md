# Subscriptions for Yaparr Cloud (design)

Status: **a design, nothing is built.** It says how a paid plan for Yaparr Cloud would work, with [Polar](https://polar.sh) taking the payments and the design kept generic enough that Stripe (or anyone else) can replace it by writing one adapter. The server is [Yaparr.Cloud](https://github.com/zburtle/Yaparr.Cloud); the website is [Yaparr.Web](https://github.com/zburtle/Yaparr.Web). See [Yaparr Cloud](guides/yaparr-cloud.md) for what the service does and [Mailbox](mailbox.md) for the rules it already follows.

## What is sold, and what is not

- **Chat is free and stays free.** Yaparr works fully with no account anywhere ([Mailbox](mailbox.md#what-yaparr-cloud-is-and-is-not): nothing in the client may depend on the cloud or be held back without it). A subscription is for the *service*: Yaparr Cloud's sync, kept history, mailbox and pictures for people who are away, push wake-ups and the UDP gateway, which cost real storage and bandwidth.
- **One plan, `cloud`**, in two intervals (monthly, yearly). More plans can be added without changing the design: a plan is a name and a set of limits.
- **No free tier of the cloud.** Accounts are still made by hand or by signup code today ([README](https://github.com/zburtle/Yaparr.Cloud#accounts-a-password-to-sign-in-a-sync-key-to-open-what-is-stored)); that stays, and a signup code keeps giving the `lifetime` pass it gives now, so the friends who already have an account are untouched.

## Principles

1. **Billing is optional in the server and off by default.** A self-hosted Yaparr Cloud never has it, the apps work with a server that does not, and `GET /v1/plan` on such a server says billing is off (the apps show nothing). Only a deployment that sets `Service__Billing__Provider` has it. The AGPL code is all in the open; the secrets are not in the code.
2. **The provider is an adapter, not a concept.** The server reasons about an **entitlement** (this account may use the cloud until this date). Polar's products, customers and event names appear in exactly one place, `PolarBilling`. Everything else, the database, the apps, the website, the admin portal, would be identical under Stripe.
3. **An entitlement is a lease.** What a subscription grants is "until `valid_until`", and the server decides from that date. A missed webhook can then never cause a wrong lapse or a free ride for long: the lease runs out on its own, and a renewal only extends it.
4. **Webhooks say "look", they do not say "what".** A webhook is verified, de-duplicated, and then the server **asks the provider for the subscription's current state** and stores that. Order, repeats and the exact meaning of each event name (which differ between providers and change between versions) stop mattering.
5. **The provider's id for a person is ours.** Each account gets a random `billing_id`; that is what the provider stores as its customer reference (`external_customer_id` on Polar). The account's name (its email address) is never the key, and the provider never learns anything else about the account.
6. **The apps never know the provider.** They ask the server for the plan, and for a page to manage it; the server redirects to Polar. Nothing in an app, the website or a guide needs to change when the provider does.
7. **Yaparr still never emails anyone.** Receipts, failed-payment and renewal notices come from the provider, which is the seller and already has the address. Yaparr shows the state in the app (below).

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
    DateTimeOffset ValidUntil,      // the end of the period paid for, plus the grace days
    DateTimeOffset UpdatedAt);
```

| Provider's word (Polar) | Our status | Meaning |
|---|---|---|
| `active`, `trialing` | `Active` | paid, renews |
| `active` with cancel-at-period-end | `Canceling` | paid until `ValidUntil`, then ends |
| `past_due` | `PastDue` | a payment failed and is being retried; still has access |
| `canceled` (after the period), `revoked`, `unpaid`, `incomplete_expired` | `Ended` | no access beyond `ValidUntil` |

`ValidUntil` is `current_period_end` plus `Billing__GraceDays` (3), so a payment that lands a day late does not lock anyone out. An `Ended` subscription's `ValidUntil` is clamped to now when it was revoked (a refund or a chargeback ends access at once).

**What an account is, now** (`AccountPlan.Of(account, now)`; the only place that decides):

| State | When | Writes | Reads, sign-in, delete |
|---|---|---|---|
| `Lifetime` | the account's `pass` is `lifetime` (everyone so far) | yes | yes |
| `Subscribed` | some subscription has `ValidUntil` in the future | yes | yes |
| `Lapsed` | had a subscription, none valid, within `Billing__KeepDays` (60) of `ValidUntil` | **no (402)** | yes (export, renew, delete) |
| `Expired` | the same, past `KeepDays` | the data has been removed | sign-in and renewing still work |

The account row keeps `pass`; `lifetime` is never changed by billing. A new column `billing_id` (random, 128 bits, made when the account first needs one) is the provider's customer reference.

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

**A payment fails:** the provider retries and emails the customer; the subscription is `PastDue` and keeps access through its `ValidUntil` (period end plus grace). If the retries give up, it becomes `Ended`.

**A refund or a chargeback:** arrives as a revocation; `ValidUntil` becomes now, so the account is `Lapsed` at once.

**Deleting an account** ([`/account/delete`](https://github.com/zburtle/Yaparr.Cloud#deleting-an-account)) cancels the account's subscriptions at the provider first (`CancelAsync`, immediately, so nothing is billed again), then removes everything as it does now. The page says so. If the provider cannot be reached the delete waits and says why: an account is not erased while it is still being charged.

**The webhook** (`POST /billing/webhook/{provider}`, public, so it does very little):

1. Refuse a body over 256 KB; per-address limits like the rest of the service.
2. `VerifyWebhook(headers, rawBody)`: the raw bytes, never re-serialized JSON. A bad or missing signature, or a timestamp outside the five-minute window, is `400` and logged as "bad signature" with no body.
3. Insert `(provider, event_id)` into `billing_events`; if it was there already, `200` and stop (the same event again).
4. If the event names no subscription, `200` and stop (an event type this version does not use is not an error, so the provider does not retry it).
5. `GetSubscriptionAsync(id)`; if the provider cannot be reached, remove the event row and answer `503`, so the provider retries.
6. Map it to a `Subscription` (the table above), upsert it by `(provider, subscription id)`, `200`.

The route has the provider's name in it so that **two providers can be live at once** while moving from one to the other.

**Reconciling** (a daily job, and `accounts billing sync <email>` by hand): for each subscription that is `Active`, `PastDue` or `Canceling` and has not been updated for three days, or whose `ValidUntil` has passed by a day, ask the provider and store the answer. This is the safety net under lost webhooks.

**Lapse and removal:** when the last lease runs out the account is `Lapsed` (writes refused, everything else works, so the person can export, renew or delete). After `KeepDays` a daily job removes what the account holds (backups, synced records, kept pieces, pictures, mailbox items, push registrations) the way account erasure does, and leaves the account so it can be renewed from nothing. Nobody is emailed by Yaparr: the app shows a banner for the whole period and the provider has already sent its own notices.

## The server (Yaparr.Cloud) changes

- **Schema 6** (`PostgresDatabase`, and the file store alongside, behind a new `ISubscriptions` held to the same behaviour by one contract test, as the other stores are): `accounts.billing_id`, `subscriptions(account, provider, provider_customer_id, provider_subscription_id, plan, status, valid_until, updated_at)` keyed by `(provider, provider_subscription_id)`, and `billing_events(provider, event_id, received_at)` (purged after 30 days).
- **`AccountPlan`** and **`PlanGate`**: one function for the state, one check for the endpoints that write. Gated: `PUT /v1/backup`, `PUT /v1/sync`, `POST /v1/mailbox/{id}`, `PUT /v1/blob/{id}`, `PUT /v1/keep/{id}`, `PUT /v1/push`, and the gateway upgrade. Not gated: every `GET`, every `DELETE`, sign-in, `POST /v1/push/wake/{token}` (it has no account; a lapsed account's token simply answers like an unknown one), and `/account/*`.
- **A refused write** is `402` with `{"error":"subscription_required","state":"lapsed","endsAt":"…"}`. The apps stop retrying and show the state; they never treat it as a failed sync to be retried every minute.
- **Endpoints:** `GET /v1/plan` (bearer): `{billing:"off"}` when disabled, else `{billing:"on", state, plan, endsAt, willRenew}`; the account pages above; `POST /billing/start`, `GET /billing/done`, `POST /billing/webhook/{provider}`. No endpoint under `/v1` returns a provider link: those are pages in a browser, behind the sign-in.
- **Admin:** the portal and `accounts list` show the plan state; `accounts grant <email> --days N` (a comp, a subscription with provider `manual`), `accounts billing sync`. The admin portal can never see a card or an invoice: those are the provider's.
- **Settings** (`Service__Billing__…`, nothing by default): `Provider` (`polar`), `GraceDays` (3), `KeepDays` (60), `PendingHours` (24), and for Polar `Server` (`sandbox` or `production`), `AccessToken`, `WebhookSecret`, and the products (`Plans__cloud__Monthly`, `Plans__cloud__Yearly`). The token and the secret come from `.env` (`chmod 600`), as the other secrets do, and are never logged, printed or committed.
- **Logging:** outcomes only (`billing: webhook subscription (polar)`, `billing: bad signature (polar)`, `billing: reconciled 3`), never an address, an id or a body, like [`PushApi`](https://github.com/zburtle/Yaparr.Cloud#push-notifications-pushapi).

## The apps (Yaparr)

- `BackupServerClient.PlanAsync()` for `GET /v1/plan`, and `BackupServerClient.SubscriptionPage(server)` for the page, beside `PasswordPage`.
- **Settings > Yaparr Cloud**, one line under the sign-in: nothing when billing is off or the account is `Lifetime`; "Active, renews 3 Dec", "Ends 3 Dec", "Payment failed: update your card", or "Lapsed: syncing is paused. Your data is kept until 2 Feb." with one button, **Manage subscription**, which opens the page in the browser like **Change password** does. A "Learn more about Yaparr Cloud plans" link as for every other setting ([Learn more](development.md#keeping-the-apps-short)).
- **A `402`** from sync, the mailbox, pictures or the gateway sets that state and stops the work; the sync line says "Syncing is paused: the Yaparr Cloud subscription ended." (reads still work, so a restore from the cloud does too).
- **Android and the stores.** Google Play requires its own billing for digital goods sold *inside* an app, and what an app may say about buying elsewhere differs by region and has been changing. The safe shape is the one above: the app **shows the state and opens the account page in the browser, and shows no price, no "subscribe" button and no purchase wording**; the Subscribe button and the price live on the website and the browser page only. Read the current Play policy before the first release with this, and consider a build without the button for Play only if the policy asks.

## The website (Yaparr.Web)

`/subscribe` (the plan, what it holds, the price, what happens when it ends, a link to the guide, and the form), and a short `/subscribe/thanks` for the `/billing/done` fallback. The Yaparr Cloud [guide](guides/yaparr-cloud.md) gets a "Plans" section (the guides are copied to the site by `scripts/sync-learn.mjs`). The [privacy policy](https://yaparr.com/privacy) must say that payments are handled by Polar as the seller, that Polar sees the email address, card and country, that Yaparr Cloud stores only the subscription's state and ids, and what is removed when an account is deleted.

## Polar specifics

What the adapter uses (checked against [Polar's documentation](https://polar.sh/docs) in November 2026; confirm each against the version you pin, since Polar versions its API):

- **API:** `https://api.polar.sh/v1` (production) and `https://sandbox-api.polar.sh/v1` (sandbox; separate tokens). Build and test everything in the sandbox first. Pin the API version.
- **Checkout:** [`POST /v1/checkouts/`](https://polar.sh/docs/api-reference/checkouts/create-session) with `products`, `external_customer_id` (the account's `billing_id`), `customer_email`, `success_url` (with `checkout_id={CHECKOUT_ID}`) and `return_url`. For a customer made by a checkout, Polar creates the customer with that external id.
- **Portal:** a customer session gives a short-lived `customer_portal_url`; make a new one at each click, never store it. The documentation shows it made from the customer id; the adapter keeps `ProviderCustomerId` for this, and checks whether the external id also works on the version it pins.
- **Webhooks:** [Polar's](https://polar.sh/docs/integrate/webhooks/events) events include `subscription.created`, `.updated`, `.canceled`, `.revoked`, `.past_due` and `customer.state_changed`. Secrets made since 8 September 2026 use **Standard Webhooks** (the `webhook-id`, `webhook-timestamp` and `webhook-signature` headers, HMAC-SHA256 over id, timestamp and the raw body); older ones use Polar's own variant. `PolarBilling.VerifyWebhook` does the first and refuses the rest. The design deliberately depends on **none** of the event names beyond "this names a subscription" (principle 4), because their exact meaning is the part that varies.
- **Fees** (as published when this was written; check the dashboard for the rate the organization is actually on): the free plan is **5% + 50¢** a transaction, 1.5% more for international cards, and payouts go through Stripe with its own small fees; the paid tiers lower the percentage for a monthly fee. A fixed 50¢ weighs most on a small monthly price: at $3 a month it is about 22% of the charge, at $5 about 15%, and a yearly plan at $36 about 6%. Offering a yearly plan and setting the monthly price with this in mind matters more than the choice of tier.
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
- An email address that is typed at checkout is the account's name, as it is for every account, and is not verified (nothing is emailed). Someone could pay for an account named with someone else's address; it is a name only, and the payer, not the address's owner, is the one who then sets the password.
- Deleting an account cancels what it pays for (above).

## Tests

- `ISubscriptions`: file and Postgres through one contract test (upsert, status mapping, `ValidUntil`, purging old events).
- `AccountPlan`: every row of the state table, with the clock injected, and `Lifetime` never changed by anything.
- `PlanGate`: each gated endpoint refuses a `Lapsed` account with `402` and allows every read, delete and sign-in.
- The webhook: a bad signature, a stale timestamp, a repeated event, an event naming no subscription, events out of order, the provider failing (`503`, retried), and the same set against a **fake provider** so the flow is tested end to end without Polar.
- `PolarBilling`: recorded sandbox samples of the webhook and API responses; the signature check against signatures made by a Standard Webhooks reference library, so it is tested against someone else's implementation and not only our own.
- The flows: new subscriber to a working account, the done page before and after the webhook, a pending account expiring, deleting a subscribed account.

## Decisions for the owner

1. **Price and intervals.** Monthly and yearly, and the amounts. (The fee arithmetic above favours a yearly plan and a monthly price of $4 or more.)
2. **What happens when it lapses.** The design says read-only for 60 days, then the data is removed. Longer or shorter? Should the first lapse also keep the data a second time before removal?
3. **A trial.** None in the design. Polar supports one; it is a lease like any other (`trialing` maps to `Active`).
4. **Existing accounts.** The design keeps every account that exists as `lifetime`. Confirm, or set an end date for some.
5. **Refunds.** Polar is the seller, so its refund policy governs; say what it is on `/subscribe`.
6. **Where the seller is.** Polar pays out through Stripe, which must support the owner's country and bank; set that up in the Polar dashboard before anything else.
7. **The Android store policy** (above), and whether Play builds get the account-page button.

## Order of work

1. **Yaparr.Cloud:** the neutral model, `ISubscriptions` (file and Postgres, schema 6), `AccountPlan`, `PlanGate` and `GET /v1/plan`, all with billing off by default and a **fake provider**. Nothing changes for anyone yet.
2. **Yaparr.Cloud:** the webhook, reconciling, the account pages and the pending-account flow against the fake provider.
3. **Yaparr.Cloud:** `PolarBilling`, tested in the sandbox with a real test checkout.
4. **Yaparr:** the plan line, the `402` handling and the button; a guide section.
5. **Yaparr.Web:** `/subscribe`, the privacy policy, the guide.
6. Turn billing on in production, with the first checkout done by the owner.
