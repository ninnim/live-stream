import { apiFetch } from "@/lib/api/client";
import type { AiCapabilities, AiJob, AiJobKind } from "@/lib/types";

/**
 * AI operations (docs/13-ai-features.md).
 *
 * Every call here queues or reads work. None of them wait on a model, which is what keeps a slow
 * provider from ever showing up as a slow studio.
 */
export const aiApi = {
  capabilities: (signal?: AbortSignal) =>
    apiFetch<AiCapabilities>("/api/v1/ai/capabilities", { signal }),

  jobs: (sessionId: string, signal?: AbortSignal) =>
    apiFetch<AiJob[]>(`/api/v1/live-sessions/${sessionId}/ai-jobs`, { signal }),

  request: (sessionId: string, kind: AiJobKind) =>
    apiFetch<AiJob>(`/api/v1/live-sessions/${sessionId}/ai-jobs`, {
      method: "POST",
      body: { kind },
    }),

  cancel: (sessionId: string, jobId: string) =>
    apiFetch<AiJob>(`/api/v1/live-sessions/${sessionId}/ai-jobs/${jobId}`, { method: "DELETE" }),
};
