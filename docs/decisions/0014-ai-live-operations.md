# ADR 0014 — AI live operations

**Status:** Accepted
**Date:** 2026-09-06
**Phase:** 6 — AI Live Operations

How AI is added to the platform without becoming something the platform depends on.

## 1. The acceptance criteria are the architecture

The phase states them plainly, and each one is a structural decision rather than a feature:

> *"AI features are asynchronous, observable, permissioned, and independently disableable. Core
> streaming remains functional when AI services are unavailable."*

| Criterion | How it is met | What would have broken it |
|---|---|---|
| Asynchronous | Requests write a row and return `202`; a separate worker calls the model | Calling the model inside the request |
| Observable | Attempts, timings, model, tokens, cost, failure and source range are all on the job row | Leaving it to logs |
| Permissioned | Every route authorizes against the session, like sources and destinations | A deployment-wide switch and nothing else |
| Independently disableable | A master switch plus a per-feature map, checked again at run time | One global on/off |
| Streaming unaffected | A separate worker loop, and no reference to AI anywhere on the media path | Reconciling AI work inside the health monitor |

The last one is asserted rather than asserted-about: an integration test points the provider at a
failure and then runs a broadcast through prepare, start and LIVE.

## 2. What the platform can honestly analyse today

AI features need input, and the platform has less of it than the phase's scope list assumes.

**Available:** the session's own event timeline — state changes, reconnections, device joins and
departures, destination outcomes — plus session metadata and duration.

**Not available:** there is no chat feature, no audio pipeline exposed to the API, and no
time-series of viewers or bitrate. `LiveSessionHealth` holds current values only.

So three job kinds were built, each on input that actually exists:

- **Session recap** — what happened during the broadcast.
- **Stream quality review** — what went wrong technically, and what to change.
- **Chapters** — chapter markers for the recording, from the same timeline.

And five scope items were **not** built, because building them would have meant inventing their
inputs:

| Scope item | What it needs first |
|---|---|
| Transcription | An audio pipeline and a speech-to-text service. Claude does not transcribe audio. |
| Translation | Transcripts to translate — so, transcription. |
| Moderation | A chat feature. There is none. |
| Automatic highlights / clips | Viewer or chat signal to detect a highlight *from*, and a clipping pipeline to cut one. |
| AI director | Program switching by scene content, which needs vision over the live frames. |

Each is a real piece of work with a real dependency, not an oversight. The pipeline they would run
on is built and tested; what they lack is something to read.

## 3. The provider is a leaf, not a dependency

`IAiAnalyst` has two members. Everything above it — the queue, the worker, the retry policy, the
API — is provider-agnostic, and the only implementation that talks to a model is one file in
Infrastructure.

That is what makes the whole feature testable without an API key: the integration suite substitutes
a fake analyst and exercises queueing, deduplication, retries, permissions, cancellation and cost
accounting for real. Nothing about the pipeline is verified only in production.

`UnconfiguredAiAnalyst` refuses rather than returning plausible text. A stub that produced a
readable recap would be a fabricated analysis presented as a real one, and an operator would act on
it.

## 4. Failures are classified, not merely counted

The adapter decides whether a failure is worth retrying, because only it knows what its provider's
errors mean:

| Failure | Retryable | Why |
|---|---|---|
| Rate limited | Yes | It will pass |
| Provider 5xx, network error | Yes | Transient |
| Model refusal (`stop_reason: "refusal"`) | **No** | It will refuse again |
| 4xx — bad request, bad key, unknown model | **No** | Our fault; retrying spends money to be told the same thing |

Three attempts, backing off 30 s → 2 min → 8 min. Far longer than the media path's, because nothing
is waiting on the answer and the failures worth retrying last minutes.

The refusal check matters more than it looks: a policy decline arrives as **HTTP 200** with
`stop_reason: "refusal"`. Read the content without checking, and a refusal looks like an empty
answer.

## 5. What reaches the model, and what does not

The prompt carries the session's event timeline and its title. It does not carry user identities,
email addresses, pairing codes, device tokens, stream keys, or provider credentials.

That is enforced by the shape of the request-building code — it selects specific columns rather than
loading entities — and asserted by a test that inspects what the analyst actually received. The risk
was never that somebody writes "send the stream key"; it is that a convenient `Include` quietly adds
one.

A long broadcast's timeline is **sampled** down to a fixed budget rather than truncated, keeping
both ends, and the job records the range it actually covered. Truncating to the first N entries
would throw away the last hour of a four-hour session, which is the part a recap needs.

## 6. Money is a first-class concern

Every job costs real money, which shapes four decisions:

- **Deduplication per session and kind.** Pressing the button twice means "I want this", not "I want
  to pay twice"; the second request returns the job already running.
- **Three attempts, not ten.** A job that has failed three times is failing for a reason a fourth
  attempt will not discover.
- **`Start` is committed before the model is called.** A crash mid-request leaves a `Running` job
  rather than a `Queued` one that gets picked up and billed again.
- **Cost is shown.** Tokens and model are stored, and priced from a published table. An unknown
  model reports **null** rather than zero: a job showing $0.00 reads as free.

## 7. Structured output, not parsed prose

Responses are constrained to a JSON schema per job kind (`output_config.format`). Free text turned
into structure by a regular expression is a failure waiting for an unusual session.

Each schema carries a top-level `summary`, which is what the list view shows — so an operator can
scan results without opening any of them.

## 8. Off by default

AI is disabled unless a deployment turns it on, and the API key lives only on the server. The studio
never sees it: the browser asks this API to queue work, and this API talks to the model.

When AI is unconfigured the panel does not render at all. A row of dead buttons suggests something
is broken, when in fact the feature was simply never enabled.

## 9. What this is not

- **Not real-time.** Nothing here runs during a broadcast to influence it.
- **No autonomous action.** Jobs produce text. Nothing acts on it —
  docs/13-ai-features.md's guardrail that AI must not "secretly publish destructive actions" is met
  by there being no action to take.
- **No streaming of partial results.** A job is pending, then it is done.
- **One model per deployment.** No per-feature model selection, and no cheaper model for cheaper
  jobs.
