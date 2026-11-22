# Email

Yaparr Cloud sends email for one purpose: **a link that the person asked for**. It is built (Yaparr.Cloud, "Email" in its README) and **off unless the server is told how to send** (`Service__Mail__Host`), so a server that is run for one's friends or one's own network is exactly what it was.

## What is sent, and nothing else

- **A link to choose a password**, to the address, after a payment and after a sign-up code. The address is proved to be the person's because the page that chooses the password is reached from the mail. (Before this, the link was on the page that follows a payment, and an address was never proved: someone could pay for an account named with another person's address.)
- **A link to reset a forgotten password**, from `/account/forgot`. Choosing a new password signs every device out. Backups and synced data are locked with a key on the person's devices, not with the password, so a reset does not open them.
- **A new link for someone who paid and lost theirs**, from the same page.

Never marketing, never a newsletter, never a notice nobody asked for. Receipts, and notices that a payment failed or a subscription renews, come from the payment provider, which is the seller and already has the address (see [billing](billing.md)); Yaparr shows the state in the app.

## How it behaves

- **The same answer always.** The page answers identically for an address with an account and one without, and mail is sent in the background, so neither the words nor the time say who is known. Each try counts against the place it came from and against the address (eight in a quarter of an hour), so the page cannot fill someone's inbox.
- **Links.** Random, one-use, short-lived (an hour to reset; half an hour after a sign-up code; a day after a payment). A reset link is kept in memory as a hash, so a restart ends them. Opening a link only shows the form: a mail scanner that fetches it uses up nothing.
- **What is kept.** No copy of a message; the log says that a mail was sent or could not be, never to whom. A mail service delivers it and sees the address and the message while it does (the [privacy policy](https://yaparr.com/privacy) names it).
- **How it is sent.** Any SMTP server (`MAIL_HOST` and the others in `.env`; `starttls` on 587 by default). `IMailer` is the one place a different way of sending, such as a provider's API, would be added. The sending domain needs SPF, DKIM and DMARC or the mail lands in spam.

## Decisions

- **Why email at all.** Without it a forgotten password had no remedy but the owner, a lost link after paying had none either, and addresses were names that nobody had proved. A link is the smallest thing that fixes all three.
- **Why only that.** Every other message a service could send is one that somebody did not ask for; the apps already show the state of an account, and the provider sends what it must.
- **Not done:** a verified-address check for accounts made before this; a notice when a password is reset (a mail that nobody asked for, though a useful alarm: worth deciding on its own).
