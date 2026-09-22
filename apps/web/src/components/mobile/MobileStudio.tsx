"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { MobileControlBar } from "@/components/mobile/MobileControlBar";
import { MobileDetailsSheet } from "@/components/mobile/MobileDetailsSheet";
import { MobilePreview } from "@/components/mobile/MobilePreview";
import { MobileStatusBar } from "@/components/mobile/MobileStatusBar";
import { useAdaptiveQuality } from "@/hooks/useAdaptiveQuality";
import { useBroadcaster } from "@/hooks/useBroadcaster";
import { useDestinations } from "@/hooks/useDestinations";
import { useElapsedSeconds } from "@/hooks/useElapsed";
import { useLiveSession } from "@/hooks/useLiveSession";
import { useMobileCapture } from "@/hooks/useMobileCapture";
import { useStreamKey } from "@/hooks/useStreamKey";
import { useWakeLock } from "@/hooks/useWakeLock";
import type { CaptureQuality } from "@/lib/media/devices";

/**
 * The mobile broadcaster (Phase 3).
 *
 * The same session, the same control plane and the same WHIP ingest as the desktop studio — a
 * broadcast started here is indistinguishable from one started there, and can be taken over from
 * there. What differs is everything above the media layer, and it differs because a phone is used
 * differently:
 *
 * - **One screen, four controls.** The studio's panels are a control room. This is a camera.
 * - **No compositor.** The studio composes a program on a canvas at 30fps and mixes audio through
 *   a Web Audio graph. On a phone that is a second encoder's worth of work for a layout nobody can
 *   build one-handed, so the camera publishes directly.
 * - **Quality adapts itself** (ADR 0021), because nobody filming is watching a health panel.
 * - **The screen stays awake**, because a phone locking suspends capture and freezes the picture.
 *
 * Session state still comes from the server and is never inferred here — the same rule the studio
 * follows, and the reason a broadcast survives this page being reloaded.
 */
