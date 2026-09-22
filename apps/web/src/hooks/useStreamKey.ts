"use client";

import { useCallback, useState } from "react";
import { ApiError } from "@/lib/api/client";
import { liveSessionApi } from "@/lib/api/live-sessions";
import type { StreamKey } from "@/lib/types";

export interface UseStreamKeyResult {
  /** The key, only while this page still has it. Never fetched — it cannot be. */
  key: StreamKey | null;
  busy: boolean;
  error: string | null;
  /** True when the deployment does not offer external ingest, so the panel can say so and stop. */
  unavailable: boolean;
  /** Issues a key, or rotates the existing one. Both are the same request. */
  reveal: () => Promise<void>;
  revoke: () => Promise<void>;
  /** Drops the key from this page without revoking it — the "hide" half of reveal. */
  forget: () => void;
  clearError: () => void;
}

/**
 * The encoder stream key.
 *
 * There is deliberately no "load the current key" here, and there cannot be: the server stores
 * only a hash, so the plaintext exists in exactly one place — the response to the request that
 * created it — and then only in this page's memory (ADR 0022). Reloading the studio loses it, and
 * getting it back means rotating, which is the honest behaviour rather than a limitation to
 * apologise for: a key that can be re-read on demand is a key that can be re-read by anyone who
 * gets the session open.
 *
 * Nothing is persisted to storage for the same reason.
 */
export function useStreamKey(sessionId: string): UseStreamKeyResult {
  const [key, setKey] = useState<StreamKey | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [unavailable, setUnavailable] = useState(false);

  const reveal = useCallback(async () => {
    setBusy(true);
    setError(null);

    try {
      // Prepare first, because a key opens nothing until the session has a media path: publish
      // authorization requires the session to be expecting ingest. Without this the encoder is
      // refused on its first attempt, which encoders report as a bare connection failure and
      // people reasonably read as a wrong key.
      //
      // Best effort: prepare is idempotent, and it legitimately refuses for a session that is
      // already live — which is a session that already has a path, so the key works anyway.
      await liveSessionApi.prepare(sessionId).catch(() => undefined);

      setKey(await liveSessionApi.issueStreamKey(sessionId));
    } catch (revealError) {
      // A deployment with no RTMP origin configured answers 400. That is a statement about the
      // deployment rather than a failure of this request, so the panel says so and stops offering.
      if (revealError instanceof ApiError && revealError.status === 400) {
        setUnavailable(true);
        return;
      }

      setError(
        revealError instanceof ApiError
          ? revealError.message
          : "We could not create a stream key. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  }, [sessionId]);

  const revoke = useCallback(async () => {
    setBusy(true);
    setError(null);

    try {
      await liveSessionApi.revokeStreamKey(sessionId);
      setKey(null);
    } catch (revokeError) {
      setError(
        revokeError instanceof ApiError
          ? revokeError.message
          : "We could not revoke the stream key. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  }, [sessionId]);

  const forget = useCallback(() => setKey(null), []);
  const clearError = useCallback(() => setError(null), []);

  return { key, busy, error, unavailable, reveal, revoke, forget, clearError };
}
