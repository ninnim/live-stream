# ADR 0015 — Scale, security, and globalization

**Status:** Accepted
**Date:** 2026-09-06
**Phase:** 7 — Scale, Security & Globalization

How this platform becomes something you can run as more than one process, sell to more than one kind
of customer, and operate without guessing what it costs.

## 1. The acceptance criteria are three separate problems

> *"The platform can scale components independently, recover from regional/component failures
> according to defined SLOs, and provide operators with clear capacity and cost visibility."*

| Criterion | What it actually required | What would have broken it |
|---|---|---|
| Scale components independently | A role that composes the binary into a tier, and leases so background work does not double when replicas do | Adding replicas and hoping the loops were idempotent |
| Recover per defined SLOs | Real readiness, a drain that precedes shutdown, leases that expire, and stated numbers | A `/health` that returns 200 unconditionally |
| Capacity and cost visibility | Measured quantities, and an explicit list of what is *not* measured | A cost report that silently shows zero for what nobody meters |

## 2. Background work is leased, because replicas are the point

Every background loop in this platform is a singleton by nature. Two instances reconciling one
session race each other into the recovery window; two instances running one AI job pay for it
twice; two instances sweeping retention delete the same media twice. Before this phase that was
guaranteed by there being one instance — which is exactly the guarantee "scale independently"
removes.

So each loop takes a **named, expiring lease** before each pass, and skips the pass if it cannot.

**Why the database and not a lock service.** Every replica already depends on the database, so using
it adds no new failure domain. etcd or Redis would add one, and would be another thing to run for a
platform that deliberately deferred Redis (ADR 0004).

**Why optimistic concurrency and not `pg_try_advisory_lock`.** An advisory lock is the more obvious
tool, and it is PostgreSQL-only — the integration suite runs on SQLite (ADR 0006), so a lock that
only exists in production would be a coordination mechanism verified nowhere. A version column and
a conditional update behave identically on both, and `LeaseCoordinatorTests` runs two real
coordinators against one real database and asserts that exactly one wins.

**Why the version moves on renewal too.** If renewals left it alone, an instance that had read an
expired lease could commit a takeover just after the owner renewed it, and both would believe they
held the work. The fencing token still moves only on takeover, so a takeover stays detectable.

**Why one lease per loop rather than one per instance.** Work spreads. Verified on a real two-container
deployment: the API instance held `stream-health` and `source-presence` while the worker instance
held `destinations` and `retention` — four leases, two instances, one owner each.

**Why the first pass does not wait a full interval.** A periodic timer fires after one interval, so
an hourly loop would do nothing for an hour after every restart — and on a deployment that restarts
more often than that, retention would never run at all. The first pass is capped at 15 seconds after
start.

## 3. Readiness is about this instance, not about everything it talks to

Three probes, and the distinction between them is the whole design:

| Probe | Answers | Includes |
|---|---|---|
| `/health/live` | Is this process alive? | Nothing. It stays healthy through a drain — an orchestrator that saw it fail would kill the process instead of letting it finish |
| `/health/ready` | Should this instance be sent traffic? | Draining flag, database |
| `/health/dependencies` | What does this instance see? | The above plus the media gateway |

**The media gateway is deliberately not part of readiness.** If it were, a media-plane outage would
take every API instance out of the load balancer at once, and replace a degraded platform with an
unreachable one — while the control plane could still list sessions, show health, end broadcasts,
and say what is wrong. The gateway failure surfaces where it belongs: on the session being prepared.

**Draining precedes shutdown, and that ordering is load-bearing.** Hosted services stop in reverse
registration order, so the drain service is registered last and therefore stops first: readiness
starts failing, the load balancer stops choosing this instance, and only then does anything else
begin tearing down. The pause is configurable and must exceed the load balancer's probe interval.

Verified on the running stack: `Draining: readiness is now failing` followed by four
`Lease released` lines, then exit.

## 4. Service level objectives

These are what the design above buys, and what the runbook in
`docs/troubleshooting/scaling-and-operations.md` measures against.