export function MobileStudio({ sessionId }: { sessionId: string }) {
  const [sheetOpen, setSheetOpen] = useState(false);

  /**
   * Breaks the circle between capture and publishing: the capture hook has to push a swapped
   * camera into the transport, and the transport is created after it.
   */
  const replaceTrackRef = useRef<((track: MediaStreamTrack) => Promise<void>) | null>(null);

  const publishTrack = useCallback(
    (track: MediaStreamTrack) => replaceTrackRef.current?.(track),
    [],
  );

  const media = useMobileCapture({
    onVideoTrackChanged: publishTrack,
    onAudioTrackChanged: publishTrack,
  });

  const destinations = useDestinations(sessionId);

  // Publishing a game or a screen from an encoder app on this phone, rather than the camera.
  const streamKey = useStreamKey(sessionId);

  const { session, status, health, loading, error, refresh, applyStatus } = useLiveSession(sessionId, {
    onDestinationStateChanged: destinations.applyRealtime,
  });

  const broadcaster = useBroadcaster({ sessionId, onStatus: applyStatus, onRefresh: refresh });

  useEffect(() => {
    replaceTrackRef.current = broadcaster.replaceTrack;
  }, [broadcaster.replaceTrack]);

  const sessionStatus = status?.status ?? session?.status ?? "DRAFT";
  const isLive = sessionStatus === "LIVE" || sessionStatus === "DEGRADED";
  const isReconnecting = sessionStatus === "RECONNECTING";
  const isBroadcasting = status?.isBroadcasting ?? isLive;
  const isStarting = sessionStatus === "STARTING";
  const isEnded = sessionStatus === "ENDED" || sessionStatus === "FAILED";

  /**
   * Adaptation runs only while something is actually publishing.
   *
   * Left running it would be deciding about a null sample forever, which changes nothing but keeps
   * a state machine ticking on a device whose battery is the length of the broadcast.
   */
  const adaptive = useAdaptiveQuality({
    enabled: isBroadcasting || isStarting,
    quality: broadcaster.quality,
    applyEncoding: broadcaster.applyEncoding,
  });

  // The screen must not lock while the camera is on air, and must be allowed to lock afterwards.
  useWakeLock(isBroadcasting || isStarting);

  const elapsedSeconds = useElapsedSeconds(
    status?.startedAt ?? session?.startedAt,
    status?.endedAt ?? session?.endedAt,
  );

  /**
   * Streaming something this browser cannot see — a game, captured by an encoder app on this same
   * phone, publishing over RTMP (ADR 0022). Having asked for a key is the operator saying so.
   */
  const usingEncoder = streamKey.key !== null;

  const canGoLive =
    (usingEncoder || (media.permission === "granted" && media.hasCapture))
    && !isBroadcasting
    && !isStarting
    && !isEnded;

  const handleGoLive = useCallback(() => {
    // The camera wins when there is one: a phone with its viewfinder open is broadcasting that.
    if (media.stream && media.hasCapture) {
      void broadcaster.goLive(media.stream);
      return;
    }

    void broadcaster.goLiveExternal();
  }, [broadcaster, media.stream, media.hasCapture]);

  const handleStop = useCallback(() => void broadcaster.stopLive(), [broadcaster]);

  /**
   * Changing the capture rung by hand puts the ladder back at the top.
   *
   * Their choice is about the camera; the ladder's position is about a link that has just been
   * given a different amount of work. Carrying "minimum" into a freshly chosen 540p capture would
   * quietly halve what they asked for.
   */
  const handleSelectQuality = useCallback(
    (next: CaptureQuality) => {
      void media.setQuality(next).then(() => adaptive.reset());
    },
    [media, adaptive],
  );

  // Once the session is over the camera light goes out without needing a navigation.
  useEffect(() => {
    if (isEnded) media.release();
    // `release` is idempotent, and re-running it on an unrelated media change is harmless.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isEnded]);

  /**
   * Picks the broadcast back up after this page is reloaded or restored.
   *
   * On a phone this is not the rare case it is on a desktop: iOS discards backgrounded tabs
   * routinely, and the session is still live inside its recovery window when the page comes back.
   * Gated on the server reporting that nothing is currently publishing, so a second device
   * observing the session does not fight the first one for the ingest path.
   */
  useEffect(() => {
    if (!isBroadcasting || isEnded || health?.ingestConnected !== false) return;
    if (broadcaster.busy || broadcaster.connectionState !== "idle") return;

    // Never while an encoder is the source. The video is coming from a game capture app, and
    // "ingest is down" there means the encoder is reconnecting — opening this camera and
    // publishing it would take the session away from the thing the audience came for (ADR 0022).
    if (usingEncoder) return;

    if (media.permission === "idle") {
      void media.requestAccess();
      return;
    }

    if (media.stream) void broadcaster.resume(media.stream);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    isBroadcasting,
    isEnded,
    health?.ingestConnected,
    media.permission,
    media.stream,
    broadcaster.busy,
    broadcaster.connectionState,
    usingEncoder,
  ]);

  if (loading && !session) {
    return (
      <div className="flex min-h-[100dvh] items-center justify-center bg-black">
        <p className="text-sm text-slate-500">Loading…</p>
      </div>
    );
  }

  return (
    <div className="fixed inset-0 overflow-hidden bg-black" data-testid="mobile-studio">
      <MobilePreview
        stream={media.stream}
        hasVideo={media.videoTrack !== null}
        facing={media.facing}
        cameraEnabled={media.cameraEnabled}
        interrupted={media.interrupted && (isBroadcasting || isStarting)}
      />

      <div className="relative flex h-full flex-col justify-between">
        <MobileStatusBar
          title={session?.title ?? "Live session"}
          status={sessionStatus}
          isLive={isLive}
          elapsedSeconds={elapsedSeconds}
          viewerCount={health?.viewerCount ?? null}
          verdict={broadcaster.quality?.verdict ?? null}
          reduced={adaptive.reduced}
          onOpenDetails={() => setSheetOpen(true)}
        />

        {/* The middle is left empty on purpose: it is the shot. Only things that must be read
            interrupt it, and each of them can be dismissed or acted on in one tap. */}
        <div className="flex flex-col items-center gap-2 px-4">
          {media.permission === "idle" || media.permission === "requesting" ? (
            <Notice>
              <p className="mb-3 text-sm text-slate-200">
                Allow camera and microphone access to broadcast from this phone.
              </p>
              <button
                type="button"
                onClick={() => void media.requestAccess()}
                disabled={media.permission === "requesting"}
                data-testid="mobile-allow"
                className="min-h-11 w-full rounded-lg bg-sky-500 px-4 text-sm font-semibold text-white disabled:opacity-50"
              >
                {media.permission === "requesting" ? "Asking…" : "Allow access"}
              </button>
            </Notice>
          ) : null}

          {media.error ? (
            <Notice tone="error">
              <p className="text-sm font-medium text-red-200">{media.error.userMessage}</p>
              <p className="mt-1 text-xs text-red-300/80">{media.error.recovery}</p>
              {/*
                A retry, because the recovery for the commonest of these happens outside the page:
                on iOS a refused camera is re-granted in Settings, and coming back to a screen with
                no way forward but a reload is a dead end.
              */}
              <button
                type="button"
                onClick={() => void media.requestAccess()}
                disabled={media.permission === "requesting"}
                data-testid="mobile-retry-access"
                className="mt-3 min-h-11 w-full rounded-lg bg-red-900/60 px-4 text-sm font-semibold text-red-100 disabled:opacity-50"
              >
                Try again
              </button>
            </Notice>
          ) : null}

          {broadcaster.error ? (
            <Notice tone="error">
              <p className="text-sm text-red-200">{broadcaster.error}</p>
              <button
                type="button"
                onClick={broadcaster.clearError}
                className="mt-2 min-h-11 text-xs font-semibold text-red-300"
              >
                Dismiss
              </button>
            </Notice>
          ) : null}

          {error ? (
            <Notice tone="error">
              <p className="text-sm text-red-200">{error}</p>
            </Notice>
          ) : null}

          {isReconnecting ? (
            <Notice tone="caution">
              <p className="text-sm text-amber-200" data-testid="mobile-reconnecting">
                Reconnecting
                {broadcaster.reconnectAttempt > 0 ? ` — attempt ${broadcaster.reconnectAttempt}` : ""}. Your
                session stays live while we retry.
              </p>
            </Notice>
          ) : null}

          {/* The one thing the ladder owes the broadcaster: a sentence saying what it did. */}
          {adaptive.notice ? (
            <Notice tone="caution">
              <p className="text-sm text-amber-200" data-testid="mobile-adaptive-notice">
                {adaptive.notice}
              </p>
              <button
                type="button"
                onClick={adaptive.dismissNotice}
                className="mt-2 min-h-11 text-xs font-semibold text-amber-300"
              >
                Dismiss
              </button>
            </Notice>
          ) : null}

          {media.notice ? (
            <Notice>
              <p className="text-sm text-slate-200">{media.notice}</p>
              <button
                type="button"
                onClick={media.dismissNotice}
                className="mt-2 min-h-11 text-xs font-semibold text-slate-400"
              >
                Dismiss
              </button>
            </Notice>
          ) : null}

          {isEnded ? (
            <Notice>
              <p className="text-sm text-slate-200">This session has ended.</p>
              {session?.recordingEnabled ? (
                <p className="mt-1 text-xs text-slate-400">
                  The recording appears on the playback page once it has been finalized.
                </p>
              ) : null}
              <Link
                href={`/watch/${sessionId}`}
                className="mt-3 block min-h-11 rounded-lg bg-slate-800 px-4 py-3 text-center text-sm font-semibold text-slate-100"
              >
                View playback page
              </Link>
            </Notice>
          ) : null}

          {isStarting ? (
            <p className="text-center text-xs text-slate-400">
              Waiting for the streaming server to confirm your video.
            </p>
          ) : null}
        </div>

        <MobileControlBar
          isBroadcasting={isBroadcasting}
          isStarting={isStarting}
          busy={broadcaster.busy}
          canGoLive={canGoLive}
          cameraEnabled={media.cameraEnabled}
          microphoneEnabled={media.microphoneEnabled}
          hasCamera={media.videoTrack !== null}
          hasMicrophone={media.audioTrack !== null}
          canFlip={media.canFlip}
          switching={media.switching}
          onGoLive={handleGoLive}
          onStop={handleStop}
          onFlip={() => void media.flipCamera()}
          onToggleCamera={media.toggleCamera}
          onToggleMicrophone={media.toggleMicrophone}
        />
      </div>

      {sheetOpen ? (
        <MobileDetailsSheet
          sessionId={sessionId}
          quality={media.quality}
          qualityReason={media.qualityReason}
          videoFormat={media.videoFormat}
          measured={broadcaster.quality}
          reducedLabel={adaptive.reduced ? adaptive.rung.label : null}
          destinations={destinations.destinations}
          busyDestinationId={destinations.busyId}
          streamKey={streamKey}
          sessionIsEnded={isEnded}
          switching={media.switching}
          onSelectQuality={handleSelectQuality}
          onStartDestination={(id) => void destinations.start(id)}
          onStopDestination={(id) => void destinations.stop(id)}
          onClose={() => setSheetOpen(false)}
        />
      ) : null}
    </div>
  );
}

function Notice({
  tone = "neutral",
  children,
}: {
  tone?: "neutral" | "caution" | "error";
  children: React.ReactNode;
}) {
  const ring =
    tone === "error"
      ? "bg-red-950/90 ring-red-800"
      : tone === "caution"
        ? "bg-amber-950/90 ring-amber-800"
        : "bg-slate-900/90 ring-slate-700";

  return <div className={`w-full max-w-sm rounded-xl px-4 py-3 ring-1 backdrop-blur ${ring}`}>{children}</div>;
}
