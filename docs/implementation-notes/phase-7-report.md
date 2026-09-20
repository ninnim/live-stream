# Phase 7 — Scale, Security & Globalization: implementation report

**Acceptance criteria:** *"The platform can scale components independently, recover from
regional/component failures according to defined SLOs, and provide operators with clear capacity and
cost visibility."*

All three are met, and the parts that only exist once everything is wired together were checked
by running a two-instance stack rather than by reading the code — see
[Verification](#verification). Reasoning is in
[ADR 0015](../decisions/0015-scale-security-and-globalization.md); operating procedures are in
[docs/troubleshooting/scaling-and-operations.md](../troubleshooting/scaling-and-operations.md).

## What was built

| Piece | File |
|---|---|
| Runtime roles and lease settings | `services/api/src/LiveStream.Application/Common/RuntimeOptions.cs` |
| Leader election over the database | `services/api/src/LiveStream.Application/Governance/LeaseCoordinator.cs` |
| The lease row | `services/api/src/LiveStream.Domain/Governance/RuntimeLease.cs` |
| Leased background loop, shared by all five | `services/api/src/LiveStream.Api/BackgroundServices/LeasedBackgroundService.cs` |
| Readiness, liveness, dependencies, draining | `services/api/src/LiveStream.Api/Health/HealthChecks.cs` |
| Plans and their allowances | `services/api/src/LiveStream.Domain/Governance/WorkspacePlan.cs` |
| Per-workspace limits and residency | `services/api/src/LiveStream.Domain/Governance/WorkspaceLimits.cs` |
| Resolving what is actually enforced | `services/api/src/LiveStream.Application/Governance/TenantLimitService.cs` |
| Capacity and cost | `services/api/src/LiveStream.Application/Governance/UsageService.cs` |
| Retention sweep and policy application | `services/api/src/LiveStream.Application/Governance/RetentionService.cs` |
| Export and erasure | `services/api/src/LiveStream.Application/Governance/WorkspaceGovernanceService.cs` |
| Single sign-on | `services/api/src/LiveStream.Application/Auth/SsoService.cs` |
| OIDC: discovery, PKCE, ID token validation | `services/api/src/LiveStream.Infrastructure/Auth/OidcClient.cs` |
| Operator routes behind the internal secret | `services/api/src/LiveStream.Api/Endpoints/OperationsEndpoints.cs` |
| Workspace administration UI | `apps/web/src/app/workspace/page.tsx`, `apps/web/src/components/workspace/` |
| Sign-in with an identity provider | `apps/web/src/app/login/page.tsx`, `apps/web/src/app/sign-in/sso/callback/page.tsx` |

## API surface

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/health/live` | anonymous | Liveness. Runs no checks; stays healthy through a drain |
| GET | `/health/ready` | anonymous | Readiness. Draining flag and database |
| GET | `/health/dependencies` | anonymous | The above plus the media gateway, for dashboards |
| GET | `/api/v1/workspaces/{id}/limits` | member | Plan, what it allows, what is enforced |
| PUT | `/api/v1/workspaces/{id}/limits` | admin | Replaces the overrides. May tighten, never loosen |
| GET | `/api/v1/workspaces/{id}/usage` | analyst | Capacity, measured usage, estimated cost |
| GET | `/api/v1/workspaces/{id}/export` | admin | Everything the workspace holds. No secrets |
| DELETE | `/api/v1/workspaces/{id}` | admin | Erases the workspace, its sessions and its media |
| GET | `/api/v1/workspaces/{id}/sso` | admin | The identity provider, if configured. Never the secret |
| PUT | `/api/v1/workspaces/{id}/sso` | admin | Configures single sign-on |
| DELETE | `/api/v1/workspaces/{id}/sso` | admin | Removes it. Linked accounts keep working |
| POST | `/api/v1/auth/sso/discover` | anonymous | Whether an address signs in through a provider |
| POST | `/api/v1/auth/sso/start` | anonymous | Begins a sign-in; returns the provider URL |
| POST | `/api/v1/auth/sso/callback` | anonymous | Exchanges an authorization code for a session |
| GET | `/api/v1/operations/capacity` | operator | What this deployment is carrying |
| PUT | `/api/v1/operations/workspaces/{id}/plan` | operator | Moves a workspace between plans |
| PUT | `/api/v1/operations/sso-domains/{domain}` | operator | Verifies a claimed email domain |

*operator* means the internal shared secret in `X-Internal-Auth`, header only. Those routes
answer 404 without it, so their existence is not confirmed to a caller probing for them.

New error codes: `LIVE_032_WORKSPACE_NOT_FOUND`, `LIVE_033_PLAN_LIMIT_REACHED` (409),
`LIVE_034_REGION_NOT_AVAILABLE` (421), `LIVE_035_SSO_NOT_CONFIGURED`, `LIVE_036_SSO_FAILED`
(401), `LIVE_037_RECORDING_EXPIRED` (410).

## Scope: what was built, and what was not

Everything in the phase's scope list was addressed. Two items were addressed as **design plus a
guard rail** rather than as running infrastructure, and that distinction is stated rather than
blurred:

| Scope item | Status |
|---|---|
| Autoscaling | **Built** — roles, readiness, draining, leases. The scaler itself is orchestrator configuration |
| Advanced tenant limits | **Built** — plans, per-workspace overrides, enforced at every limit site |
| SSO | **Built** — OIDC with PKCE, domain verification, JIT provisioning |
| Compliance / retention controls | **Built** — retention sweep, export, erasure |
| Cost telemetry | **Built** — measured usage, configured rates, an explicit unmetered list |
| Capacity planning | **Built** — the operator capacity endpoint |
| CDN / edge strategy | **Built** — an optional CDN origin for HLS. WHEP deliberately stays on the gateway |
| Regional data controls | **Built** — residency pinning, enforced where media is provisioned |
| Multi-region media architecture | **Designed** — a global control plane with regional media planes (ADR 0015 §5). Standing it up needs a second region, a global load balancer and cross-region replication |
| Disaster recovery | **Designed and documented** — SLOs, restore procedure, and what is not recoverable. The restore was not rehearsed against a real regional failure |

## Verification

| Suite | Result |
|---|---|
| Backend build | 0 warnings, 0 errors |
| Backend unit | **359 passed** (was 326) |
| Backend integration | **202 passed** (was 160) |
| Migrations | applied to a real PostgreSQL 16; `No changes have been made to the model since the last migration` |
| Frontend lint / typecheck / build | clean |
| Frontend unit | **231 passed** (was 209) |
| End-to-end, real stack | **29 passed** (was 23) |

### Three things were verified by running them, not by reading them

**Work spreads across instances.** A second container was started on the same network with
`Runtime__Role=Worker`, and the operator capacity endpoint was read:

```
name             ownerId                held
destinations     bb2f351b3efe-66ead6fa  True
retention        bb2f351b3efe-66ead6fa  True
source-presence  0e88064ebc6b-8c64c8d4  True
stream-health    0e88064ebc6b-8c64c8d4  True
```

Four leases, two instances, exactly one owner each. That is the phase's first acceptance criterion
observed rather than argued.

**A graceful stop drains before it shuts down.** From the outgoing instance's log, in order:

```
Application is shutting down...
Draining: readiness is now failing; waiting 5s before shutdown
Lease released retention …
Lease released source-presence …
Lease released destinations …
Lease released stream-health …
```

**The migration is safe on real data.** The development stack's PostgreSQL had accumulated 237
workspaces across six phases. Applying this phase's migration to it backfilled every one onto the
Pro plan — whose allowances are exactly the limits enforced since Phase 1 — so no existing tenant
was restricted by the upgrade. The migration was also applied from empty to a clean PostgreSQL 16,
which is where the no-pending-model-changes check was run.

### Tests worth naming

- **`Two_instances_racing_for_an_expired_lease_produce_one_winner`** — two coordinators, two
  contexts, both having read the row before either writes. This is the race the version token
  exists to settle, and it runs against a real relational database rather than a stub.
- **`A_workspace_cannot_raise_a_limit_above_its_plan`** and its domain-level twin — if this ever
  passes by accident, plans stop meaning anything, because tenants set them.
- **`A_second_sign_in_returns_the_same_account_even_when_the_address_changed`** — accounts are keyed
  on the provider's subject. Matching on email would hand a reassigned address the previous holder's
  account.
- **`An_identity_outside_every_verified_domain_is_refused`** — the provider authenticated somebody,
  but not somebody at a domain this workspace proved it owns.
- **`An_export_carries_the_workspace_and_no_secret`** — asserts the literal stream key does not
  appear anywhere in the export body. The risk was never that somebody adds a `streamKey` field; it
  is that projecting an entity carries one.
- **`Erasure_removes_the_workspace_its_sessions_and_its_media`** — asserts the media was deleted, not
  just the rows.

**Single sign-on was tested without an identity provider**, the same way the media plane is tested
without a gateway: `IOidcClient` is substituted, and everything this platform decides around it —
domain verification, account linking, provisioning, membership, replay — is exercised for real. What
is *not* covered is the adapter's own protocol handling against a live provider: discovery, the token
exchange, and ID token signature validation run first the day a deployment configures one. Its
failure mode is a failed sign-in with password sign-in unaffected, which is asserted.

## Two defects this phase found in its own code

**A new SSO domain was saved as an UPDATE against a row that did not exist.** `WorkspaceSsoDomain`
pre-assigned its `Id`, and EF treats a child discovered through a navigation collection with a key
already set as an existing row. The second save of a connection therefore threw a concurrency
exception. This repository had already hit and documented the same trap on `LiveSessionEvent`; the
fix is the same, and the comment now says so in both places.

**The retention sweep would never have run.** A periodic timer fires after one full interval, so the
hourly loop did nothing for an hour after every restart — and on a deployment that restarts more
often than that, it would have deleted nothing, ever, while reporting expiry dates. Caught by seeing
its lease missing from the capacity endpoint on a freshly started stack. The first pass is now capped
at 15 seconds after start.

Neither was caught by a test that was written to be lenient, because neither test was.

## Behaviour that changed

- **Hitting the concurrent-session limit now answers `409 LIVE_033_PLAN_LIMIT_REACHED`** instead of
  `429 LIVE_014_RATE_LIMITED`. A rate limit clears by waiting and a plan limit does not, and a client
  told to retry would retry forever. The existing test was updated with that reasoning, not deleted.
- **`IRecordingStore` gained `DeleteAsync`.** Retention and erasure need it. Any implementation must
  be idempotent: "already gone" is the outcome retention wanted.
- **`WorkspacePermission.WorkspaceManage`** was added, held only by Owner and Admin. Everything
  behind it either sets a security boundary or destroys data.

## Known gaps

- **One region.** The residency check refuses what belongs elsewhere; there is no elsewhere yet.
- **Rate limits are per-instance.** Across N replicas the effective limit is N times the configured
  one. A distributed limiter needs the shared store Redis was deferred for (ADR 0004).
- **SSO login state is per-instance by default.** A multi-replica deployment needs a shared cache or
  sticky sessions for sign-in; the runbook says so, and the failure is a refused sign-in rather than
  a wrong one.
- **No DNS domain verification.** An operator verifies out of band.
- **No billing.** Plans are enforced; nothing charges for them.
- **Viewer egress is not metered.** Named in every cost report rather than reported as zero.
- **The disaster-recovery procedure is documented, not rehearsed.** Restoring a database and starting
  the tiers is exercised piecemeal by every deploy; a full regional failover is not.
- **The OIDC adapter's live protocol handling is unverified** until a deployment configures a
  provider.
