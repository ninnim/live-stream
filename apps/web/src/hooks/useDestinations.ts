"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/lib/api/client";
import { destinationApi, distributionApi } from "@/lib/api/destinations";
import type { Destination, DestinationStatusPayload, ProviderDescriptor } from "@/lib/types";

export interface UseDestinationsResult {
  destinations: Destination[];
  providers: ProviderDescriptor[];
  loading: boolean;
  error: string | null;
  /** Set while an operator action is in flight, so buttons can be disabled. */
  busyId: string | null;
  refresh: () => Promise<void>;
  applyRealtime: (payload: DestinationStatusPayload) => void;
  add: (input: {
    provider: Destination["provider"];
    displayName: string;
    ingestUrl?: string | null;
    streamKey?: string | null;
    providerAccountId?: string | null;
  }) => Promise<void>;
  remove: (destinationId: string) => Promise<void>;
  setEnabled: (destinationId: string, enabled: boolean) => Promise<void>;
  start: (destinationId: string) => Promise<void>;
  stop: (destinationId: string) => Promise<void>;
}

/** Slower than the session poll: destination changes are less time-critical than going live. */
const POLL_MS = 8000;

/**
 * Keeps the studio's destination list synchronised.
 *
 * Same contract as the session hook: realtime for latency, polling for correctness. Destination
 * state is driven by a server-side reconciler, so the client only ever reads it — there is no
 * optimistic status anywhere in here.
 */
export function useDestinations(sessionId: string): UseDestinationsResult {
  const [destinations, setDestinations] = useState<Destination[]>([]);
  const [providers, setProviders] = useState<ProviderDescriptor[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const next = await destinationApi.list(sessionId);
      setDestinations(next);
      setError(null);
    } catch (fetchError) {
      // A caller without DESTINATION_MANAGE can still view; anything else is worth surfacing.
      setError(fetchError instanceof ApiError ? fetchError.message : "Could not load destinations.");
    } finally {
      setLoading(false);
    }
  }, [sessionId]);

  // The provider catalogue is static for the life of the page.
  useEffect(() => {
    let cancelled = false;

    void distributionApi
      .providers()
      .then((next) => {
        if (!cancelled) setProviders(next);
      })
      .catch(() => {
        // Without the catalogue the form falls back to a free-form RTMP entry, which still works.
      });

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout>;

    const tick = async (): Promise<void> => {
      if (cancelled) return;
      await refresh();
      if (cancelled) return;
      timer = setTimeout(() => void tick(), POLL_MS);
    };

    timer = setTimeout(() => void tick(), 0);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [refresh]);

  /**
   * Merges a realtime update into the list.
   *
   * Only the fields the event actually carries are replaced. Overwriting the whole row would drop
   * configuration the event does not include — the ingest URL, for one — and make it flicker.
   */
  const applyRealtime = useCallback((payload: DestinationStatusPayload) => {
    setDestinations((current) =>
      current.map((destination) =>
        destination.id === payload.id
          ? {
              ...destination,
              status: payload.status,
              enabled: payload.enabled,
              lastErrorCode: payload.lastErrorCode,
              lastErrorMessage: payload.lastErrorMessage,
              watchUrl: payload.watchUrl,
              attemptCount: payload.attemptCount,
              nextRetryAt: payload.nextRetryAt,
              uptimeSeconds: payload.uptimeSeconds,
              bytesSent: payload.bytesSent,
            }
          : destination,
      ),
    );
  }, []);

  /** Runs one operator action, refreshing afterwards so the server's view wins. */
  const run = useCallback(
    async (id: string, action: () => Promise<unknown>) => {
      setBusyId(id);
      setError(null);

      try {
        await action();
        await refresh();
      } catch (actionError) {
        setError(
          actionError instanceof ApiError ? actionError.message : "That did not work. Please try again.",
        );
        throw actionError;
      } finally {
        setBusyId(null);
      }
    },
    [refresh],
  );

  const add = useCallback<UseDestinationsResult["add"]>(
    (input) => run("new", () => destinationApi.create(sessionId, input)),
    [run, sessionId],
  );

  const remove = useCallback(
    (destinationId: string) => run(destinationId, () => destinationApi.remove(sessionId, destinationId)),
    [run, sessionId],
  );

  const setEnabled = useCallback(
    (destinationId: string, enabled: boolean) =>
      run(destinationId, () => destinationApi.update(sessionId, destinationId, { enabled })),
    [run, sessionId],
  );

  const start = useCallback(
    (destinationId: string) => run(destinationId, () => destinationApi.start(sessionId, destinationId)),
    [run, sessionId],
  );

  const stop = useCallback(
    (destinationId: string) => run(destinationId, () => destinationApi.stop(sessionId, destinationId)),
    [run, sessionId],
  );

  return {
    destinations,
    providers,
    loading,
    error,
    busyId,
    refresh,
    applyRealtime,
    add,
    remove,
    setEnabled,
    start,
    stop,
  };
}
