"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { type CaptureOutcome, summarizeCaptureAttempt } from "@/hooks/media-access";
import {
  type CaptureQuality,
  MediaAccessError,
  type TrackFormat,
  describeTrackFormat,
  detectMediaSupport,
  requestCameraTrack,
  requestMicrophoneTrack,
} from "@/lib/media/devices";
import {
  type CameraFacing,
  MOBILE_FRAME_RATE,
  chooseMobileStartingQuality,
  facingOfTrack,
  oppositeFacing,
  readNetworkSignals,
} from "@/lib/media/mobile";

export type MobilePermissionState = "idle" | "requesting" | "granted" | "denied" | "unsupported";

export interface UseMobileCaptureResult {
  permission: MobilePermissionState;
  stream: MediaStream | null;
  videoTrack: MediaStreamTrack | null;
  audioTrack: MediaStreamTrack | null;
  /** Which way the camera is actually pointing, read from the track rather than from the request. */
  facing: CameraFacing;
  /** True when this phone has a second camera to flip to. */
  canFlip: boolean;
  cameraEnabled: boolean;
  microphoneEnabled: boolean;
  quality: CaptureQuality;
  /** What the camera is really producing, which is often not what was asked for. */
  videoFormat: TrackFormat | null;
  /** Why the starting rung is what it is, in one sentence. */
  qualityReason: string;
  /** True while a camera is being opened or swapped, so the controls can show it. */
  switching: boolean;
  /** True while the operating system has taken the camera away — a call, or another app. */
  interrupted: boolean;
  notice: string | null;
  error: MediaAccessError | null;
  hasCapture: boolean;
  requestAccess: () => Promise<void>;
  flipCamera: () => Promise<void>;
  setQuality: (quality: CaptureQuality) => Promise<void>;
  toggleCamera: () => void;
  toggleMicrophone: () => void;
  dismissNotice: () => void;
  release: () => void;
}

export interface UseMobileCaptureOptions {
  /** Pushes a new camera or microphone track into a transport that is already publishing. */
  onVideoTrackChanged?: (track: MediaStreamTrack) => Promise<void> | void;
  onAudioTrackChanged?: (track: MediaStreamTrack) => Promise<void> | void;
}

/**
 * Camera and microphone capture for a phone.
 *
 * Deliberately **not** `useMediaDevices`. That hook is the desktop studio's capture layer and it
 * carries what a studio needs: screen sharing, a Web Audio graph summing a shared screen's sound
 * with the microphone, device enumeration and hot-plug recovery across an arbitrary number of
 * cameras and interfaces. None of it applies to a phone, and two parts of it are actively wrong
 * there — the audio graph costs battery to sum one source with nothing, and 60fps capture rungs
 * cost battery and uplink for frames a handheld broadcast never needed.
 *
 * `docs/08-mobile-app.md` asks for exactly this separation: keep the mobile capture layer apart
 * from the business UI so a future native media path does not mean rewriting the application.
 * What is shared is the part worth sharing — `@/lib/media/devices` opens the hardware and
 * translates its failures, so a phone reports a blocked camera in the same words the studio does.
 *
 * The four mobile facts this hook exists to handle:
 *
 *  1. **Front and back, not a device list.** Nobody picks a phone camera by name.
 *  2. **30fps.** See {@link MOBILE_FRAME_RATE}.
 *  3. **The camera is not ours alone.** A phone call, a switch to another app, or the screen
 *     locking takes it away, and iOS `mute`s the track rather than ending it — so a broadcast that
 *     only listens for `ended` comes back from a phone call sending a frozen frame.
 *  4. **One hand.** Everything here is reachable without a second one.
 */
