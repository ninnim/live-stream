# Phase 6 — AI Live Operations: implementation report

**Acceptance criteria:** *"AI features are asynchronous, observable, permissioned, and independently
disableable. Core streaming remains functional when AI services are unavailable."*

All five are met, and the last one is asserted by a test rather than argued for. Reasoning is in
[ADR 0014](../decisions/0014-ai-live-operations.md).

## What was built

An AI job pipeline that runs away from the broadcast path: a queue, a worker, a retry policy that
distinguishes failures worth retrying from failures that are final, per-feature switches, and cost
accounting. Plus three analyses that run on it.

| Piece | File |
|---|---|
| Job aggregate and lifecycle | `services/api/src/LiveStream.Domain/Ai/AiJob.cs` |
| Provider seam | `services/api/src/LiveStream.Application/Abstractions/IAiAnalyst.cs` |
| Queueing, permissions, capabilities | `services/api/src/LiveStream.Application/Ai/AiJobService.cs` |
| The worker, and what reaches the model | `services/api/src/LiveStream.Application/Ai/AiJobRunner.cs` |
| Cost estimation | `services/api/src/LiveStream.Application/Ai/AiPricing.cs` |
| The one place a model is called | `services/api/src/LiveStream.Infrastructure/Ai/ClaudeAiAnalyst.cs` |
| Background loop | `services/api/src/LiveStream.Api/BackgroundServices/AiJobWorker.cs` |
| Control room | `apps/web/src/components/studio/AiPanel.tsx` |

## Scope: what was built, and what was not

Three analyses, each on input the platform actually has — the session's own event timeline:

- **Session recap** — what happened during the broadcast.
- **Stream quality review** — what went wrong, and what to change before the next show.
- **Chapters** — chapter markers for the recording.

Five scope items were **not** built, because the platform does not yet produce what they read:

| Not built | Blocked on |
|---|---|
| Transcription | An audio pipeline and a speech-to-text service — Claude does not transcribe audio |
| Translation | Transcripts, so: transcription |
| Moderation | A chat feature; there is none |
| Highlights / clips | Viewer or chat signal to detect a moment from, and a clipping pipeline |
| AI director | Vision over live frames |

These are real dependencies, not oversights. The pipeline they would run on exists and is tested;
what they lack is something to read. Building them against invented inputs would have produced
features that demo and do not work.

## Verification

| Suite | Result |
|---|---|
| Backend build | 0 warnings, 0 errors |
| Backend unit | 326 passed |
| Backend integration | 160 passed |
| Migrations | no pending model changes |
| Frontend lint / typecheck / build | clean |
| Frontend unit | 209 passed |
| End-to-end (real stack) | 23 passed |

**No API key was used, and none is needed.** The provider seam lets the integration suite substitute
a fake analyst and exercise the real pipeline — queueing, deduplication, the worker, retries,
cancellation, permissions, cost accounting — end to end. What is *not* covered by tests is the
adapter's own request and response mapping against the live API; that runs the first time a
deployment sets a key, and its failure mode is a failed job rather than anything on air.

Two tests are worth naming:

**`No_credential_or_identity_reaches_the_model`** inspects the request the analyst actually received
and asserts that no email address, pairing code, device token or stream key appears in it. The risk
was never that somebody writes "send the stream key" — it is that a convenient `Include` quietly
adds one.

**`A_broken_AI_provider_does_not_affect_broadcasting`** points the provider at a failure, then runs a
session through prepare, start and LIVE while the AI worker fails against it. That is the phase's
last acceptance criterion, asserted rather than asserted-about.

## One thing the tests caught, and it was mine

Four AI tests failed on first run with jobs stuck as `Queued`. The pipeline was correct: the worker
deliberately runs **one job per pass, oldest first**, and earlier tests in the class had left a
backlog — so each pass served somebody else's job. The test helper now drains the queue.

The same run hid a second one behind it: the fake analyst's "fail N times" counter was global, so a
leftover job absorbed the single failure the retry test had asked for, and that test silently got a
success. Setting the failure count now resets the counter.

Neither was a product defect, and both would have been invisible had the tests been written to be
lenient.

## Known gaps

- **Not real-time.** Nothing runs during a broadcast to influence it.
- **No autonomous action.** Jobs produce text; nothing acts on it. That is deliberate —
  docs/13-ai-features.md requires human approval for high-impact actions, and the simplest way to
  honour it is to have no action to take.
- **No streaming of partial results.** A job is pending, then it is done.
- **One model per deployment.** No per-feature model choice, and no cheaper model for cheaper jobs.
- **The adapter's live request/response mapping is unverified** until a deployment sets a key.
- **Cost is an estimate** from published list prices, and knows nothing about caching discounts,
  batch pricing, or a negotiated rate.
