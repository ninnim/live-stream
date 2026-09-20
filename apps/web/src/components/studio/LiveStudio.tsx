"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef } from "react";
import { CameraPreview } from "@/components/studio/CameraPreview";
import { DestinationPanel } from "@/components/studio/DestinationPanel";
import { DeviceControls } from "@/components/studio/DeviceControls";
import { HealthPanel } from "@/components/studio/HealthPanel";
import { AiPanel } from "@/components/studio/AiPanel";
import { AudioMixerPanel } from "@/components/studio/AudioMixerPanel";
import { OverlayPanel } from "@/components/studio/OverlayPanel";
import { ProgramPanel } from "@/components/studio/ProgramPanel";
import { RecordingPanel } from "@/components/studio/RecordingPanel";
import { ScenePanel } from "@/components/studio/ScenePanel";
import { SourcePanel } from "@/components/studio/SourcePanel";
import {
  DeviceNoticeBanner,
  ErrorBanner,
  MediaErrorBanner,
  NoCameraPrompt,
  PermissionPrompt,
  ReconnectingBanner,
} from "@/components/studio/StatusBanners";
import { Badge, Button, Card } from "@/components/ui/primitives";
import { useAiJobs } from "@/hooks/useAiJobs";
import { useBroadcaster } from "@/hooks/useBroadcaster";
import { useDestinations } from "@/hooks/useDestinations";
import { useElapsedSeconds } from "@/hooks/useElapsed";
import { MICROPHONE_INPUT, SCREEN_INPUT, useInputLevels } from "@/hooks/useInputLevels";
import { useLocalRecording } from "@/hooks/useLocalRecording";
import { useMachineCapability } from "@/hooks/useMachineCapability";
import { useLiveSession } from "@/hooks/useLiveSession";
import { useMediaDevices } from "@/hooks/useMediaDevices";
import { useProgram } from "@/hooks/useProgram";
import { useStudioConfig } from "@/hooks/useStudioConfig";
import { useStudioHotkeys } from "@/hooks/useStudioHotkeys";
import { useSources } from "@/hooks/useSources";
import { statusLabel, statusTone } from "@/lib/format";

/**
 * The Web Live Studio: the screen that lets a creator broadcast from the platform itself, with no
 * OBS and no external encoder (MASTER_BLUEPRINT.md §8A).
 *
 * Division of responsibility, per the architecture rules:
 *  - the browser owns capture, preview, device state, and the media transport;
 *  - the server owns session state, health, and whether the session is actually LIVE.
 *
 * The studio therefore renders the server's status, never its own guess at it.
 */