| Objective | Target | What enforces it |
|---|---|---|
| Control-plane API availability | 99.9% monthly | Stateless API replicas behind a load balancer that honours readiness |
| A live session survives an API instance dying | Always | Session state is in the database, not in a process; media flows through the gateway, which the control plane is not in the path of |
| Background work resumes after an instance is lost | ≤ 30 s (the lease TTL) | Lease expiry; a graceful stop hands over in ~1 s instead |
| Background work resumes after a graceful deploy | ≤ 5 s | Lease release on shutdown |
| Ingest loss is detected | ≤ 6 s | Health monitor at 3 s, two passes |
| A dropped broadcaster is failed rather than left hanging | 120 s | `LiveSessions:RecoveryWindowSeconds` |
| Recording metadata survives any single component restart | Always | Written transactionally at finalize; media on shared storage |
| RPO for control-plane data | ≤ 5 min | Point-in-time recovery on the managed database |
| RTO for a single region | ≤ 30 min | Documented restore procedure; media and recordings restored from object storage |

**A live broadcast does not survive losing its media gateway**, and no amount of control-plane
availability changes that: the media is flowing through that process. What the platform guarantees
is that the session is not lost — it goes RECONNECTING, and the broadcaster can resume inside the
recovery window.

## 5. Multi-region: what was built, and what was designed

**Built.** A deployment declares the region it serves (`Runtime:Region`). A workspace on a plan that
includes data residency may pin itself to a region. Preparing a session in the wrong region fails
with `LIVE_034_REGION_NOT_AVAILABLE` rather than being served quietly — because serving it would
put the tenant's media in a region they excluded, and nothing downstream would ever notice.

**Designed, not stood up.** The intended shape is a *global control plane with regional media
planes*: one database (with read replicas), and an API + gateway + relay + recording store per
region behind a latency-based router. Session rows are global; media is regional. What is missing
to run it is infrastructure rather than code — a second region, a global load balancer, and
cross-region database replication — none of which can be stood up or verified here, so none of it is
claimed as working.

The residency check is what makes that architecture safe to adopt incrementally: a mis-routed
request fails loudly today, before anyone has built the routing.

## 6. Plans set the ceiling; a workspace can only tighten

Three things decide what a tenant is allowed, and the answer is always the tightest:

1. **The deployment's caps** — a resource guard. No plan can make one machine serve more than it can.
2. **The plan** — a commercial entitlement. Operator-set, through the internal operations API.
3. **The workspace's own overrides** — a customer choosing to be stricter than they have to be.

**A workspace admin may lower a limit, never raise one.** If they could raise it, the plan would mean
nothing, because the tenant would be setting it. This is enforced twice on purpose: when a value is
set, so an admin gets a clear error, and when it is read, so a row written before a plan downgrade
cannot leave a workspace above its entitlement.

**A plan limit answers 409, not 429.** A rate limit clears by waiting; a plan limit does not. A client
told to retry a request that cannot succeed until the plan changes would retry forever.

**New workspaces default to Pro**, whose allowances are exactly the limits this platform has enforced
since Phase 1. Turning plans on therefore changes nothing until an operator moves somebody, and the
migration backfills every existing workspace onto it — an upgrade must not silently restrict a
customer who was never told about plans.

## 7. Single sign-on: a domain is a security boundary, not a setting

The rule the whole feature turns on: **a workspace may claim an email domain, but only an operator
can verify it, and only a verified domain signs anybody in.**

Whoever holds a domain decides who may sign in with an address in it. A self-service claim on
`example.com` would not be a configuration mistake; it would be an account-takeover primitive. So a
claimed domain is stored, shown as unverified, and does nothing at all until an operator verifies it
through the internal operations API. (DNS `TXT` verification is the obvious next step and is not
built; the operator step is what makes the feature safe without it.)

Three consequences follow from the same reasoning:

- **Accounts are keyed on the provider's `sub` claim, never on email.** An address can be reassigned
  inside a company, and matching on it would hand the new holder the previous holder's account.
