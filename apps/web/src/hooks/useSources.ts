"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/lib/api/client";
import { sourceApi } from "@/lib/api/devices";
import type { SessionSource, SourceInvitation, SourceRole, SourceStatusPayload } from "@/lib/types";

export interface UseSourcesResult {
  sources: SessionSource[];
  loading: boolean;
  error: string | null;
  busyId: string | null;
  /** The most recent invitation, held so the code can be shown until dismissed. */
  invitation: SourceInvitation | null;
  refresh: () => Promise<void>;
  applyRealtime: (payload: SourceStatusPayload) => void;
  invite: (role: SourceRole, displayName: string) => Promise<void>;
  dismissInvitation: () => void;
  revoke: (sourceId: string) => Promise<void>;
  rename: (sourceId: string, displayName: string) => Promise<void>;
  /** Cuts to a source. Applies the returned rows immediately so the highlight moves on click. */
  setProgram: (sourceId: string) => Promise<void>;
}

/** Presence changes matter more than destination changes, so this polls a little faster. */
const POLL_MS = 5000;

/**
 * Keeps the control room's view of contributing devices synchronised.
 *
 * Presence is observed server-side from the media plane, so nothing here ever infers that a device
 * is connected — it only renders what the server reports.
 */
export function useSources(sessionId: string): UseSourcesResult {
  const [sources, setSources] = useState<SessionSource[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [invitation, setInvitation] = useState<SourceInvitation | null>(null);

  const refresh = useCallback(async () => {
    try {
      setSources(await sourceApi.list(sessionId));
      setError(null);
    } catch (fetchError) {
      setError(fetchError instanceof ApiError ? fetchError.message : "Could not load devices.");
    } finally {
      setLoading(false);
    }
  }, [sessionId]);

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
   * Only the fields the event carries are replaced. Overwriting the whole row would drop
   * configuration the event does not include and make the tile flicker.
   */
  const applyRealtime = useCallback((payload: SourceStatusPayload) => {
    setSources((current) =>
      current.map((source) =>
        source.id === payload.id
          ? {
              ...source,
              status: payload.status,
              isProgram: payload.isProgram,
              ingestConnected: payload.ingestConnected,
              bitrateKbps: payload.bitrateKbps,
              lastSeenAt: payload.lastSeenAt,
              displayName: payload.displayName,
            }
          : source,
      ),
    );
  }, []);

  const run = useCallback(
    async (id: string, action: () => Promise<unknown>) => {
      setBusyId(id);
      setError(null);

      try {
        await action();
        await refresh();
      } catch (actionError) {
        setError(actionError instanceof ApiError ? actionError.message : "That did not work. Please try again.");
        throw actionError;
      } finally {
        setBusyId(null);
      }
    },
    [refresh],
  );

  const invite = useCallback<UseSourcesResult["invite"]>(
    (role, displayName) =>
      run("new", async () => {
        // Held in state rather than shown in a transient toast: the operator has to read eight
        // characters onto a phone, and the code cannot be retrieved a second time.
        setInvitation(await sourceApi.invite(sessionId, role, displayName));
      }),
    [run, sessionId],
  );

  const revoke = useCallback(
    (sourceId: string) => run(sourceId, () => sourceApi.revoke(sessionId, sourceId)),
    [run, sessionId],
  );

  const rename = useCallback(
    (sourceId: string, displayName: string) =>
      run(sourceId, () => sourceApi.rename(sessionId, sourceId, displayName)),
    [run, sessionId],
  );

  /**
   * Cuts to a source.
   *
   * The response carries both ends of the swap, and they are applied before the refresh so the
   * highlight moves on click rather than a poll later. Cutting is the one action in this panel that
   * an operator performs to a beat.
   */
  const setProgram = useCallback(
    (sourceId: string) =>
      run(sourceId, async () => {
        const changed = await sourceApi.setProgram(sessionId, sourceId);
        const byId = new Map(changed.map((source) => [source.id, source]));

        setSources((current) => current.map((source) => byId.get(source.id) ?? source));
      }),
    [run, sessionId],
  );

  const dismissInvitation = useCallback(() => setInvitation(null), []);

  return {
    sources,
    loading,
    error,
    busyId,
    invitation,
    refresh,
    applyRealtime,
    invite,
    dismissInvitation,
    revoke,
    rename,
    setProgram,
  };
}
