"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/lib/api/client";
import { liveSessionApi } from "@/lib/api/live-sessions";
import { LiveHubClient } from "@/lib/realtime/live-hub";
import type {
  DestinationStatusPayload,
  LiveSession,
  LiveSessionHealth,
  LiveSessionStatusPayload,
  Recording,
  SourceStatusPayload,
} from "@/lib/types";

export interface UseLiveSessionResult {
  session: LiveSession | null;
  status: LiveSessionStatusPayload | null;
  health: LiveSessionHealth | null;
  recording: Recording | null;
  realtimeConnected: boolean;
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  applyStatus: (status: LiveSessionStatusPayload) => void;
}

/** How often to poll while the realtime channel is down. */
const FALLBACK_POLL_MS = 4000;

/** A slower reconciliation poll that runs even when realtime is healthy. */
const RECONCILE_POLL_MS = 15_000;

/**
 * Keeps the studio synchronised with server-authoritative session state.
 *
 * SignalR provides low latency; REST polling provides correctness. Polling continues at a slower
 * cadence even while the hub is connected, so a silently dead socket cannot leave the studio
 * displaying stale state during a broadcast.
 */
export function useLiveSession(
  sessionId: string,
  options: {
    onDestinationStateChanged?: (payload: DestinationStatusPayload) => void;
    onSourceStateChanged?: (payload: SourceStatusPayload) => void;
  } = {},
): UseLiveSessionResult {
  const [session, setSession] = useState<LiveSession | null>(null);
  const [status, setStatus] = useState<LiveSessionStatusPayload | null>(null);
  const [health, setHealth] = useState<LiveSessionHealth | null>(null);
  const [recording, setRecording] = useState<Recording | null>(null);
  const [realtimeConnected, setRealtimeConnected] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const realtimeConnectedRef = useRef(false);

  // Held in a ref so a new callback identity does not tear down and rebuild the hub connection —
  // which, mid-broadcast, would drop realtime updates every render. Synced in an effect rather
  // than during render, because a render can be discarded and must not have side effects.
  const onDestinationStateChangedRef = useRef(options.onDestinationStateChanged);
  const onSourceStateChangedRef = useRef(options.onSourceStateChanged);

  useEffect(() => {
    onDestinationStateChangedRef.current = options.onDestinationStateChanged;
    onSourceStateChangedRef.current = options.onSourceStateChanged;
  }, [options.onDestinationStateChanged, options.onSourceStateChanged]);

  const applyStatus = useCallback((next: LiveSessionStatusPayload) => {
    setStatus((current) => {
      // Version is the server's ordering guarantee: never let a delayed message overwrite newer state.
      if (current && next.version < current.version) return current;
      return next;
    });
  }, []);

  const refresh = useCallback(async () => {
    try {
      const [nextSession, nextStatus] = await Promise.all([
        liveSessionApi.get(sessionId),
        liveSessionApi.status(sessionId),
      ]);

      setSession(nextSession);
      setHealth(nextSession.health);
      applyStatus(nextStatus);
      setError(null);
    } catch (fetchError) {
      if (fetchError instanceof ApiError) {
        setError(fetchError.message);
      } else {
        setError("We lost contact with the server. Retrying…");
      }
    } finally {
      setLoading(false);
    }
  }, [sessionId, applyStatus]);

  // Realtime channel.
  useEffect(() => {
    const hub = new LiveHubClient(sessionId, {
      onSessionStateChanged: applyStatus,
      onHealthUpdated: (_, nextHealth) => setHealth(nextHealth),
      onViewerCountUpdated: (_, viewerCount) =>
        setHealth((current) => (current ? { ...current, viewerCount } : current)),
      onRecordingStateChanged: (_, nextRecording) => setRecording(nextRecording),
      onDestinationStateChanged: (payload) => onDestinationStateChangedRef.current?.(payload),
      onSourceStateChanged: (payload) => onSourceStateChangedRef.current?.(payload),
      onConnectionStateChanged: (connected) => {
        realtimeConnectedRef.current = connected;
        setRealtimeConnected(connected);
      },
    });

    void hub.connect().catch(() => {
      // Realtime is an optimisation; polling below keeps the studio correct without it.
      realtimeConnectedRef.current = false;
      setRealtimeConnected(false);
    });

    return () => {
      void hub.disconnect();
    };
  }, [sessionId, applyStatus]);

  // Loading and polling: the first tick fires immediately, then fast while realtime is down and
  // slowly for reconciliation while it is up.
  //
  // Even the initial load runs on a timer rather than directly in the effect body, so its state
  // updates land in their own commit instead of cascading inside this one.
  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout>;

    const tick = async (): Promise<void> => {
      if (cancelled) return;
      await refresh();
      if (cancelled) return;

      timer = setTimeout(
        () => void tick(),
        realtimeConnectedRef.current ? RECONCILE_POLL_MS : FALLBACK_POLL_MS,
      );
    };

    timer = setTimeout(() => void tick(), 0);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [refresh]);

  return {
    session,
    status,
    health,
    recording,
    realtimeConnected,
    loading,
    error,
    refresh,
    applyStatus,
  };
}