- **JIT provisioning cannot grant Owner or Admin.** An identity provider must never be able to mint
  somebody who can then reconfigure the identity provider.
- **An existing membership is never re-roled on sign-in.** Somebody promoted to Admin must not be
  demoted to the connection's default role every time they authenticate.

**PKCE is used even though this is a confidential client with a secret.** It costs one hash and it
closes code interception at the redirect. **The nonce is checked against the one minted when the
redirect was issued**, which is what stops a token obtained in one sign-in being replayed into
another. **The ID token is validated against the provider's published keys, issuer and audience** —
an unvalidated ID token is just a string the browser handed us.

**The callback lands on the web app, not the API.** The web app posts the code to the API and gets
tokens in a response body. Having the API receive the redirect and bounce back with tokens in the URL
would be simpler and would write them into browser history and every proxy log on the way.

**Every SSO failure returns one error code.** Distinguishing "unknown state" from "bad code" from
"domain not verified" would tell somebody probing the endpoint exactly how far they got.

## 8. Retention deletes bytes, and expiry is stored rather than derived

A retention policy that is only written down is not a retention policy. An hourly sweep deletes
recordings whose window has elapsed, in bounded batches so that tightening a policy from a year to a
week drains steadily instead of hammering storage.

**Expiry is stored on the recording.** The sweep is then one indexed query however many workspaces
exist, and a viewer can be told the date their recording goes away. The cost is that changing a
policy has to rewrite the recordings it covers — a rare admin action, not an hourly one.

**Recordings that predate retention keep a null expiry.** An upgrade must not schedule a customer's
back catalogue for deletion. Saving the policy is the deliberate act that opts history in.

**Media that cannot be deleted leaves its row alone.** Marking it Deleted anyway would erase the
platform's only record of files that still exist.

## 9. Erasure deletes media before rows

If the rows went first and media deletion then failed, the platform would hold files it no longer has
any record of, and no way to find them again. This order can only leave the opposite — a row whose
media is already gone, which the next attempt cleans up.

Erasure requires the workspace name typed back, and is refused while a session is on air.

## 10. Cost is measured where it can be, and named where it cannot

Every quantity comes from a stored row: broadcast hours from session timestamps, relay egress from
the bytes each relay actually sent, AI spend from the tokens Phase 6 recorded per job.

**A rate that is not configured reports null, not zero** — the same rule Phase 6's pricing follows,
for the same reason: a cost line showing $0.00 reads as free, and nobody budgets for free.

**What the platform does not measure is listed in the response.** Viewer delivery is the important
one: nothing here meters bytes per viewer, so a report showing 0 GB of viewer egress would be a
figure an operator would budget against and be wrong.

## 11. Operator routes are not member routes

Plan changes and domain verification are reached with the internal shared secret, not a user token,
because neither belongs to a tenant: a plan is what a customer is entitled to, and a domain
verification decides whether a workspace may sign in everyone at an address.

They answer **404 without the secret**, so an unauthenticated caller cannot learn the operations API
exists. Header only — no query-string fallback. The media gateway needs that fallback because it
cannot set headers; operator tooling can, and a secret in a URL ends up in logs.

## 12. What this is not

- **Not multi-region.** One region is served; the residency check refuses what belongs elsewhere.
- **No autoscaling policy.** The platform is now shaped so an autoscaler can work — roles, readiness,
  draining, leases — but the scaler itself is orchestrator configuration.
- **Rate limits remain per-instance.** They partition by user and fall back to IP, in process. Across
  N replicas the effective limit is N times the configured one. A distributed limiter needs the
  shared store Redis was deferred for.
- **SSO login state is per-instance by default.** It uses `IDistributedCache`, which is in-memory
  unless Redis is configured — so a multi-replica deployment needs a shared cache or sticky sessions
  for sign-in, and the deployment guide says so.
- **No DNS domain verification.** An operator verifies; the platform has no way to prove ownership
  itself.
- **No billing.** Plans exist and are enforced; nothing charges for them.
