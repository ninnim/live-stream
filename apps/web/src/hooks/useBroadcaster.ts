"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/lib/api/client";
import type { EncodingLimits } from "@/lib/media/adaptive";
import { liveSessionApi } from "@/lib/api/live-sessions";
import type { PublishQuality } from "@/lib/media/quality";
import {
  type BroadcastConnectionState,
  DEFAULT_AUDIO_BITRATE,
  DEFAULT_VIDEO_BITRATE,
  WhipPublisher,
} from "@/lib/media/whip";
import type { LiveSessionStatusPayload } from "@/lib/types";

/** How often outgoing video is measured while live. Frequent enough to catch a problem forming. */
const QUALITY_SAMPLE_INTERVAL_MS = 2000;

export interface UseBroadcasterResult {
  connectionState: BroadcastConnectionState;
  reconnectAttempt: number;
  busy: boolean;
  error: string | null;
  /** Measured from the browser's own encoder. `null` until something is publishing. */
  quality: PublishQuality | null;
  goLive: (stream: MediaStream) => Promise<void>;
  /** Goes live on video this browser is not sending — an external encoder (ADR 0022). */
  goLiveExternal: () => Promise<void>;
  /** Republishes into a session the server already considers live (e.g. after a studio reload). */
  resume: (stream: MediaStream) => Promise<void>;
  /**
   * Swaps a capture track into the open transport. Does nothing when not publishing, so the studio
   * can call it on every device change without first asking whether it is live.
   */
  replaceTrack: (track: MediaStreamTrack) => Promise<void>;
  /** Stops sending one kind of media, because its last device went away. */
  removeTrack: (kind: "video" | "audio") => Promise<void>;
  /**
   * Retunes the running encoder in place — bitrate, resolution scale, frame rate — with no
   * renegotiation and no gap in the picture. Does nothing when not publishing, and is remembered
   * so a reconnect comes back at the same rung.
   */
  applyEncoding: (limits: EncodingLimits) => Promise<void>;
  stopLive: () => Promise<void>;
  clearError: () => void;
}

interface UseBroadcasterOptions {
  sessionId: string;
  /** Applies the authoritative status the API returned for start/stop. */
  onStatus: (status: LiveSessionStatusPayload) => void;
  onRefresh: () => Promise<void>;
  /**
   * Codec families this machine's GPU can encode. Shapes which codec the offer prefers, so a
   * machine that accelerates something other than H.264 is not pushed into software encoding.
   */
  hardwareCodecs?: readonly string[];
}

/**
 * Drives the broadcast: publish media, then ask the server to go live.
 *
 * Media is connected *before* start is requested so the API can promote the session to LIVE as soon
 * as it sees ingest, and so the studio never shows LIVE before media is actually flowing
 * (docs/04-native-broadcasting.md).
 */
