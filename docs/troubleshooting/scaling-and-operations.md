# Scaling and operations

Running this platform as more than one process, and getting it back when something breaks.
Reasoning is in [ADR 0015](../decisions/0015-scale-security-and-globalization.md); this is the
procedures.

## Composing the binary into tiers

One image, three roles, set with `Runtime__Role`:

| Role | Serves HTTP | Runs background loops | Use it for |
|---|---|---|---|
| `All` (default) | yes | yes | A single container. Anything smaller than a fleet |
| `Api` | yes | no | Request volume. Add replicas freely; they add no background work |
| `Worker` | yes | yes | Sessions in flight. Scale on live sessions, not on requests |

A `Worker` still serves HTTP, so its readiness probe works; put it behind no load balancer.

**Leave `Runtime__LeaderElection` on for anything with more than one instance.** Two unleased
replicas reconcile the same session and pay for the same AI job twice. Turning it off saves one small
database write per loop per tick, and is only safe when exactly one instance runs the loops.

### Verifying that the split actually worked

```bash
curl -s -H "X-Internal-Auth: $INTERNAL_API_SECRET" \
  http://localhost:8080/api/v1/operations/capacity | jq '.leases'
```

Each lease should name exactly one owner. With two instances running you should see the four leases
distributed between them — that is the whole claim, and it is visible here:

```json
[
  { "name": "destinations",    "ownerId": "bb2f351b3efe-66ead6fa", "held": true },
  { "name": "retention",       "ownerId": "bb2f351b3efe-66ead6fa", "held": true },
  { "name": "source-presence", "ownerId": "0e88064ebc6b-8c64c8d4", "held": true },
  { "name": "stream-health",   "ownerId": "0e88064ebc6b-8c64c8d4", "held": true }
]
```

A lease with `"held": false` is free and will be taken on the next tick of whichever loop wants it.
That is normal right after a deploy; it is a problem only if it stays false for longer than the
loop's interval.

## Health probes

| Path | Use it as | Fails when |
|---|---|---|
| `/health/live` | Liveness probe | The process is not answering at all |
| `/health/ready` | Readiness probe, load balancer target | Draining, or the database is unreachable |
| `/health/dependencies` | Dashboard only | Never fails readiness; reports the media gateway |

Kubernetes probes these over HTTP directly. Docker Compose needs a command inside the container, and
the runtime image ships no HTTP client on purpose — so the compose file declares no healthcheck.
Probe from outside instead.

**Set the readiness probe interval below `Runtime__ShutdownDrainSeconds` (default 5).** The drain
only helps if the load balancer notices it.

**Set the container stop grace period above the drain plus lease handover.** The compose file uses
`stop_grace_period: 30s`. A killed instance keeps its leases until they expire, which is exactly the
pause the drain exists to avoid.

## Rolling deploy: what a good one looks like

In the outgoing instance's logs, in this order:

```
Application is shutting down...
Draining: readiness is now failing; waiting 5s before shutdown
Lease released retention owner=…
Lease released source-presence owner=…
Lease released destinations owner=…
Lease released stream-health owner=…
```

Then in the incoming instance's logs, within a second or two:

```
Lease acquired stream-health owner=… fencingToken=2
```

If instead you see the new instance idle for up to 30 seconds and then a takeover, the old one was
killed before it could hand over: the stop grace period is too short, or something is blocking
shutdown.

## Tenant limits and plans

Read what a workspace is allowed (any member):

```bash
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:8080/api/v1/workspaces/$WORKSPACE_ID/limits | jq
```

Move it between plans (operator only — nobody inside the tenant can raise their own entitlement):

```bash
curl -X PUT -H "X-Internal-Auth: $INTERNAL_API_SECRET" \
  -H 'Content-Type: application/json' -d '{"plan":"Business"}' \
  http://localhost:8080/api/v1/operations/workspaces/$WORKSPACE_ID/plan
```

### "This workspace already hit a limit and I raised the plan, but nothing changed"

The effective limit is the *tightest* of the deployment cap, the plan, and the workspace's own
override. Raising a plan above the deployment cap has no effect until the operator raises the cap
too — `Distribution__MaxDestinationsPerSession`, `Sources__MaxSourcesPerSession`,
`LiveSessions__MaxConcurrentSessionsPerWorkspace`. The `limits` response shows both numbers, so
compare `effective*` against `planMax*`.

## Single sign-on

A workspace admin configures the connection; **it does nothing until an operator verifies a domain**:

```bash
curl -X PUT -H "X-Internal-Auth: $INTERNAL_API_SECRET" \
  -H 'Content-Type: application/json' -d '{"verified":true}' \
  http://localhost:8080/api/v1/operations/sso-domains/example.com
```

