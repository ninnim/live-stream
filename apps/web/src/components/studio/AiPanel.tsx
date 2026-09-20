"use client";

import { Badge, Button, Card } from "@/components/ui/primitives";
import type { UseAiJobsResult } from "@/hooks/useAiJobs";
import type { AiJob, AiJobStatus } from "@/lib/types";

const STATUS_TONE: Record<AiJobStatus, "neutral" | "caution" | "positive" | "critical"> = {
  Queued: "neutral",
  Running: "caution",
  Succeeded: "positive",
  Failed: "critical",
  Cancelled: "neutral",
};

interface AiPanelProps {
  ai: UseAiJobsResult;
}

/**
 * AI analysis of the session.
 *
 * Nothing here is on the broadcast path. Requesting analysis queues work and returns; the answers
 * turn up when they turn up, and a provider that is down produces a failed job and nothing else
 * (implementation/phase-6-ai-live-operations.md).
 */
export function AiPanel({ ai }: AiPanelProps) {
  // Absent rather than disabled when the deployment has no provider: a row of dead buttons
  // suggests something is broken, when in fact the feature was simply never turned on.
  if (!ai.capabilities?.configured) return null;

  return (
    <Card className="flex flex-col gap-4">
      <div>
        <h2 className="text-sm font-semibold text-slate-100">AI analysis</h2>
        <p className="mt-0.5 text-xs text-slate-500">
          Runs in the background against this session&rsquo;s own timeline. It cannot see or hear the
          broadcast.
        </p>
      </div>

      {ai.error ? (
        <p role="alert" className="rounded-lg bg-red-950/60 px-3 py-2 text-sm text-red-300">
          {ai.error}
        </p>
      ) : null}

      <div className="flex flex-wrap gap-2">
        {ai.capabilities.features.map((feature) => (
          <Button
            key={feature.kind}
            variant="secondary"
            disabled={!feature.enabled || ai.busyKind !== null}
            title={feature.enabled ? feature.description : "This feature is turned off for this deployment."}
            onClick={() => void ai.request(feature.kind)}
          >
            {ai.busyKind === feature.kind ? "Starting…" : feature.displayName}
          </Button>
        ))}
      </div>

      {ai.jobs.length === 0 ? (
        <p className="text-sm text-slate-500">
          No analysis yet. A recap is most useful once the broadcast has finished.
        </p>
      ) : (
        <ul className="flex flex-col gap-3">
          {ai.jobs.map((job) => (
            <AiJobRow key={job.id} job={job} onCancel={() => void ai.cancel(job.id)} />
          ))}
        </ul>
      )}
    </Card>
  );
}

function AiJobRow({ job, onCancel }: { job: AiJob; onCancel: () => void }) {
  return (
    <li className="rounded-xl border border-slate-800 bg-slate-900/40 p-3">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <Badge tone={STATUS_TONE[job.status]} pulse={job.status === "Running"}>
              {job.status}
            </Badge>
            <p className="truncate text-sm font-medium text-slate-200">{label(job)}</p>
          </div>

          {job.summary ? <p className="mt-1 text-sm text-slate-300">{job.summary}</p> : null}

          {job.errorMessage ? (
            <p className="mt-1 text-sm text-red-300">
              {job.errorMessage}
              {job.attemptCount < job.maxAttempts && job.status === "Queued"
                ? ` Retrying (attempt ${job.attemptCount + 1} of ${job.maxAttempts}).`
                : ""}
            </p>
          ) : null}
        </div>

        {job.status === "Queued" ? (
          <Button variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        ) : null}
      </div>

      {job.status === "Succeeded" ? (
        <>
          {/*
            The full answer is offered rather than shown: these run to a few hundred words, and an
            operator scanning a list wants the one-line summary above.
          */}
          <details className="mt-2">
            <summary className="cursor-pointer text-xs text-slate-400">Full result</summary>
            <pre className="mt-2 max-h-80 overflow-auto rounded-lg bg-slate-950 p-3 text-xs text-slate-300">
              {pretty(job.resultJson)}
            </pre>
          </details>

          {/*
            Provenance and cost, together. docs/13-ai-features.md requires output to be traceable to
            the range it came from, and an AI feature whose price is invisible is one nobody budgets
            for.
          */}
          <p className="mt-2 text-xs text-slate-500">
            {job.modelId ?? "unknown model"} · {job.inputTokens.toLocaleString()} in /{" "}
            {job.outputTokens.toLocaleString()} out
            {job.estimatedCostUsd !== null ? ` · ~$${job.estimatedCostUsd.toFixed(4)}` : ""}
            {job.sourceRangeStart && job.sourceRangeEnd
              ? ` · from ${new Date(job.sourceRangeStart).toLocaleTimeString()} to ${new Date(
                  job.sourceRangeEnd,
                ).toLocaleTimeString()}`
              : ""}
          </p>
        </>
      ) : null}
    </li>
  );
}

function label(job: AiJob): string {
  switch (job.kind) {
    case "SessionRecap":
      return "Session recap";
    case "StreamQualityReview":
      return "Stream quality review";
    case "Chapters":
      return "Chapters";
    default:
      return job.kind;
  }
}

/** Formats the result for reading, falling back to the raw text if it is not JSON after all. */
function pretty(json: string | null): string {
  if (!json) return "";

  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}