export function LiveStudio({ sessionId }: { sessionId: string }) {
  /**
   * Breaks the circle between capture and publishing: the media hook must be able to push a new
   * track into the broadcaster, and the broadcaster is created after it. Assigned below, on every
   * render, so the swap always reaches the current publisher.
   */
  const replaceTrackRef = useRef<((track: MediaStreamTrack) => Promise<void>) | null>(null);
  const removeTrackRef = useRef<((kind: "video" | "audio") => Promise<void>) | null>(null);

  /**
   * While the program is being composed, local capture feeds the compositor and the mixer — not
   * the transport. Publishing the raw camera here as well would overwrite the canvas the audience
   * is watching, so a device change mid-show would cut everyone to the operator's webcam.
   */
  const composingRef = useRef(false);

  const publishLocalTrack = useCallback((track: MediaStreamTrack) => {
    if (composingRef.current) return undefined;
    return replaceTrackRef.current?.(track);
  }, []);

  // Asked once, on the way in: what can this machine actually encode? The answer chooses the
  // starting rung, and nothing after that — lowering it mid-broadcast stays the operator's call
  // (ADR 0011 §7).
  const capability = useMachineCapability();

  const media = useMediaDevices({
    recommended: capability?.recommended ?? null,
    onVideoTrackChanged: publishLocalTrack,
    onAudioTrackChanged: publishLocalTrack,
    onTrackRemoved: (kind) => removeTrackRef.current?.(kind),
  });

  // Declared before the session hook so their realtime handlers can be handed to the same hub
  // connection, rather than opening a second one per concern.
  const destinations = useDestinations(sessionId);
  const sources = useSources(sessionId);

  const { session, status, health, realtimeConnected, loading, error, refresh, applyStatus } =
    useLiveSession(sessionId, {
      onDestinationStateChanged: destinations.applyRealtime,
      onSourceStateChanged: sources.applyRealtime,
    });

  const broadcaster = useBroadcaster({
    sessionId,
    onStatus: applyStatus,
    onRefresh: refresh,
    hardwareCodecs: capability?.hardwareCodecs,
  });

  useEffect(() => {
    replaceTrackRef.current = broadcaster.replaceTrack;
    removeTrackRef.current = broadcaster.removeTrack;
  }, [broadcaster.replaceTrack, broadcaster.removeTrack]);

  const studioConfig = useStudioConfig(sessionId);
  const ai = useAiJobs(sessionId);

  const cutTo = useCallback(
    (sourceId: string) => void sources.setProgram(sourceId).catch(() => undefined),
    [sources],
  );

  // Measured on the devices, not on what is published: while the screen's sound is being mixed in,
  // the published track is a Web Audio destination, and a meter on it could not tell the broadcaster
  // which of the two sources had gone quiet.
  const inputLevels = useInputLevels({
    [MICROPHONE_INPUT]: media.microphoneTrack,
    [SCREEN_INPUT]: media.screenAudioTrack,
  });

  const program = useProgram({
    sessionId,
    sources: sources.sources,
    localStream: media.stream,
    localAudioTrack: media.audioTrack,
    branding: studioConfig.branding,
    enabled: media.permission === "granted",
    onProgramTrackChanged: (track) => replaceTrackRef.current?.(track),
    onCutRequested: cutTo,
  });

  useEffect(() => {
    composingRef.current = program.composing;
  }, [program.composing]);

  /**
   * Records whatever the studio is showing — the composition once there is one, the plain capture
   * otherwise. The same thing the preview shows and the same thing a broadcast would carry, so
   * there is never a question of which of the two a recording captured.
   */
  const recorder = useLocalRecording({
    title: session?.title ?? "recording",
    sources: {
      video: program.programStream?.getVideoTracks()[0] ?? media.videoTrack,
      audio: program.programStream?.getAudioTracks()[0] ?? media.audioTrack,
    },
  });

  const sessionStatus = status?.status ?? session?.status ?? "DRAFT";
  const isLive = sessionStatus === "LIVE" || sessionStatus === "DEGRADED";
  const isReconnecting = sessionStatus === "RECONNECTING";
  const isBroadcasting = status?.isBroadcasting ?? isLive ?? false;
  const isEnded = sessionStatus === "ENDED" || sessionStatus === "FAILED";
  const isStarting = sessionStatus === "STARTING";

  const elapsedSeconds = useElapsedSeconds(
    status?.startedAt ?? session?.startedAt,
    status?.endedAt ?? session?.endedAt,
  );

  // A camera is optional and so is a microphone, but something has to be going out: a
  // broadcast with neither is a session that would sit in STARTING until it timed out.
  const canGoLive =
    media.permission === "granted" &&
    media.hasCapture &&
    !isBroadcasting &&
    !isStarting &&
    !isEnded;

  const handleGoLive = useCallback(() => {
    if (media.stream) {
      void broadcaster.goLive(media.stream);
    }
  }, [broadcaster, media.stream]);

  const handleStopLive = useCallback(() => {
    void broadcaster.stopLive();
  }, [broadcaster]);

  /**
   * Keyboard control, live only while the session is running.
   *
   * Deliberately no shortcut for going live or stopping: ending a broadcast with a stray keystroke
   * is a failure no undo repairs.
   */
  useStudioHotkeys(
    {
      recallScene: (index) => {
        const scene = studioConfig.scenes[index];
        if (scene) program.applyScene(scene);
      },
      cutToSource: (index) => {
        const feed = program.feeds[index];
        if (feed && !feed.source.isProgram && feed.source.status === "Connected") {
          cutTo(feed.source.id);
        }
      },
      setLayout: program.setLayout,
      toggleLowerThird: () => program.setLowerThirdVisible(!program.lowerThirdVisible),
      toggleMicrophone: media.toggleMicrophone,
      toggleCamera: media.toggleCamera,
    },
    !isEnded && media.permission === "granted",
  );

  // Once the session is over the camera light should go out without needing a page navigation.
  useEffect(() => {
    if (isEnded) {
      media.release();
    }
    // `media` is stable enough for this guard; releasing twice is harmless.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isEnded]);

  /**
   * Pick a broadcast back up after the control room is reloaded.
   *
   * A reload destroys the page and with it the peer connection, but the session is still live and
   * inside its recovery window. Without this the broadcast would quietly bleed out and fail, which
   * is exactly the outcome the reliability rules forbid.
   *
   * Gated on the server reporting that nothing is currently publishing, so opening the studio in a
   * second tab observes the live session instead of fighting the first tab for the ingest path.
   */
  useEffect(() => {
    if (!isBroadcasting || isEnded || health?.ingestConnected !== false) return;
    if (broadcaster.busy || broadcaster.connectionState !== "idle") return;

    if (media.permission === "idle") {
      // Already-granted permission is re-issued without prompting; a revoked one surfaces the
      // usual friendly banner.
      void media.requestAccess();
      return;
    }

    if (media.stream) {
      void broadcaster.resume(media.stream);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    isBroadcasting,
    isEnded,
    health?.ingestConnected,
    media.permission,
    media.stream,
    broadcaster.busy,
    broadcaster.connectionState,
  ]);

  if (loading && !session) {
    return (
      <div className="flex min-h-[60vh] items-center justify-center">
        <p className="text-sm text-slate-500">Loading your studio…</p>
      </div>
    );
  }

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-6 px-4 py-8">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-xs font-medium uppercase tracking-wide text-slate-500">Live Studio</p>
          <h1 className="mt-1 text-2xl font-semibold text-slate-50">{session?.title ?? "Live session"}</h1>
        </div>

        <div className="flex items-center gap-3">
          <Badge tone={statusTone(sessionStatus)} pulse={isLive}>
            {statusLabel(sessionStatus)}
          </Badge>
          <Link href="/dashboard" className="text-sm text-slate-400 underline-offset-4 hover:underline">
            Back to sessions
          </Link>
        </div>
      </header>

      {error ? <ErrorBanner title={error} onRetry={() => void refresh()} /> : null}

      {broadcaster.error ? (
        <ErrorBanner title={broadcaster.error} onDismiss={broadcaster.clearError} />
      ) : null}

      {media.error ? <MediaErrorBanner error={media.error} onRetry={() => void media.requestAccess()} /> : null}

      {media.notice ? <DeviceNoticeBanner message={media.notice} onDismiss={media.dismissNotice} /> : null}

      {isReconnecting ? (
        <ReconnectingBanner
          attempt={broadcaster.reconnectAttempt}
          secondsRemaining={health?.recoveryWindowSecondsRemaining ?? null}
        />
      ) : null}

      {session?.lastErrorMessage && sessionStatus === "FAILED" ? (
        <ErrorBanner title="This session ended unexpectedly." detail={session.lastErrorMessage} />
      ) : null}

      {media.permission === "idle" || media.permission === "requesting" ? (
        <PermissionPrompt
          onRequest={() => void media.requestAccess()}
          onShareScreen={() => void media.shareScreen()}
          busy={media.permission === "requesting"}
        />
      ) : null}

      {/*
        Running on sound alone. Offered as a prompt rather than an error: nothing is broken,
        and there is something useful to do about it.
      */}
      {media.permission === "granted" && media.videoTrack === null && !isEnded ? (
        <NoCameraPrompt onShareScreen={() => void media.shareScreen()} busy={media.switching} />
      ) : null}

      {/*
        Once the program is composed, the preview shows the composition rather than the camera —
        the operator has to be looking at what the audience is getting, not at one of its inputs.
      */}
      <CameraPreview
        stream={program.programStream ?? media.stream}
        cameraEnabled={program.composing || media.cameraEnabled}
        isLive={isLive}
        isReconnecting={isReconnecting}
      />

      {/*
        Offered before Start Live and independently of it. "Just record this" is a whole use for the
        studio that has nothing to do with an audience, and making somebody start a broadcast to get
        a recording would be the wrong shape entirely.
      */}
      {!isEnded && media.permission === "granted" ? (
        <RecordingPanel recorder={recorder} hasCapture={media.hasCapture} />
      ) : null}

      <ProgramPanel
        program={program}
        onCut={cutTo}
        busySourceId={sources.busyId}
        sessionIsEnded={isEnded}
      />

      <AudioMixerPanel program={program} sessionIsEnded={isEnded} />

      {isBroadcasting || isStarting || isEnded ? (
        <HealthPanel
          health={health}
          elapsedSeconds={elapsedSeconds}
          connectionState={broadcaster.connectionState}
          realtimeConnected={realtimeConnected}
          cameraEnabled={media.cameraEnabled}
          microphoneEnabled={media.microphoneEnabled}
          quality={broadcaster.quality}
        />
      ) : null}

      {!isEnded && media.permission === "granted" ? (
        <Card>
          <DeviceControls
            cameras={media.cameras}
            microphones={media.microphones}
            selectedCameraId={media.selectedCameraId}
            selectedMicrophoneId={media.selectedMicrophoneId}
            cameraEnabled={media.cameraEnabled}
            microphoneEnabled={media.microphoneEnabled}
            quality={media.quality}
            videoFormat={media.videoFormat}
            videoSource={media.videoSource}
            screenMode={media.screenMode}
            screenFrameRate={media.screenFrameRate}
            hasVideo={media.videoTrack !== null}
            hasAudio={media.microphoneTrack !== null}
            hasScreenAudio={media.screenAudioTrack !== null}
            audioLevels={media.audioLevels}
            inputLevels={inputLevels}
            microphoneMode={media.microphoneMode}
            capabilityNote={capability?.reason ?? null}
            switching={media.switching}
            onSelectCamera={(id) => void media.selectCamera(id)}
            onSelectMicrophone={(id) => void media.selectMicrophone(id)}
            onSelectQuality={(value) => void media.setQuality(value)}
            onToggleCamera={media.toggleCamera}
            onToggleMicrophone={media.toggleMicrophone}
            onChangeAudioLevels={media.setAudioLevels}
            onSelectMicrophoneMode={(mode) => void media.setMicrophoneMode(mode)}
            onShareScreen={() => void media.shareScreen()}
            onSelectScreenMode={(mode) => void media.setScreenMode(mode)}
            onSelectScreenFrameRate={(rate) => void media.setScreenFrameRate(rate)}
            onStopSharing={() => void media.stopSharing()}
          />
        </Card>
      ) : null}

      {!isEnded && media.permission === "granted" ? (
        <>
          <OverlayPanel config={studioConfig} program={program} sessionIsEnded={isEnded} />
          <ScenePanel
            config={studioConfig}
            program={program}
            sources={sources.sources}
            sessionIsEnded={isEnded}
          />
        </>
      ) : null}

      <SourcePanel sources={sources.sources} sessionIsEnded={isEnded} controller={sources} />

      <AiPanel ai={ai} />

      <DestinationPanel
        destinations={destinations.destinations}
        sessionIsBroadcasting={isBroadcasting}
        sessionIsEnded={isEnded}
        controller={destinations}
      />

      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="text-sm text-slate-500">
          {session?.recordingEnabled ? "This session is being recorded." : "Recording is off for this session."}
        </div>

        <div className="flex gap-3">
          {isBroadcasting || isStarting ? (
            <Button variant="danger" onClick={handleStopLive} disabled={broadcaster.busy}>
              {broadcaster.busy ? "Stopping…" : "Stop Live"}
            </Button>
          ) : null}

          {!isBroadcasting && !isStarting && !isEnded ? (
            <Button onClick={handleGoLive} disabled={!canGoLive || broadcaster.busy}>
              {broadcaster.busy ? "Starting…" : "Start Live"}
            </Button>
          ) : null}

          {isEnded ? (
            <Link
              href={`/watch/${sessionId}`}
              className="inline-flex items-center rounded-lg bg-slate-800 px-4 py-2.5 text-sm font-semibold text-slate-100 hover:bg-slate-700"
            >
              View playback page
            </Link>
          ) : null}
        </div>
      </div>

      {isStarting ? (
        <p className="text-sm text-slate-400">
          Waiting for the streaming server to confirm your video. You will go live automatically.
        </p>
      ) : null}
    </div>
  );
}
