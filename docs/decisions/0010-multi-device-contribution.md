# ADR 0010 — Multi-device contribution

**Status:** Accepted
**Date:** 2026-09-04
**Phase:** 4 — Multi-Device & Collaboration

How additional devices join a Live Session without ever holding a permanent credential.

## 1. Codes and tokens, not accounts

A phone joining a broadcast has no account and should not need one. It redeems a short-lived,
single-use **pairing code** for a short-lived, source-scoped **device token**.

| | Pairing code | Device token |
|---|---|---|
| Shape | 8 characters, 30-letter alphabet | 256 random bits, URL-safe |
| Lifetime | 10 minutes | 12 hours |
| Uses | Exactly one | Until expiry or revocation |
| At rest | SHA-256 hash | SHA-256 hash |
| Returned | Once, on invitation | Once, on redemption |

The code is short because someone is reading it off a screen and typing it into a phone, so the
alphabet excludes `0/O/1/I/L/U`. Eight characters is roughly 39 bits — far weaker than a token, and
deliberately so. What makes guessing impractical is not length but the combination of a **ten-minute
expiry**, **single use**, and a **rate limit of 10 attempts per minute per address**. All three are
load-bearing; remove any one and the limit becomes the only thing standing between an eight-character
guess and a live broadcast.

Redemption erases the code rather than flagging it, so a spent code is unusable even if the status
check were ever bypassed.

## 2. Device authentication is database-backed, not JWT

`docs/05-multi-device.md` requires that **"Revocation is immediate."** A self-contained bearer token
cannot do that: it stays valid until it expires, and making it revocable means maintaining a
denylist — which is a database lookup wearing a disguise.

So devices authenticate through their own ASP.NET scheme that resolves the presented token against
the database on every request. The cost is one indexed seek on a hash; the benefit is that an
operator pressing **Remove** locks the device out of its very next request.

The two schemes are strictly separate. A device token presented as `Bearer` is rejected, and a user's
access token presented as `Device` is rejected — both are covered by tests, because the failure mode
if they were interchangeable is a borrowed phone holding a producer's rights.

## 3. Roles are assigned by the operator, never chosen by the device

The role travels with the invitation, not with the redemption. Whoever scans the code gets exactly
the capabilities the operator picked.

| Role | Publish | View | Switch program | Manage devices | Moderate |
|---|---|---|---|---|---|
| Host | ● | ● | ● | ● | |
| Camera / Screen / Audio | ● | ● | | | |
| Operator | | ● | ● | ● | |
| Moderator | | ● | | | ● |
| Viewer monitor | | ● | | | |

A contributing camera deliberately cannot switch program or manage devices. Handing a borrowed phone
the ability to cut the show — or to revoke the operator who lent it — is not a capability anyone
intends to grant, and it would be an easy accident if roles were a single "device" bit.

Roles that send no media get **no ingest path at all** rather than an unused one.

## 4. One path per source

Each contributing source publishes to its own unguessable gateway path. Two devices cannot share a
path, and a per-source path is what lets one device be revoked without disturbing the others.

The **studio source publishes to the session's own path**, so a single-camera broadcast behaves
exactly as it did before multi-device existed — Phase 1's verified flow is untouched, and the extra
machinery only appears when a second device arrives.

## 5. Revocation severs media, not just credentials

Three things happen together when a device is removed, and all three are necessary:

1. its stored secrets are **erased** (not flagged);
2. every outstanding ingest credential for its path is **revoked**;
3. whatever is currently publishing is **kicked at the gateway**.

Without the third, a revoked phone keeps streaming until its credential expires — up to five minutes
of video from a device an operator has explicitly removed. The media plane authorizes at connect
time, so nothing short of severing the connection actually stops it.

The gateway kick covers both WebRTC and RTSP publishers. Checking only one would make revocation work
for phones and silently not for anything else.

## 6. Presence is observed, never declared

A source shows as connected because the media plane reported media on its path, not because the
device said so. That is what makes the control room trustworthy when a phone dies silently rather
than disconnecting politely.

A short grace period (20s) sits in front of "disconnected", for the same reason sessions have a
recovery window: a phone switching between cell and wifi has not left the show.

Source reconciliation runs in its **own background loop**, separate from session health and
destination monitoring. Three loops, three blast radii: presence for a borrowed phone must never be
able to interfere with the reconciliation that keeps a broadcast alive.

## 7. The operator's name wins

The join screen has no "name this device" field, and the API ignores any label a device sends when
setting the display name.

This was a change made after seeing it in a real browser. The device originally named itself, which
meant an operator who deliberately typed "Stage left camera" watched it silently become "Windows PC"
the moment someone scanned the code. The device's platform is still recorded — in the pairing audit
event, where it is useful for diagnostics and cannot overwrite a deliberate choice.

## 8. Program switching is deferred to Phase 5

Sources can be seen, monitored and managed. Choosing which one viewers watch is **not** implemented,
and the reason is worth recording.

### What was tried

MediaMTX can proxy one path from another (`source: rtsp://…`), and its API can add paths at runtime.
Repointing a "program" path at a different source would have made switching a single control-API
call, with no re-encoding at all.

**It crashes the gateway.** Adding a path through the config API on 1.11.3 panics:

```
panic: runtime error: invalid memory address or nil pointer dereference
[signal SIGSEGV: segmentation violation code=0x1 addr=0x30 pc=0xd90778]
github.com/bluenviron/mediamtx/internal/recordcleaner.(*Cleaner).ReloadPathConfs(...)
	/s/internal/recordcleaner/cleaner.go:54
```

The whole media plane goes down and every session with it. **Never mutate gateway path
configuration at runtime.**

### What remains possible

The relay built in Phase 2 can feed a program path instead: pull the chosen source over RTSP, push
it into the session path, `-c copy` on both sides so there is no re-encoding. Switching becomes
restarting one relay against a different source.

It is deferred rather than done because it changes where the session's *program* comes from, and
therefore touches Phase 1's verified media flow — the studio would stop publishing directly into the
session path. That is a real rework of working software, and it belongs with the composition
pipeline in Phase 5 rather than bolted onto presence.

The model already carries `IsProgram`, and the API exposes it, so Phase 5 adds the switch without a
migration. `docs/05-multi-device.md` anticipates this: *"Full multi-source composition and automatic
switching belongs in the professional studio phase."*
