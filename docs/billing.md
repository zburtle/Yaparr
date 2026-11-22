# Subscriptions for Yaparr Cloud (design)

Status: **a design, nothing is built.** It says how a paid plan for Yaparr Cloud would work, with [Polar](https://polar.sh) taking the payments and the design kept generic enough that Stripe (or anyone else) can replace it by writing one adapter. The server is [Yaparr.Cloud](https://github.com/zburtle/Yaparr.Cloud); the website is [Yaparr.Web](https://github.com/zburtle/Yaparr.Web). See [Yaparr Cloud](guides/yaparr-cloud.md) for what the service does and [Mailbox](mailbox.md) for the rules it already follows.

## What is sold, and what is not

- **Chat is free and stays free.** Yaparr works fully with no account anywhere ([Mailbox](mailbox.md#what-yaparr-cloud-is-and-is-not): nothing in the client may depend on the cloud or be held back without it). A subscription is for the *service*: Yaparr Cloud's sync, kept history, mailbox and pictures for people who are away, push wake-ups and the UDP gateway, which cost real storage and bandwidth.
- **One plan, `cloud`**: **$4 a month or $40 a year** (the yearly price is two months free). No trial. More plans can be added without changing the design: a plan is a name and a set of limits.
- **No free tier of the cloud.** Accounts are still made by hand or by signup code today ([README](https://github.com/zburtle/Yaparr.Cloud#accounts-a-password-to-sign-in-a-sync-key-to-open-what-is-stored)); that stays, and a signup code keeps giving the `lifetime` pass it gives now, so the friends who already have an account are untouched, and **every account that exists stays free for life**. Anyone given one of those codes later is free for life too, and an admin can give any account the pass (see [Lifetime passes](#lifetime-passes)).

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
| `active` | `Active` | paid, renews |
| `active` with cancel-at-period-end | `Canceling` | paid until `ValidUntil`, then ends |
| `past_due` | `PastDue` | a payment failed and is being retried; still has access |
| `canceled` (after the period), `revoked`, `unpaid`, `incomplete_expired` | `Ended` | no access beyond `ValidUntil` |

`ValidUntil` is `current_period_end` plus `Billing__GraceDays` (3), so a payment that lands a day late does not lock anyone out. An `Ended` subscription's `ValidUntil` is clamped to now when it was revoked (a refund or a chargeback ends access at once).

**What an account is, now** (`AccountPlan.Of(account, now)`; the only place that decides):

| State | When | Signing in on a new device | Everything else |
|---|---|---|---|
| `Lifetime` | the account's `pass` is `lifetime` (everyone so far, and anyone given the pass) | yes | yes |
| `Subscribed` | some subscription has `ValidUntil` in the future | yes | yes |
| `Lapsed` | had a subscription, none is valid now | **no** | unchanged: devices that are already signed in keep working, reads, deletes and the account pages work, and **nothing is ever removed** |

"Signing in" here is what makes a device token (`/login` for an app, `/v1/token`, the hand-off after sign-up): a device that signed out, or a new one, cannot get one until the subscription is renewed. A token that exists keeps working. This is the whole lever: the cloud stays as it is today (data is never purged), and what a subscription buys is being able to add a device.

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

**When it lapses:** the account is `Lapsed`. Nothing is removed and nothing stops on a device that is already signed in. The sign-in page, after the password is right, says "Your Yaparr Cloud subscription has ended" with **Renew** (a checkout, as above) instead of handing the app a code; once the subscription is paid, signing in works again. Nobody is emailed by Yaparr: the apps show the state in Settings and the provider has sent its own notices.

*What this leaves open:* a person who cancels keeps syncing on the devices they have, for as long as those stay signed in, and loses only the ability to add or re-sign-in a device (the cap of 20 devices, and a lost phone, are where it bites). If that turns out to cost too much, the same `PlanGate` can also refuse the writes (`402`) with no change to the model; it is one setting away, and not on by default.

## The server (Yaparr.Cloud) changes

- **Schema 6** (`PostgresDatabase`, and the file store alongside, behind a new `ISubscriptions` held to the same behaviour by one contract test, as the other stores are): `accounts.billing_id`, `subscriptions(account, provider, provider_customer_id, provider_subscription_id, plan, status, valid_until, updated_at)` keyed by `(provider, provider_subscription_id)`, and `billing_events(provider, event_id, received_at)` (purged after 30 days).
- **`AccountPlan`** and **`PlanGate`**: one function for the state, one check, applied where a device token is made (`POST /login`, `POST /v1/token`, `POST /v1/token/handoff`; not to `UserOfToken`, so an existing token is untouched). Not gated: every call with a token that exists, the `/account/*` pages (a lapsed account must be able to renew, change its password and delete itself), and `POST /v1/push/wake/{token}`.
- **A refused sign-in** is a page in the browser (the app starts the browser, so there is nothing for the app to parse): it says the subscription ended and offers **Renew**. `POST /v1/token` for a lapsed account answers `402` `{"error":"subscription_required"}`, for an app that is mid-sign-in when it lapses.
- **Optional, off by default:** `Service__Billing__PauseWritesWhenLapsed=true` also refuses `PUT /v1/backup`, `PUT /v1/sync`, `POST /v1/mailbox/{id}`, `PUT /v1/blob/{id}`, `PUT /v1/keep/{id}`, `PUT /v1/push` and the gateway with the same `402` (see *When it lapses*).
- **Endpoints:** `GET /v1/plan` (bearer): `{billing:"off"}` when disabled, else `{billing:"on", state, plan, endsAt, willRenew}`; the account pages above; `POST /billing/start`, `GET /billing/done`, `POST /billing/webhook/{provider}`. No endpoint under `/v1` returns a provider link: those are pages in a browser, behind the sign-in.
- **Admin:** the portal and `accounts list` show the plan state; `accounts pass <email> lifetime` (see [Lifetime passes](#lifetime-passes)), `accounts grant <email> --days N` (a comp, a subscription with provider `manual`), `accounts billing sync`. The admin portal can never see a card or an invoice: those are the provider's.
- **Settings** (`Service__Billing__…`, nothing by default): `Provider` (`polar`), `GraceDays` (3), `PendingHours` (24), `PauseWritesWhenLapsed` (false), and for Polar `Server` (`sandbox` or `production`), `AccessToken`, `WebhookSecret`, and the products (`Plans__cloud__Monthly`, `Plans__cloud__Yearly`). The token and the secret come from `.env` (`chmod 600`), as the other secrets do, and are never logged, printed or committed.
- **Logging:** outcomes only (`billing: webhook subscription (polar)`, `billing: bad signature (polar)`, `billing: reconciled 3`), never an address, an id or a body, like [`PushApi`](https://github.com/zburtle/Yaparr.Cloud#push-notifications-pushapi).

## Lifetime passes

- Every account that exists is `lifetime` and stays free for life; billing never changes a `lifetime` pass.
- **Signup codes** keep doing what they do now: a code the owner makes by hand gives its people the `lifetime` pass. Public sign-up is by payment; codes are the invitation. Anyone given such a code later is free for life too.
- **`accounts pass <email> lifetime`** gives an existing account the pass. If that account has a live subscription, the command cancels it at the provider at once (`CancelAsync`) so it is not billed again, and prints a reminder to refund the unused part by hand (see below). `accounts pass <email> none` takes the pass away and the account then follows the ordinary rules.

## Refunds

The policy, as decided:

- **Monthly:** a full refund if asked within **48 hours of the payment date**. After that, none.
- **Annual:** a refund of the **unused time, prorated**: the price times the days left in the period over the days in it, counted from the day of the request, rounded down to the cent.
- **Otherwise:** no refund, unless there are special circumstances, which the owner decides case by case.

How it works: the owner refunds in Polar's dashboard (the amount for an annual refund is the formula above; Polar can refund part of a payment). Polar then ends the subscription, the webhook arrives, and the account is `Lapsed` at once (principle 4; `ValidUntil` becomes now). Nothing in Yaparr issues a refund, so Yaparr never holds a payment credential. A "request a refund" button could compute the amount, since the rule is mechanical; it is not in the first version.

Two things to settle when the page that states the policy is written: Polar is the seller, so its terms and the buyer's consumer law (for example the withdrawal rights some countries give for online services) apply on top of this policy; and whether Polar returns its own fee when a payment is refunded, because that decides what a refund costs. `/subscribe` must say the policy in plain words.

## The apps (Yaparr)

- `BackupServerClient.PlanAsync()` for `GET /v1/plan`, and `BackupServerClient.SubscriptionPage(server)` for the page, beside `PasswordPage`.
- **Settings > Yaparr Cloud**, one line under the sign-in: nothing when billing is off or the account is `Lifetime`; "Active, renews 3 Dec", "Ends 3 Dec", "Payment failed: update your card", or "Subscription ended: you can't sign in on a new device until you renew. Your data is kept." with one button, **Manage subscription**, which opens the page in the browser like **Change password** does. A "Learn more about Yaparr Cloud plans" link as for every other setting ([Learn more](development.md#keeping-the-apps-short)).
- **A `402`** when signing in is shown as that same line ("Subscription ended …") with the button, and not as an unexplained failure. (If `PauseWritesWhenLapsed` is ever turned on, a `402` from sync, the mailbox, pictures or the gateway stops that work and the sync line says why.)
- **Android and the stores.** Google Play requires its own billing for digital goods sold *inside* an app, and what an app may say about buying elsewhere differs by region and has been changing. The safe shape is the one above: the app **shows the state and opens the account page in the browser, and shows no price, no "subscribe" button and no purchase wording**; the Subscribe button and the price live on the website and the browser page only. Read the current Play policy before the first release with this, and consider a build without the button for Play only if the policy asks.

## The website (Yaparr.Web)

`/subscribe` (the plan, what it holds, the price, what happens when it ends, a link to the guide, and the form), and a short `/subscribe/thanks` for the `/billing/done` fallback. The Yaparr Cloud [guide](guides/yaparr-cloud.md) gets a "Plans" section (the guides are copied to the site by `scripts/sync-learn.mjs`). The [privacy policy](https://yaparr.com/privacy) must say that payments are handled by Polar as the seller, that Polar sees the email address, card and country, that Yaparr Cloud stores only the subscription's state and ids, and what is removed when an account is deleted.

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
- An email address that is typed at checkout is the account's name, as it is for every account, and is not verified (nothing is emailed). Someone could pay for an account named with someone else's address; it is a name only, and the payer, not the address's owner, is the one who then sets the password.
- Deleting an account cancels what it pays for (above).

## Tests

- `ISubscriptions`: file and Postgres through one contract test (upsert, status mapping, `ValidUntil`, purging old events).
- `AccountPlan`: every row of the state table, with the clock injected, and `Lifetime` never changed by anything.
- `PlanGate`: a `Lapsed` account is refused a new token at each place one is made (and `Lifetime`, `Subscribed` and a subscription within its grace days are not), a token that already exists keeps working everywhere, nothing is ever removed, and the optional `402` on writes does exactly what the list says when turned on.
- The webhook: a bad signature, a stale timestamp, a repeated event, an event naming no subscription, events out of order, the provider failing (`503`, retried), and the same set against a **fake provider** so the flow is tested end to end without Polar.
- `PolarBilling`: recorded sandbox samples of the webhook and API responses; the signature check against signatures made by a Standard Webhooks reference library, so it is tested against someone else's implementation and not only our own.
- The flows: new subscriber to a working account, the done page before and after the webhook, a pending account expiring, deleting a subscribed account.

## Decisions

Settled by the owner:

| | |
|---|---|
| Price | **$4 a month or $40 a year** |
| Trial | none |
| When it lapses | new devices cannot sign in; nothing is ever removed (data is not purged today and stays so) |
| Existing accounts | free for life, and so is anyone given one of the owner's codes later |
| Refunds | monthly: full, within 48 hours of payment; annual: prorated for the unused time; otherwise none unless there are special circumstances |
| Seller and payouts | Polar, paying out through Stripe to the owner's country and bank |
| Android and the stores | as in *The apps*: state and a browser page, no price or purchase wording in the app |

Still to confirm:

1. **Lapsed accounts keep their signed-in devices.** The decision was "no logins on new devices"; this design takes that literally (devices already signed in carry on syncing). If paying should also be needed to keep syncing, turn on `PauseWritesWhenLapsed`.
2. **The annual refund reading.** "Prorated for annual" is read as available at any time; if it is meant only inside a window like the monthly one, say so and the formula gets a deadline.
3. **Consumer law and Polar's terms** against the refund policy (above), before `/subscribe` is written.
4. **The Android store policy,** read once more before the first release that shows the plan.

## Order of work

0. **The owner, in Polar:** the organization, the payout account, the sandbox, and the two products ($4 a month, $40 a year); nothing is needed in code for this.
1. **Yaparr.Cloud:** the neutral model, `ISubscriptions` (file and Postgres, schema 6), `AccountPlan`, `PlanGate` on token issuing and `GET /v1/plan`, `accounts pass`, all with billing off by default and a **fake provider**. Nothing changes for anyone yet.
2. **Yaparr.Cloud:** the webhook, reconciling, the account pages and the pending-account flow against the fake provider.
3. **Yaparr.Cloud:** `PolarBilling`, tested in the sandbox with a real test checkout.
4. **Yaparr:** the plan line, the `402` handling and the button; a guide section.
5. **Yaparr.Web:** `/subscribe`, the privacy policy, the guide.
6. Turn billing on in production, with the first checkout done by the owner.
