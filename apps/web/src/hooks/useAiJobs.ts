"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { aiApi } from "@/lib/api/ai";
import { ApiError } from "@/lib/api/client";
import type { AiCapabilities, AiJob, AiJobKind } from "@/lib/types";

export interface UseAiJobsResult {
  capabilities: AiCapabilities | null;
  jobs: AiJob[];
  loading: boolean;
  error: string | null;
  busyKind: AiJobKind | null;
  request: (kind: AiJobKind) => Promise<void>;
  cancel: (jobId: string) => Promise<void>;
  clearError: () => void;
}

/** How often to re-check while something is still running. */
const POLL_MS = 5000;

function isActive(job: AiJob): boolean {
  return job.status === "Queued" || job.status === "Running";
}

/**
 * The control room's view of AI work.
 *
 * Polled only while a job is actually running. AI jobs take a minute or two and then stop changing
 * forever, so a permanent timer would be almost entirely wasted requests — and the whole point of
 * the feature is that it costs nothing when nobody is using it.
 */
export function useAiJobs(sessionId: string): UseAiJobsResult {
  const [capabilities, setCapabilities] = useState<AiCapabilities | null>(null);
  const [jobs, setJobs] = useState<AiJob[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyKind, setBusyKind] = useState<AiJobKind | null>(null);

  const hasActive = jobs.some(isActive);

  const load = useCallback(async () => {
    try {
      const [features, current] = await Promise.all([aiApi.capabilities(), aiApi.jobs(sessionId)]);

      setCapabilities(features);
      setJobs(current);
      setError(null);
    } catch (loadError) {
      setError(loadError instanceof ApiError ? loadError.message : "Could not load AI jobs.");
    } finally {
      setLoading(false);
    }
  }, [sessionId]);

  useEffect(() => {
    const initial = setTimeout(() => void load(), 0);
    return () => clearTimeout(initial);
  }, [load]);

  // Held in a ref so the polling effect depends on *whether* anything is running, not on the job
  // list — which changes identity on every poll and would restart the timer each time.
  const loadRef = useRef(load);
  useEffect(() => {
    loadRef.current = load;
  }, [load]);

  useEffect(() => {
    if (!hasActive) return;

    const timer = setInterval(() => void loadRef.current(), POLL_MS);
    return () => clearInterval(timer);
  }, [hasActive]);

  const request = useCallback(
    async (kind: AiJobKind) => {
      setBusyKind(kind);
      setError(null);

      try {
        const job = await aiApi.request(sessionId, kind);

        // Merged rather than appended: asking twice returns the job already running, and appending
        // it would show the same work twice.
        setJobs((current) =>
          current.some((existing) => existing.id === job.id)
            ? current.map((existing) => (existing.id === job.id ? job : existing))
            : [job, ...current],
        );
      } catch (requestError) {
        setError(
          requestError instanceof ApiError ? requestError.message : "Could not start that analysis.",
        );
      } finally {
        setBusyKind(null);
      }
    },
    [sessionId],
  );

  const cancel = useCallback(
    async (jobId: string) => {
      setError(null);

      try {
        const job = await aiApi.cancel(sessionId, jobId);
        setJobs((current) => current.map((existing) => (existing.id === jobId ? job : existing)));
      } catch (cancelError) {
        setError(cancelError instanceof ApiError ? cancelError.message : "Could not cancel that job.");
      }
    },
    [sessionId],
  );

  const clearError = useCallback(() => setError(null), []);

  return { capabilities, jobs, loading, error, busyKind, request, cancel, clearError };
}