export function useMobileCapture(options: UseMobileCaptureOptions = {}): UseMobileCaptureResult {
  const [permission, setPermission] = useState<MobilePermissionState>("idle");
  const [stream, setStream] = useState<MediaStream | null>(null);
  const [videoTrack, setVideoTrack] = useState<MediaStreamTrack | null>(null);
  const [audioTrack, setAudioTrack] = useState<MediaStreamTrack | null>(null);
  const [facing, setFacing] = useState<CameraFacing>("user");
  const [canFlip, setCanFlip] = useState(false);
  const [cameraEnabled, setCameraEnabled] = useState(true);
  const [microphoneEnabled, setMicrophoneEnabled] = useState(true);
  const [videoFormat, setVideoFormat] = useState<TrackFormat | null>(null);
  const [switching, setSwitching] = useState(false);
  const [interrupted, setInterrupted] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<MediaAccessError | null>(null);

  /**
   * The starting rung, chosen from the network before anything opens.
   *
   * Computed once, lazily, rather than in an effect: the first `requestAccess` happens on the
   * broadcaster's first tap, and a recommendation that arrived after it would have opened the
   * camera at the wrong rung and then re-opened it at the right one, in front of them.
   */
  const [start] = useState(() =>
    chooseMobileStartingQuality({
      network: readNetworkSignals(
        typeof navigator === "undefined"
          ? null
          : (navigator as Navigator & { connection?: unknown }).connection,
      ),
      cores:
        typeof navigator !== "undefined" && typeof navigator.hardwareConcurrency === "number"
          ? navigator.hardwareConcurrency
          : null,
    }),
  );

  const [quality, setQualityState] = useState<CaptureQuality>(start.quality);

  const streamRef = useRef<MediaStream | null>(null);
  const videoTrackRef = useRef<MediaStreamTrack | null>(null);
  const audioTrackRef = useRef<MediaStreamTrack | null>(null);
  const facingRef = useRef<CameraFacing>("user");
  const qualityRef = useRef<CaptureQuality>(start.quality);
  const releasedRef = useRef(false);

  // Inline callbacks are what every caller passes; holding them in a ref keeps this hook's API
  // stable across the caller's renders.
  const handlersRef = useRef(options);

  useEffect(() => {
    handlersRef.current = options;
  }, [options]);

  const ensureStream = useCallback((): MediaStream => {
    if (!streamRef.current) {
      streamRef.current = new MediaStream();
      setStream(streamRef.current);
    }
    return streamRef.current;
  }, []);

  /**
   * Puts a newly opened track into the preview and, if publishing, into the transport.
   *
   * Order matters and is the same as the studio's: rebuild the preview, swap the sender, then stop
   * the old track. Stopping first would black the outgoing video out for the length of the swap —
   * on a phone, where flipping the camera is a thing people do constantly and on camera, that gap
   * is the whole difference between a flip and a glitch.
   */
  const adoptTrack = useCallback(
    async (track: MediaStreamTrack) => {
      const isVideo = track.kind === "video";
      const previous = isVideo ? videoTrackRef.current : audioTrackRef.current;
      const target = ensureStream();

      if (previous) target.removeTrack(previous);
      target.addTrack(track);

      if (isVideo) {
        videoTrackRef.current = track;
        setVideoTrack(track);
        setVideoFormat(describeTrackFormat(track));
        setCameraEnabled(track.enabled);

        const actual = facingOfTrack(track, facingRef.current);
        facingRef.current = actual;
        setFacing(actual);
      } else {
        audioTrackRef.current = track;
        setAudioTrack(track);
        setMicrophoneEnabled(track.enabled);
      }

      try {
        if (isVideo) {
          await handlersRef.current.onVideoTrackChanged?.(track);
        } else {
          await handlersRef.current.onAudioTrackChanged?.(track);
        }
      } finally {
        // Always release the old hardware, even when the swap was refused: the new track is the
        // one in the preview now, and two open cameras is two camera indicators and double the
        // battery drain.
        previous?.stop();
      }
    },
    [ensureStream],
  );

  const openCamera = useCallback(
    async (wanted: CameraFacing) => {
      facingRef.current = wanted;

      const track = await requestCameraTrack({
        facingMode: wanted,
        quality: qualityRef.current,
        frameRate: MOBILE_FRAME_RATE,
        // A handheld camera is motion. Under load, shed resolution and keep the picture moving.
        contentHint: "motion",
      });

      await adoptTrack(track);
    },
    [adoptTrack],
  );

  const openMicrophone = useCallback(async () => {
    // Voice processing, always: a phone microphone is inches from a mouth in an untreated room,
    // and every phone broadcast that is not one is a phone broadcast of a person talking.
    await adoptTrack(await requestMicrophoneTrack({ mode: "voice" }));
  }, [adoptTrack]);

  /** Counts cameras, which is only how the flip button learns whether there is one to flip to. */
  const countCameras = useCallback(async () => {
    try {
      const devices = await navigator.mediaDevices.enumerateDevices();
      setCanFlip(devices.filter((device) => device.kind === "videoinput").length > 1);
    } catch {
      // Enumeration failing does not stop anything; the flip button simply stays hidden.
      setCanFlip(false);
    }
  }, []);

  const requestAccess = useCallback(async () => {
    const support = detectMediaSupport();
    if (!support.supported) {
      setPermission("unsupported");
      setError(support.error ?? null);
      return;
    }

    releasedRef.current = false;
    setPermission("requesting");
    setError(null);
    setNotice(null);

    // Opened independently, for the same reason the studio does it: a phone whose camera is
    // refused should still be able to broadcast sound, and a chained request never reaches the
    // microphone at all.
    const attempt = {
      camera: await tryOpen(() => openCamera(facingRef.current)),
      microphone: await tryOpen(() => openMicrophone()),
    };

    await countCameras();

    const outcome = summarizeCaptureAttempt(attempt);
    setError(outcome.error);
    setNotice(outcome.notice);

    if (!outcome.granted) {
      setPermission(outcome.error?.reason === "permission-denied" ? "denied" : "idle");
      return;
    }

    setPermission("granted");
  }, [countCameras, openCamera, openMicrophone]);

  /** Runs a capture change with the busy flag and error translation every path needs. */
  const runSwitch = useCallback(async (change: () => Promise<void>) => {
    setSwitching(true);
    setError(null);

    try {
      await change();
    } catch (switchError) {
      setError(
        switchError instanceof MediaAccessError
          ? switchError
          : new MediaAccessError(
              "unknown",
              switchError instanceof Error ? switchError.message : "We could not change the camera.",
              "Try again.",
            ),
      );
    } finally {
      setSwitching(false);
    }
  }, []);

  /**
   * Front to back and back again, without interrupting the broadcast.
   *
   * The new camera is opened before the old one is let go, so the swap lands as a cut rather than
   * a dropout. That costs a moment with two cameras open, which is the right trade: turning the
   * phone round to show somebody what you are looking at is the single most common thing anyone
   * does on a mobile broadcast.
   */
  const flipCamera = useCallback(
    () => runSwitch(() => openCamera(oppositeFacing(facingRef.current))),
    [runSwitch, openCamera],
  );

  const setQuality = useCallback(
    (next: CaptureQuality) =>
      runSwitch(async () => {
        qualityRef.current = next;
        setQualityState(next);

        // Only when a camera is already open. Choosing a rung before going live is remembered and
        // applied when the camera opens.
        if (videoTrackRef.current) await openCamera(facingRef.current);
      }),
    [runSwitch, openCamera],
  );

  const toggleCamera = useCallback(() => {
    const track = videoTrackRef.current;
    if (!track) return;

    // `enabled` rather than stopping the track: the transport keeps its sender, the audience gets
    // black instead of a frozen frame, and turning the camera back on needs no renegotiation.
    track.enabled = !track.enabled;
    setCameraEnabled(track.enabled);
  }, []);

  const toggleMicrophone = useCallback(() => {
    const track = audioTrackRef.current;
    if (!track) return;

    track.enabled = !track.enabled;
    setMicrophoneEnabled(track.enabled);
  }, []);

  /**
   * Recovers the camera after the operating system took it.
   *
   * This is the mobile failure the desktop studio has no equivalent of. A phone call, a switch to
   * another app, or the screen locking suspends capture; iOS reports it by `mute`ing the track
   * rather than ending it, so the track stays "live" and the broadcast keeps sending the last
   * frame it had. Nothing in the WebRTC stack notices, and the audience watches a still picture
   * until the broadcaster works it out for themselves.
   *
   * Re-opening the camera on return is the only reliable repair. It is cheap, and a redundant
   * re-open is invisible.
   */
  const resumeCapture = useCallback(async () => {
    if (releasedRef.current || permission !== "granted") return;

    const track = videoTrackRef.current;
    const lost = !track || track.readyState === "ended" || track.muted;

    if (!lost) {
      setInterrupted(false);
      return;
    }

    await runSwitch(async () => {
      await openCamera(facingRef.current);
      setInterrupted(false);
      setNotice("Your camera came back after the interruption.");
    });
  }, [permission, runSwitch, openCamera]);

  /**
   * Watches the app's own lifecycle, which on a phone is not optional.
   *
   * `visibilitychange` is the signal that survives every route into the background — home button,
   * app switcher, screen lock, incoming call. `pageshow` catches the iOS back-forward cache, where
   * the page is restored from memory without any of the usual load events firing.
   */
  useEffect(() => {
    if (typeof document === "undefined") return;

    const onVisibility = (): void => {
      if (document.visibilityState === "hidden") {
        setInterrupted(true);
        return;
      }

      void resumeCapture();
    };

    const onPageShow = (): void => void resumeCapture();

    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("pageshow", onPageShow);

    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      window.removeEventListener("pageshow", onPageShow);
    };
  }, [resumeCapture]);

  /**
   * A track ending on its own is the Android half of the same story: there, an interruption ends
   * the track outright rather than muting it.
   */
  useEffect(() => {
    if (!videoTrack) return;

    const onEnded = (): void => {
      setInterrupted(true);
      void resumeCapture();
    };

    videoTrack.addEventListener("ended", onEnded);
    return () => videoTrack.removeEventListener("ended", onEnded);
  }, [videoTrack, resumeCapture]);

  const dismissNotice = useCallback(() => setNotice(null), []);

  const release = useCallback(() => {
    releasedRef.current = true;

    for (const track of streamRef.current?.getTracks() ?? []) {
      track.stop();
    }

    streamRef.current = null;
    videoTrackRef.current = null;
    audioTrackRef.current = null;
    setStream(null);
    setVideoTrack(null);
    setAudioTrack(null);
    setVideoFormat(null);
  }, []);

  // Leaving the screen must put the camera light out, and on a phone it must also stop the drain.
  useEffect(() => () => release(), [release]);

  return {
    permission,
    stream,
    videoTrack,
    audioTrack,
    facing,
    canFlip,
    cameraEnabled,
    microphoneEnabled,
    quality,
    videoFormat,
    qualityReason: start.reason,
    switching,
    interrupted,
    notice,
    error,
    hasCapture: videoTrack !== null || audioTrack !== null,
    requestAccess,
    flipCamera,
    setQuality,
    toggleCamera,
    toggleMicrophone,
    dismissNotice,
    release,
  };
}

/** Opens one device, reporting what happened rather than throwing. */
async function tryOpen(open: () => Promise<void>): Promise<CaptureOutcome> {
  try {
    await open();
    return { status: "opened" };
  } catch (openError) {
    return {
      status: "failed",
      error:
        openError instanceof MediaAccessError
          ? openError
          : new MediaAccessError("unknown", "We could not start that device.", "Please try again."),
    };
  }
}
