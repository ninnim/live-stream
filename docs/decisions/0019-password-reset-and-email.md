# ADR 0019 — Password reset, and the first outbound email

**Status:** Accepted
**Date:** 2026-09-20
**Supersedes part of:** [ADR 0003 — authentication and tenancy](0003-authentication-and-tenancy.md)

Until now a forgotten password was unrecoverable. The platform had no way to send an email, so it
had no way to prove somebody owned an address, so it had no way to let them back in. This adds both,
and the email half is deliberately the smaller of the two.

## 1. The endpoint must not say whether an account exists

`POST /auth/forgot-password` is the only unauthenticated endpoint that takes an email address and
does different work depending on whether it belongs to somebody. That makes it the natural place to
enumerate the platform's users, and every difference in its behaviour is a signal.

So all of these are answered **identically — `202 Accepted`, empty body**:

| What happened | What the caller sees |
|---|---|
| A link was emailed | `202` |
| No account with that address | `202` |
| The account is suspended | `202` |
| The address was malformed | `202` |
| The mail server refused the message | `202` |

The last two cost something. A malformed address gets no validation error, which means a genuine
typo produces the same reassuring "check your email" as a real request — the alternative is a third
distinguishable answer, and a probe learns as much from "that is not an email address" as from
"no such account". And a refused message is not reported, because the person asking cannot fix a
mail server; the operator can, and gets a log line naming it.

The **page says the same thing**, which matters as much as the endpoint doing so. Copy reading "no
account with that address" would undo all of the above in one line.

## 2. The token is stored hashed, single-use, and expires in an hour

Modelled on the existing `RefreshToken`: 256 bits from a cryptographic generator, and only the
SHA-256 of it is ever written down. The plaintext exists for the few milliseconds it takes to put
in an email. A stolen database backup therefore contains no way into anybody's account.

SHA-256 rather than a password hash, deliberately. A slow hash exists to make guessing a
low-entropy human-chosen secret expensive; there is nothing here to guess, so a slow hash would
only make our own lookup expensive.

A reset link is an unusual credential: it arrives by email, sits in an inbox indefinitely, and gets
forwarded by people who do not think of it as a credential at all. **One hour, and one use.**
Asking again invalidates the previous link, so somebody who requests twice because the first mail
was slow does not end up with two live ways into their account.

Expired, already used, and never-valid all return **one error code**. Telling them apart would let
somebody holding a stolen link learn whether it had been used — which is exactly what they would
want to know, and exactly what its owner needs them not to.

## 3. Resetting signs out every session

Not "every other session" — there is no session here to keep.

Somebody resets a password for one of two reasons: they forgot it, or they think somebody else has
it. The second is the one that matters, and leaving the intruder's refresh token alive would make
the reset useless for precisely the person who needed it most. The page says so plainly rather than
burying it, because being signed out on your phone is a surprise worth warning about.

## 4. Email is off by default, and says so loudly

The platform ships without a mail server, and `Email:Enabled` defaults to false. What it must never
do is fail quietly: with it off, **every message is written to the API's log in full**, so a
developer can complete a reset end to end without configuring anything —
`docker compose logs api` carries the link.

Delivery is a **dependency, not a side effect**: `IEmailSender.SendAsync` returns whether the
message was accepted and never throws for a failure it cannot control. A mail server being down is
a logged warning, not a 500 on somebody's password reset. The token still stands, so they can ask
again once it is fixed.

**MailKit, not `System.Net.Mail.SmtpClient`** — Microsoft's own documentation says not to use the
latter for new work, and a password reset is the one message that has to actually arrive. The first
version pinned MailKit 4.8.0 and the build refused it: `NU1902`, a known moderate-severity
advisory, treated as an error by this repo's settings. That is the dependency policy working, and
4.18.0 is what shipped.

## 5. What is verified, and how

The end-to-end spec runs against a **real mail server** — a disposable Mailpit in
`docker-compose.e2e.yml`, exactly as the RTMP receiver stands in for a streaming platform. It reads
the link back out of a genuine inbox, which is the only way to exercise the SMTP handshake, MailKit,
and the message this platform actually composes. Reading the API's log instead would have tested
the fallback and left the path that matters in production untested.

## 6. What this does not do

**No email verification on sign-up.** An address is still unproven until somebody resets a password
with it. The machinery to change that now exists.

**No notification that a password changed.** The standard second email — "your password was just
changed, and if that was not you…" — is worth having and is not built.

**No teammate invitations**, which was the other thing blocked on having no email at all.

**Rate limiting is per-instance.** These endpoints inherit the `Authentication` policy, which is the
right limit and the wrong scope across replicas — the same Redis-shaped gap as everything else
(ADR 0004).
