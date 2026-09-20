"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/lib/api/client";
import { deviceApi, deviceTokenStore } from "@/lib/api/devices";
import { WhipPublisher } from "@/lib/media/whip";
import type { DevicePaired, DeviceSession } from "@/lib/types";

export type DevicePhase = "idle" | "pairing" | "paired" | "publishing" | "live" | "revoked" | "error";

export interface UseDeviceBroadcastResult {
  phase: DevicePhase;
  session: DeviceSession | null;
  error: string | null;
  busy: boolean;
  pair: (code: string, deviceLabel?: string | null) => Promise<void>;
  publish: (stream: MediaStream) => Promise<void>;
  /**
   * Swaps a capture track into the open transport. On a phone this is the front/back camera
   * button, which has to work while sending — turning the phone around is not a reason to drop off
   * the show.
   */
  replaceTrack: (track: MediaStreamTrack) => Promise<void>;
  stop: () => Promise<void>;
  clearError: () => void;
}

/**
 * The device side of multi-device contribution.
 *
 * A phone that opens a join link redeems its code once, then behaves like a very small studio: it
 * asks for a fresh publish credential per connection attempt and pushes media over WHIP into its
 * own path. It never learns anything about the session beyond what its role permits.
 */
export function useDeviceBroadcast(): UseDeviceBroadcastResult {
  const [phase, setPhase] = useState<DevicePhase>("idle");
  const [session, setSession] = useState<DeviceSession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const tokenRef = useRef<string | null>(null);
  const publisherRef = useRef<WhipPublisher | null>(null);

  useEffect(() => {
    return () => {
      void publisherRef.current?.stop();
      publisherRef.current = null;
    };
  }, []);

  const pair = useCallback(async (code: string, deviceLabel?: string | null) => {
    setBusy(true);
    setError(null);
    setPhase("pairing");

    try {
      const paired: DevicePaired = await deviceApi.claim(code, deviceLabel);

      tokenRef.current = paired.deviceToken;
      deviceTokenStore.save(paired.liveSessionId, paired.deviceToken);

      setSession(await deviceApi.session(paired.deviceToken));
      setPhase("paired");
    } catch (pairError) {
      setPhase("error");
      setError(
        pairError instanceof ApiError
          ? pairError.message
          : "We could not join that session. Check the code and try again.",
      );
      throw pairError;
    } finally {
      setBusy(false);
    }
  }, []);

  const publish = useCallback(async (stream: MediaStream) => {
    const token = tokenRef.current;

    if (!token) {
      setError("This device is not paired yet.");
      return;
    }

    setBusy(true);
    setError(null);
    setPhase("publishing");

    try {
      const publisher = new WhipPublisher({
        stream,
        // A fresh credential per attempt, exactly as the studio does. Because the publisher calls
        // this on every reconnect too, a device that drops out and comes back re-authorizes rather
        // than replaying an old token — and a revoked device simply stops being able to return.
        getCredential: async () => {
          const credential = await deviceApi.issueCredential(token);

          return {
            ingestUrl: credential.ingestUrl,
            token: credential.token,
            iceServers: credential.iceServers?.map((server) => ({
              urls: server.urls,
              username: server.username ?? undefined,
              credential: server.credential ?? undefined,
            })),
          };
        },
        events: {
          // The transport's own states are richer than this screen needs; a phone operator only
          // wants to know whether their video is going out.
          onStateChange: (state) => setPhase(state === "connected" ? "live" : "publishing"),
        },
      });

      publisherRef.current = publisher;
      await publisher.start();

      setPhase("live");
    } catch (publishError) {
      await publisherRef.current?.stop();
      publisherRef.current = null;

      // A revoked device is told plainly rather than being left to retry against a door that is
      // now locked.
      if (publishError instanceof ApiError && publishError.status === 401) {
        setPhase("revoked");
        setError("This device has been removed from the session.");
        return;
      }

      setPhase("error");
      setError(
        publishError instanceof ApiError
          ? publishError.message
          : "We could not start sending video. Check your connection and try again.",
      );
    } finally {
      setBusy(false);
    }
  }, []);

  const replaceTrack = useCallback(async (track: MediaStreamTrack) => {
    await publisherRef.current?.replaceTrack(track);
  }, []);

  const stop = useCallback(async () => {
    await publisherRef.current?.stop();
    publisherRef.current = null;
    setPhase(session ? "paired" : "idle");
  }, [session]);

  const clearError = useCallback(() => setError(null), []);

  return { phase, session, error, busy, pair, publish, replaceTrack, stop, clearError };
}