export function useBroadcaster({
  sessionId,
  onStatus,
  onRefresh,
  hardwareCodecs,
}: UseBroadcasterOptions): UseBroadcasterResult {
  const [connectionState, setConnectionState] = useState<BroadcastConnectionState>("idle");
  const [reconnectAttempt, setReconnectAttempt] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [measuredQuality, setMeasuredQuality] = useState<PublishQuality | null>(null);

  const publisherRef = useRef<WhipPublisher | null>(null);

  // Held in a ref so a probe that resolves after this hook's callbacks were built still reaches
  // the next publisher, without rebuilding them and restarting a broadcast to apply it.
  const hardwareCodecsRef = useRef<readonly string[]>(hardwareCodecs ?? []);

  useEffect(() => {
    hardwareCodecsRef.current = hardwareCodecs ?? [];
  }, [hardwareCodecs]);

  const clearError = useCallback(() => setError(null), []);

  /**
   * Changes camera or microphone without interrupting the broadcast.
   *
   * A failure here is worth reporting to the server: the operator saw the picture stop matching the
   * preview, and the session timeline should say why.
   */
  const replaceTrack = useCallback(
    async (track: MediaStreamTrack) => {
      const publisher = publisherRef.current;
      if (!publisher) return;

      try {
        await publisher.replaceTrack(track);
      } catch (swapError) {
        const detail = swapError instanceof Error ? swapError.message : String(swapError);
        void liveSessionApi.reportSignal(sessionId, "DEVICE_ERROR", detail).catch(() => undefined);
        throw swapError;
      }
    },
    [sessionId],
  );

  /**
   * Applies measured encoder limits to whatever is publishing now.
   *
   * Silent when nothing is: the adaptive controller samples on its own clock, and must not have to
   * ask whether the transport is up before each decision.
   */
  const applyEncoding = useCallback(async (limits: EncodingLimits) => {
    await publisherRef.current?.applyEncoding(limits);
  }, []);

  const removeTrack = useCallback(async (kind: "video" | "audio") => {
    // Never reported as a device error: a device being unplugged is not a fault of the
    // broadcast, and the studio has already told the operator about it.
    await publisherRef.current?.removeTrack(kind);
  }, []);

  /**
   * Builds a publisher for this session. Each connection attempt mints its own short-lived
   * credential, which is what lets reconnects work without ever holding a long-lived stream secret.
   */
  const createPublisher = useCallback(
    (stream: MediaStream) =>
      new WhipPublisher({
        stream,

        // Broadcast quality rather than call quality. Both are ceilings the connection is free to
        // stay under; left unset the browser picks conference-call defaults, which is mono speech
        // audio and a thin picture.
        audioBitrate: DEFAULT_AUDIO_BITRATE,
        videoBitrate: DEFAULT_VIDEO_BITRATE,
        hardwareCodecs: hardwareCodecsRef.current,
        getCredential: async () => {
          const credential = await liveSessionApi.issueCredential(sessionId);
          return {
            ingestUrl: credential.ingestUrl,
            token: credential.token,
            iceServers: (credential.iceServers ?? []).map((server) => ({
              urls: server.urls,
              ...(server.username ? { username: server.username } : {}),
              ...(server.credential ? { credential: server.credential } : {}),
            })),
          };
        },
        events: {
          onStateChange: (state, detail) => {
            setConnectionState(state);

            if (state === "connected") {
              setReconnectAttempt(0);
              void liveSessionApi.reportSignal(sessionId, "INGEST_CONNECTED").catch(() => undefined);
            } else if (state === "reconnecting") {
              void liveSessionApi
                .reportSignal(sessionId, "RECONNECT_ATTEMPT", detail)
                .catch(() => undefined);
            } else if (state === "failed") {
              setError(
                "We could not restore the connection to the streaming server. Check your network and start again.",
              );
              void liveSessionApi.reportSignal(sessionId, "RECONNECT_FAILED", detail).catch(() => undefined);
            }
          },
          onReconnectAttempt: (attempt) => setReconnectAttempt(attempt),
        },
      }),
    [sessionId],
  );

  /**
   * Republishes into a session that is already live server-side.
   *
   * This is what makes a studio reload survivable: the page — and with it the peer connection —
   * is destroyed, but the session is still inside its recovery window, so picking the stream back
   * up continues the same broadcast instead of losing it. Deliberately does not call `start`: the
   * session is already past that transition and the server owns its state.
   */
  const resume = useCallback(
    async (stream: MediaStream) => {
      setBusy(true);
      setError(null);

      try {
        const publisher = createPublisher(stream);
        publisherRef.current = publisher;
        await publisher.start();

        void liveSessionApi
          .reportSignal(sessionId, "RECONNECT_SUCCEEDED", "Resumed after studio reload")
          .catch(() => undefined);

        await onRefresh();
      } catch (resumeError) {
        await publisherRef.current?.stop();
        publisherRef.current = null;
        setConnectionState("failed");

        setError(
          resumeError instanceof ApiError
            ? resumeError.message
            : "We could not pick your broadcast back up. Stop the session and start again.",
        );
      } finally {
        setBusy(false);
      }
    },
    [sessionId, createPublisher, onRefresh],
  );

  const goLive = useCallback(
    async (stream: MediaStream) => {
      setBusy(true);
      setError(null);

      try {
        // 1. Reserve media resources. Safe to repeat: prepare is idempotent.
        await liveSessionApi.prepare(sessionId);

        // 2. Publish.
        const publisher = createPublisher(stream);
        publisherRef.current = publisher;
        await publisher.start();

        // 3. Ask the server to go live. It confirms against the media plane and owns the answer.
        const status = await liveSessionApi.start(sessionId);
        onStatus(status);
      } catch (startError) {
        // Leave no half-open transport behind if going live failed.
        await publisherRef.current?.stop();
        publisherRef.current = null;
        setConnectionState("failed");

        setError(
          startError instanceof ApiError
            ? startError.message
            : "We could not start your broadcast. Please try again.",
        );

        void liveSessionApi
          .reportSignal(
            sessionId,
            "CLIENT_ERROR",
            startError instanceof Error ? startError.message : undefined,
          )
          .catch(() => undefined);
      } finally {
        setBusy(false);
      }
    },
    [sessionId, createPublisher, onStatus],
  );

  /**
   * Goes live on video this browser is not sending.
   *
   * The encoder case (ADR 0022): a phone streaming a game, a capture card, OBS. There is no local
   * publisher to open and nothing to swap tracks into — the media is already arriving at the
   * gateway, or is about to — so this is `goLive` with the publishing step removed rather than a
   * second lifecycle.
   *
   * The session sits in STARTING until the server sees ingest, exactly as it does for a browser
   * that has published but not yet been confirmed. That is also what makes a wrong key visible:
   * the start times out and says so, rather than appearing to work.
   */
  const goLiveExternal = useCallback(async () => {
    setBusy(true);
    setError(null);

    try {
      await liveSessionApi.prepare(sessionId);
      onStatus(await liveSessionApi.start(sessionId));
    } catch (startError) {
      setError(
        startError instanceof ApiError
          ? startError.message
          : "We could not start your broadcast. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  }, [sessionId, onStatus]);

  const stopLive = useCallback(async () => {
    setBusy(true);
    setError(null);

    try {
      await publisherRef.current?.stop();
      publisherRef.current = null;
      setConnectionState("closed");

      const status = await liveSessionApi.stop(sessionId);
      onStatus(status);
      await onRefresh();
    } catch (stopError) {
      setError(
        stopError instanceof ApiError
          ? stopError.message
          : "We could not confirm that your broadcast stopped. Refresh to check its status.",
      );
    } finally {
      setBusy(false);
    }
  }, [sessionId, onStatus, onRefresh]);

  /**
   * Samples outgoing video while connected.
   *
   * Only runs in the `connected` state: sampling a reconnecting transport reads counters from a
   * peer connection that is about to be discarded, which produces a bitrate spike that means
   * nothing.
   */
  useEffect(() => {
    if (connectionState !== "connected") return;

    let cancelled = false;

    const sample = (): void => {
      void publisherRef.current?.sampleQuality().then((measured) => {
        if (!cancelled && measured) setMeasuredQuality(measured);
      });
    };

    sample();
    const handle = window.setInterval(sample, QUALITY_SAMPLE_INTERVAL_MS);

    return () => {
      cancelled = true;
      window.clearInterval(handle);
    };
  }, [connectionState]);

  // Closing the tab must tear the transport down, or the gateway keeps the path open until the
  // server's recovery window expires.
  useEffect(
    () => () => {
      void publisherRef.current?.stop();
      publisherRef.current = null;
    },
    [],
  );

  // Derived rather than stored: a reading only means anything while the transport it was taken
  // from is still up, so a stale "smooth" can never outlive the connection that produced it.
  const quality = connectionState === "connected" ? measuredQuality : null;

  return {
    connectionState,
    reconnectAttempt,
    busy,
    error,
    quality,
    goLive,
    goLiveExternal,
    resume,
    replaceTrack,
    removeTrack,
    applyEncoding,
    stopLive,
    clearError,
  };
}