Verify a domain only for a workspace you have confirmed owns it, out of band. Whoever holds a domain
can sign in as anyone with an address there.

### Sign-in fails with "We could not complete that sign-in"

One error code covers every cause on purpose, so a probe cannot learn how far it got. The server log
says which:

| Log line | Cause |
|---|---|
| `SSO code exchange failed` | The provider rejected the code, the secret is wrong, or discovery failed |
| `ID token … failed validation` | Issuer, audience, signature or expiry mismatch. Usually the client id |
| `carried the wrong nonce` | The token is not from the sign-in that started here |
| `outside every verified domain` | The person's address is not in a verified domain |
| `carried no verified email` | The provider did not vouch for the address |

Nothing logged at all, and the state was rejected outright: the sign-in landed on a different
replica from the one that started it. `IDistributedCache` is in-memory by default — configure a
shared cache, or use sticky sessions.

### The redirect URI is rejected by the provider

It must match exactly what the provider has registered, including scheme and trailing path. The
value the platform will use is in the `redirectUri` field of the SSO connection response, derived
from `Sso__WebAppBaseUrl` (which defaults to `WEB_ORIGIN`).

## Retention

```bash
# Which recordings are due, from the operator view.
curl -s -H "X-Internal-Auth: $INTERNAL_API_SECRET" \
  http://localhost:8080/api/v1/operations/capacity | jq '.recordings'
```

`pendingDeletion` counts recordings past expiry that the sweep has not yet reached. A number that
grows across hourly sweeps means deletion is failing — check the API log for
`Retention could not delete recording`.

**Recordings finalized before this phase have a null expiry and are never swept.** That is deliberate:
an upgrade must not schedule a back catalogue for deletion. To apply a policy to them, save the
workspace's retention setting — that recomputes expiry for everything already stored.

**`Governance__RetentionEnabled=false`** keeps reporting expiry dates and deletes nothing. Use it
while validating a policy.

## Disaster recovery

### What has to survive

| Data | Where it lives | Recovery |
|---|---|---|
| Control plane (sessions, users, workspaces, limits, SSO config) | PostgreSQL | Point-in-time restore. RPO ≤ 5 min, RTO ≤ 30 min |
| Recorded media | Object storage / shared volume | Restore from storage's own replication or backups |
| Secrets encryption key | `Secrets__Keys__<id>` | **Not recoverable.** Losing it makes every stored destination credential and SSO client secret permanently unreadable |
| Live media in flight | Nowhere. It is in flight | Not recoverable. Broadcasts go RECONNECTING and resume inside the recovery window |

The encryption key is the one irreplaceable item. Back it up separately from the database, because a
backup that contains both is a backup that leaks everything if it leaks at all.

### Restoring the control plane

1. Stop the API and worker tiers. Leave the media gateway running: viewers watching an existing
   stream are unaffected by a control-plane restore.
2. Restore the database to the target point in time.
3. Start **one** instance with `Database__MigrateOnStartup=true` and let migrations settle. In a
   fleet, run migrations as a deploy step instead — several instances migrating at once is a race.
4. Start the rest. Background loops take their leases within 15 seconds and reconcile every active
   session against the media plane, which is what corrects state that drifted during the outage.
5. Sessions that were LIVE when the outage began and whose publisher has gone are failed after the
   recovery window. That is intended: they are not on air, and the record should say so.

### Verifying a restore before trusting it

```bash
curl -s http://localhost:8080/health/dependencies | jq
curl -s -H "X-Internal-Auth: $INTERNAL_API_SECRET" \
  http://localhost:8080/api/v1/operations/capacity | jq '{workspaces, sessionsByStatus, leases}'
```

Workspace count matching the pre-incident figure, and leases held, is the cheapest end-to-end proof
that the schema, the data and the loops all came back.

### Regional failure

One region is served today (ADR 0015 §5). A regional outage is a full restore into another region:
restore the database, point `Runtime__Region` at the new region, and be aware that any workspace with
a residency requirement pinned to the lost region will be refused there by design — that refusal is
correct, and lifting it is a decision for whoever owns the data-residency commitment, not an
incident-time workaround.

## Cost reporting

Rates are configured per deployment (`Costs__*`). **An unset rate reports null, not zero** — the line
appears with `—` rather than `$0.00`, because a cost shown as free is one nobody budgets for.

The report also names what the platform does not meter. Viewer delivery is the important one: no
part of this platform counts bytes per viewer, so that cost has to come from the CDN's own billing.
