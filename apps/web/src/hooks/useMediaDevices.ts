"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { type CaptureOutcome, summarizeCaptureAttempt } from "@/hooks/media-access";
import {
  CaptureAudio,
  type CaptureAudioLevels,
  DEFAULT_CAPTURE_LEVELS,
  planCaptureAudio,
} from "@/lib/media/capture-audio";
import {
  type CaptureQuality,
  DEFAULT_CAPTURE_QUALITY,
  MediaAccessError,
  type MediaDeviceOption,
  type TrackFormat,
  describeTrackFormat,
  detectMediaSupport,
  enumerateDevices,
  requestCameraTrack,
  requestMicrophoneTrack,
  type MicrophoneMode,
  DEFAULT_MICROPHONE_MODE,
  type ScreenCaptureMode,
  type ScreenFrameRate,
  DEFAULT_SCREEN_CAPTURE_MODE,
  SCREEN_PROFILES,
  requestScreenCapture,
  stopStream,
} from "@/lib/media/devices";

export type PermissionState = "idle" | "requesting" | "granted" | "denied" | "unsupported";

/** What the outgoing video is currently showing. */
export type VideoSource = "camera" | "screen";

/** Which capture devices to try when entering the studio. Neither is required. */
export interface CaptureRequest {
  camera: boolean;
  microphone: boolean;
}

export interface UseMediaDevicesOptions {
  /**
   * Where to start, worked out from what this machine can actually encode.
   *
   * Applied only before anything is on air, and only while the operator has not chosen for
   * themselves — see {@link useMediaDevices} and ADR 0011 §7.
   */
  recommended?: { quality: CaptureQuality; screenFrameRate: ScreenFrameRate } | null;
  /**
   * Called whenever the outgoing camera track is exchanged, so a live publisher can swap it into
   * the transport it already has open. Rejecting surfaces as a device error and leaves the previous
   * track sending.
   */
  onVideoTrackChanged?: (track: MediaStreamTrack) => Promise<void> | void;
  onAudioTrackChanged?: (track: MediaStreamTrack) => Promise<void> | void;
  /**
   * Called when a capture track goes away without a replacement — the only screen share
   * ended on a machine with no camera, or the last microphone was unplugged. A live
   * publisher uses this to stop sending that kind rather than keep a dead sender open.
   */
  onTrackRemoved?: (kind: "video" | "audio") => Promise<void> | void;
}

export interface UseMediaDevicesResult {
  permission: PermissionState;
  stream: MediaStream | null;
  /**
   * The live capture tracks.
   *
   * Exposed alongside the stream because the stream's identity deliberately never changes — its
   * tracks are swapped inside it. Anything that must react to a *device* change, such as the audio
   * mixer, has to watch the track rather than the container holding it.
   */
  videoTrack: MediaStreamTrack | null;
  /**
   * The audio actually being published: the microphone, the shared screen's sound, or the two
   * summed. Distinct from {@link microphoneTrack}, which is the device.
   */
  audioTrack: MediaStreamTrack | null;
  /** The microphone itself, for the controls that act on the device rather than the mix. */
  microphoneTrack: MediaStreamTrack | null;
  /** The shared screen's audio, when the sharer included it. Null far more often than not. */
  screenAudioTrack: MediaStreamTrack | null;
  /** Levels for the two capture sources, 0 to 1. */
  audioLevels: CaptureAudioLevels;
  setAudioLevels: (levels: CaptureAudioLevels) => void;
  cameras: MediaDeviceOption[];
  microphones: MediaDeviceOption[];
  selectedCameraId: string | null;
  selectedMicrophoneId: string | null;
  cameraEnabled: boolean;
  microphoneEnabled: boolean;
  quality: CaptureQuality;
  /** What the camera is actually producing, which is often not what was requested. */
  videoFormat: TrackFormat | null;
  videoSource: VideoSource;
  /** What a shared screen is carrying: slides, or something that moves. */
  screenMode: ScreenCaptureMode;
  /** Capture rate of a shared screen. Above 60 the extra frames do not survive encoding. */
  screenFrameRate: ScreenFrameRate;
  /** What the microphone is carrying, which decides how the browser treats it. */
  microphoneMode: MicrophoneMode;
  /** True while a device change is in flight, so the picker can show it rather than appear stuck. */
  switching: boolean;
  /** Transient, dismissable information — a device appearing or disappearing. Never an error. */
  notice: string | null;
  error: MediaAccessError | null;
  /**
   * Opens the studio with whichever of the requested devices can be opened.
   *
   * Neither is required: a machine with no webcam enters with a microphone, and a machine
   * with neither enters by sharing a screen. Access is only refused when nothing at all
   * could be acquired.
   */
  requestAccess: (want?: CaptureRequest) => Promise<void>;
  selectCamera: (deviceId: string) => Promise<void>;
  selectMicrophone: (deviceId: string) => Promise<void>;
  setQuality: (quality: CaptureQuality) => Promise<void>;
  setMicrophoneMode: (mode: MicrophoneMode) => Promise<void>;
  /** Whether a camera exists on this machine at all, as opposed to one being selected. */
  cameraAvailable: boolean;
  microphoneAvailable: boolean;
  /** True once at least one capture track is open, which is what going live requires. */
  hasCapture: boolean;
  shareScreen: (mode?: ScreenCaptureMode) => Promise<void>;
  setScreenMode: (mode: ScreenCaptureMode) => Promise<void>;
  setScreenFrameRate: (rate: ScreenFrameRate) => Promise<void>;
  stopSharing: () => Promise<void>;
  toggleCamera: () => void;
  toggleMicrophone: () => void;
  dismissNotice: () => void;
  release: () => void;
}

/**
 * Owns camera and microphone acquisition for the studio: permissions, device lists, live switching,
 * mute toggles, hot-plug recovery, and capture quality.
 *
 * Two decisions shape the whole hook.
 *
 * The preview stream is created once and its tracks are exchanged in place. A new `MediaStream` per
 * switch would force the preview element to rebind and flash black, which during a live show looks
 * exactly like a fault.
 *
 * Video and audio are acquired independently. Asking for both at once — the obvious thing — means
 * changing camera also re-opens the microphone, and re-opening a live microphone is an audible
 * dropout for everyone watching.
 */
export function useMediaDevices(options: UseMediaDevicesOptions = {}): UseMediaDevicesResult {
  const [permission, setPermission] = useState<PermissionState>("idle");
  const [stream, setStream] = useState<MediaStream | null>(null);
  const [cameras, setCameras] = useState<MediaDeviceOption[]>([]);
  const [microphones, setMicrophones] = useState<MediaDeviceOption[]>([]);
  const [selectedCameraId, setSelectedCameraId] = useState<string | null>(null);
  const [selectedMicrophoneId, setSelectedMicrophoneId] = useState<string | null>(null);
  const [cameraEnabled, setCameraEnabled] = useState(true);
  const [microphoneEnabled, setMicrophoneEnabled] = useState(true);
  const [quality, setQualityState] = useState<CaptureQuality>(DEFAULT_CAPTURE_QUALITY);
  const [videoFormat, setVideoFormat] = useState<TrackFormat | null>(null);
  const [videoSource, setVideoSource] = useState<VideoSource>("camera");
  const [screenMode, setScreenModeState] = useState<ScreenCaptureMode>(DEFAULT_SCREEN_CAPTURE_MODE);
  const [screenFrameRate, setScreenFrameRateState] = useState<ScreenFrameRate>(
    SCREEN_PROFILES[DEFAULT_SCREEN_CAPTURE_MODE].frameRate,
  );
  const [microphoneMode, setMicrophoneModeState] = useState<MicrophoneMode>(DEFAULT_MICROPHONE_MODE);
  const [switching, setSwitching] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<MediaAccessError | null>(null);

  /**
   * The live tracks, held twice on purpose.
   *
   * The refs are for synchronous reads inside callbacks; the state is what the `ended` listeners
   * key off. Watching a derived value instead — the selected camera id, say — silently fails for a
   * screen share, which deliberately leaves that selection alone.
   */
  const [videoTrack, setVideoTrack] = useState<MediaStreamTrack | null>(null);
  const [audioTrack, setAudioTrack] = useState<MediaStreamTrack | null>(null);
  const [microphoneTrack, setMicrophoneTrack] = useState<MediaStreamTrack | null>(null);
  const [screenAudioTrack, setScreenAudioTrack] = useState<MediaStreamTrack | null>(null);
  const [audioLevels, setAudioLevelsState] = useState<CaptureAudioLevels>(DEFAULT_CAPTURE_LEVELS);

  const streamRef = useRef<MediaStream | null>(null);
  const videoTrackRef = useRef<MediaStreamTrack | null>(null);
  /** The microphone device. What is published may be this, or this summed with the screen. */
  const microphoneTrackRef = useRef<MediaStreamTrack | null>(null);
  const screenAudioTrackRef = useRef<MediaStreamTrack | null>(null);
  const publishedAudioRef = useRef<MediaStreamTrack | null>(null);
  const audioLevelsRef = useRef<CaptureAudioLevels>(DEFAULT_CAPTURE_LEVELS);

  /** Built lazily, and only when there is more than one source to sum. */
  const captureAudioRef = useRef<CaptureAudio>(new CaptureAudio());
  const cameraIdRef = useRef<string | null>(null);
  const videoSourceRef = useRef<VideoSource>("camera");
  const screenModeRef = useRef<ScreenCaptureMode>(DEFAULT_SCREEN_CAPTURE_MODE);
  const screenFrameRateRef = useRef<ScreenFrameRate>(
    SCREEN_PROFILES[DEFAULT_SCREEN_CAPTURE_MODE].frameRate,
  );
  const qualityRef = useRef<CaptureQuality>(DEFAULT_CAPTURE_QUALITY);

  /**
   * Whether the operator has chosen a rung or a frame rate for themselves.
   *
   * Once they have, the machine's recommendation stops applying. A probe that arrives a moment
   * after somebody picked 1080p and quietly moved them back to 720p would be worse than no
   * recommendation at all.
   */
  const qualityChosenRef = useRef(false);
  const screenRateChosenRef = useRef(false);
  const microphoneModeRef = useRef<MicrophoneMode>(DEFAULT_MICROPHONE_MODE);
  const cameraCountRef = useRef<number | null>(null);
  const microphoneCountRef = useRef<number | null>(null);

  // Held in a ref so that a caller passing inline callbacks — which every caller does — cannot
  // re-create this hook's entire API on every render.
  const handlersRef = useRef(options);

  useEffect(() => {
    handlersRef.current = options;
  }, [options]);

  /**
   * Adopts what the machine can encode, as a starting point.
   *
   * Only while nothing is open and nothing has been chosen: a probe that resolved a moment after
   * the operator picked a rung, or after a camera was already running at one, would move them
   * without being asked. Once a capture track exists, the recommendation has missed its window and
   * is offered in the interface instead.
   */
  const recommended = options.recommended ?? null;

  useEffect(() => {
    if (!recommended) return;
    if (videoTrackRef.current !== null) return;

    if (!qualityChosenRef.current) {
      qualityRef.current = recommended.quality;
      setQualityState(recommended.quality);
    }

    if (!screenRateChosenRef.current) {
      screenFrameRateRef.current = recommended.screenFrameRate;
      setScreenFrameRateState(recommended.screenFrameRate);
    }
  }, [recommended]);

  const ensureStream = useCallback((): MediaStream => {
    if (!streamRef.current) {
      streamRef.current = new MediaStream();
      setStream(streamRef.current);
    }
    return streamRef.current;
  }, []);

  const refreshDeviceList = useCallback(async (): Promise<MediaDeviceOption[]> => {
    try {
      const { cameras: videoDevices, microphones: audioDevices } = await enumerateDevices();
      setCameras(videoDevices);
      setMicrophones(audioDevices);
      cameraCountRef.current = videoDevices.length;
      microphoneCountRef.current = audioDevices.length;
      return videoDevices;
    } catch {
      // A failed enumeration leaves the previous list in place; capture still works.
      return [];
    }
  }, []);

  /**
   * Works out what audio should be going out, and makes it so.
   *
   * Called whenever a source or a level changes: a microphone swapped, a screen share started or
   * ended, a slider moved. The published track only changes when the *shape* of the mix changes —
   * moving a level adjusts a gain node inside a graph whose output track stays the same object,
   * because swapping the published audio track is audible and doing it per slider tick would be
   * absurd.
   */
  const reconcileAudio = useCallback(async () => {
    const plan = planCaptureAudio(
      { microphone: microphoneTrackRef.current, screen: screenAudioTrackRef.current },
      audioLevelsRef.current,
    );

    const published = await captureAudioRef.current.applyAsync(plan);
    const previous = publishedAudioRef.current;

    if (published === previous) {
      return;
    }

    const target = ensureStream();
    if (previous) target.removeTrack(previous);
    if (published) target.addTrack(published);

    publishedAudioRef.current = published;
    setAudioTrack(published);

    if (published) {
      await handlersRef.current.onAudioTrackChanged?.(published);
    } else {
      await handlersRef.current.onTrackRemoved?.("audio");
    }
  }, [ensureStream]);

  // Held in a ref so `adoptTrack` can call it without the two depending on each other.
  const reconcileAudioRef = useRef(reconcileAudio);

  useEffect(() => {
    reconcileAudioRef.current = reconcileAudio;
  }, [reconcileAudio]);

  /**
   * Puts a new track into the preview and, if publishing, into the transport.
   *
   * The order is deliberate: the preview is rebuilt first, the sender is swapped second, and only
   * then is the old track stopped. Stopping first would black out the outgoing video for as long as
   * the swap takes.
   */
  const adoptTrack = useCallback(
    async (track: MediaStreamTrack) => {
      const isVideo = track.kind === "video";
      const previous = isVideo ? videoTrackRef.current : microphoneTrackRef.current;
      const target = ensureStream();

      if (isVideo) {
        if (previous) target.removeTrack(previous);
        target.addTrack(track);

        videoTrackRef.current = track;
        setVideoTrack(track);
        // A screen track carries a display id, not a camera id. Writing it into the camera
        // selection would blank the picker and lose the camera to come back to.
        if (videoSourceRef.current === "camera") {
          cameraIdRef.current = track.getSettings?.().deviceId ?? null;
          setSelectedCameraId(cameraIdRef.current);
        }
        setVideoFormat(describeTrackFormat(track));
        setCameraEnabled(track.enabled);

        try {
          await handlersRef.current.onVideoTrackChanged?.(track);
        } finally {
          // Always release the old hardware, even if the swap was rejected: the new track is now
          // the one in the preview, and leaving two devices open lights two camera indicators.
          previous?.stop();
        }

        return;
      }

      // The microphone is not published directly — what goes out may be a mix of it and the shared
      // screen — so it is recorded as the device and the published track is reconciled from there.
      microphoneTrackRef.current = track;
      setMicrophoneTrack(track);
      setSelectedMicrophoneId(track.getSettings?.().deviceId ?? null);
      setMicrophoneEnabled(track.enabled);

      try {
        await reconcileAudioRef.current();
      } finally {
        previous?.stop();
      }
    },
    [ensureStream],
  );

  /**
   * Removes a capture track and stops its hardware, leaving the studio running on whatever
   * remains.
   *
   * This is what makes a device genuinely optional. Without it, ending a screen share on a
   * machine with no camera, or unplugging the only microphone, would have nothing to fall
   * back to and would surface as an error rather than as one fewer source.
   */
  const dropTrack = useCallback(
    async (kind: "video" | "audio") => {
      if (kind === "audio") {
        // Only the microphone: a shared screen's sound goes away with the share itself. Losing the
        // mic while a game is still playing should leave the game audible, not silence everything.
        const microphone = microphoneTrackRef.current;
        if (!microphone) return;

        microphoneTrackRef.current = null;
        setMicrophoneTrack(null);

        try {
          // Reconciling is what removes the published track and reports it gone — and it republishes
          // the screen audio on its own if that is what is left.
          await reconcileAudioRef.current();
        } finally {
          microphone.stop();
        }

        return;
      }

      const track = videoTrackRef.current;
      if (!track) return;

      streamRef.current?.removeTrack(track);
      videoTrackRef.current = null;
      setVideoTrack(null);
      setVideoFormat(null);
      videoSourceRef.current = "camera";
      setVideoSource("camera");

      try {
        await handlersRef.current.onTrackRemoved?.("video");
      } finally {
        track.stop();
      }
    },
    [],
  );

  /**
   * Lets go of a shared screen's sound.
   *
   * Separate from `dropTrack("audio")` because the two are different events with different
   * recoveries: a microphone going away is worth telling somebody about and worth falling back
   * from, while a share ending is the operator's own doing and needs nothing said.
   */
  const dropScreenAudio = useCallback(async () => {
    const track = screenAudioTrackRef.current;
    if (!track) return;

    screenAudioTrackRef.current = null;
    setScreenAudioTrack(null);

    try {
      await reconcileAudioRef.current();
    } finally {
      track.stop();
    }
  }, []);

  const openCamera = useCallback(
    async (deviceId?: string) => {
      const track = await requestCameraTrack({ deviceId, quality: qualityRef.current });
      // Set after the request succeeds: a refused camera must not leave the studio believing it
      // stopped sharing.
      videoSourceRef.current = "camera";
      setVideoSource("camera");
      await adoptTrack(track);
    },
    [adoptTrack],
  );

  const openMicrophone = useCallback(
    async (deviceId?: string) => {
      const track = await requestMicrophoneTrack({
        deviceId,
        mode: microphoneModeRef.current,
      });
      await adoptTrack(track);
    },
    [adoptTrack],
  );
  /**
   * Recovers from a capture track ending on its own.
   *
   * A track ends when its device goes away — the USB camera is unplugged, the interface is pulled,
   * the phone bridge quits, or the browser's own "Stop sharing" bar is pressed. Falling back to any
   * remaining device of that kind keeps the broadcast up, which matters far more than staying on
   * the operator's first choice.
   *
   * When there is nothing to fall back to, the track is dropped and the broadcast continues without
   * it. Ending a screen share on a machine with no webcam is not a fault, and it must not surface
   * as one.
   */
  const recoverFrom = useCallback(
    async (kind: "video" | "audio") => {
      try {
        // The ended track is deliberately left in place for `adoptTrack` to remove and stop.
        // Clearing it here would orphan it inside the preview stream, leaving two video tracks on
        // a stream that is meant to carry one.
        if (kind === "video") {
          const wasSharing = videoSourceRef.current === "screen";

          // A share's sound goes with the share. Chromium usually ends both tracks together, but
          // "usually" is not something a live broadcast should rely on to stop transmitting a
          // game the viewer can no longer see.
          if (wasSharing) await dropScreenAudio();

          if (cameraCountRef.current === 0) {
            await dropTrack("video");
            setNotice(
              wasSharing
                ? "Screen sharing ended. There is no camera on this machine, so the broadcast has no picture."
                : "Your camera was disconnected, and there is no other one to switch to.",
            );
          } else {
            await openCamera(wasSharing ? (cameraIdRef.current ?? undefined) : undefined);
            setNotice(
              wasSharing
                ? "Screen sharing ended. You are back on camera."
                : "Your camera was disconnected. Switched to another camera.",
            );
          }
        } else if (microphoneCountRef.current === 0) {
          await dropTrack("audio");

          // Read after the drop, because dropping is what decides whether anything is left. A
          // shared screen's sound keeps the broadcast audible, and saying otherwise would send
          // somebody looking for a fault that is not there.
          setNotice(
            screenAudioTrackRef.current
              ? "Your microphone was disconnected, and there is no other one. Your screen's sound is still going out."
              : "Your microphone was disconnected, and there is no other one. The broadcast has no sound.",
          );
        } else {
          await openMicrophone();
          setNotice("Your microphone was disconnected. Switched to another microphone.");
        }
      } catch (recoveryError) {
        // Falling back failed too — the remaining device is busy, or was unplugged in between.
        // Drop the track so the studio reflects reality rather than showing a dead source.
        await dropTrack(kind);

        if (recoveryError instanceof MediaAccessError) setError(recoveryError);
      }

      await refreshDeviceList();
    },
    [dropTrack, dropScreenAudio, openCamera, openMicrophone, refreshDeviceList],
  );

  /**
   * `ended` fires on the track itself, which makes it both the earliest and the most reliable
   * signal that a source has gone: a USB camera unplugged, an interface pulled, or the browser's
   * own "Stop sharing" bar pressed. Device-list polling notices none of those as promptly.
   */
  useEffect(() => {
    if (!videoTrack) return;

    const onEnded = (): void => void recoverFrom("video");
    videoTrack.addEventListener("ended", onEnded);
    return () => videoTrack.removeEventListener("ended", onEnded);
  }, [videoTrack, recoverFrom]);

  /**
   * Watches the microphone rather than what is being published.
   *
   * While mixing, the published track is a Web Audio destination, and that never fires `ended` —
   * it has no device behind it to lose. Watching it would mean a microphone could be unplugged
   * mid-broadcast and nothing would notice.
   */
  useEffect(() => {
    if (!microphoneTrack) return;

    const onEnded = (): void => void recoverFrom("audio");
    microphoneTrack.addEventListener("ended", onEnded);
    return () => microphoneTrack.removeEventListener("ended", onEnded);
  }, [microphoneTrack, recoverFrom]);

  /**
   * The sharer can revoke a share's audio on its own, from the browser's sharing bar, without
   * ending the share. There is nothing to fall back to and nothing to report: the broadcast simply
   * carries on with the voice.
   */
  useEffect(() => {
    if (!screenAudioTrack) return;

    const onEnded = (): void => void dropScreenAudio();
    screenAudioTrack.addEventListener("ended", onEnded);
    return () => screenAudioTrack.removeEventListener("ended", onEnded);
  }, [screenAudioTrack, dropScreenAudio]);

  /** Opens one device, reporting what happened rather than throwing. */
  const tryOpen = useCallback(
    async (open: () => Promise<void>, wanted: boolean): Promise<CaptureOutcome> => {
      if (!wanted) return { status: "skipped" };

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
    },
    [],
  );

  /**
   * Retries a device that the machine says it has but that would not open.
   *
   * The commonest cause is another page still letting go of it — a reloaded studio racing
   * its own previous instance for the microphone. Treating that as "no microphone" would
   * make a reload silently drop the sound for the rest of the broadcast.
   *
   * A refused permission is never retried: the answer will not change, and asking again is
   * a second prompt for somebody who already said no.
   */
  const retryIfPresent = useCallback(
    async (outcome: CaptureOutcome, open: () => Promise<void>, present: number | null) => {
      if (outcome.status !== "failed" || outcome.error.reason === "permission-denied") {
        return outcome;
      }

      if ((present ?? 0) === 0) {
        return outcome; // The machine really does not have one.
      }

      return tryOpen(open, true);
    },
    [tryOpen],
  );

  const requestAccess = useCallback(
    async (want: CaptureRequest = { camera: true, microphone: true }) => {
      const support = detectMediaSupport();
      if (!support.supported) {
        setPermission("unsupported");
        setError(support.error ?? null);
        return;
      }

      setPermission("requesting");
      setError(null);
      setNotice(null);

      // Each device is opened on its own terms. Chaining them — the obvious thing — means a machine
      // with no webcam never reaches the microphone, and cannot enter the studio at all.
      const camera = await tryOpen(() => openCamera(), want.camera);
      const microphone = await tryOpen(() => openMicrophone(), want.microphone);

      // Device labels are only revealed once something has been granted, so list them now — and
      // the counts are what decide whether a failure above is worth retrying.
      await refreshDeviceList();

      const attempt = {
        camera: await retryIfPresent(camera, () => openCamera(), cameraCountRef.current),
        microphone: await retryIfPresent(microphone, () => openMicrophone(), microphoneCountRef.current),
      };

      const outcome = summarizeCaptureAttempt(attempt);

      setError(outcome.error);
      setNotice(outcome.notice);

      if (!outcome.granted) {
        setPermission(outcome.error?.reason === "permission-denied" ? "denied" : "idle");
        return;
      }

      setPermission("granted");
    },
    [openCamera, openMicrophone, refreshDeviceList, retryIfPresent, tryOpen],
  );

  /** Runs a device change with the busy flag and error reporting every path needs. */
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
              switchError instanceof Error ? switchError.message : "We could not change device.",
              "Try selecting the device again.",
            ),
      );
    } finally {
      setSwitching(false);
    }
  }, []);

  const selectCamera = useCallback(
    (deviceId: string) => runSwitch(() => openCamera(deviceId)),
    [runSwitch, openCamera],
  );

  const selectMicrophone = useCallback(
    (deviceId: string) => runSwitch(() => openMicrophone(deviceId)),
    [runSwitch, openMicrophone],
  );

  /**
   * Re-opens the camera at a new resolution. There is no way to re-negotiate one in place.
   *
   * These rungs are camera resolutions, and a shared screen has none — it arrives at whatever
   * the display is. Re-opening the camera here would yank a live screen share off air and
   * replace it with the webcam, which is the opposite of what changing a quality setting
   * should ever do.
   */
  const setQuality = useCallback(
    (next: CaptureQuality) =>
      runSwitch(async () => {
        qualityChosenRef.current = true;
        qualityRef.current = next;
        setQualityState(next);
        if (videoSourceRef.current === "screen") {
          return; // Remembered for when the camera comes back.
        }

        if (videoTrackRef.current) await openCamera(cameraIdRef.current ?? undefined);
      }),
    [runSwitch, openCamera],
  );

  /**
   * Puts a screen or window on air in place of the camera.
   *
   * Goes through the same track swap as a device change, so it lands mid-broadcast without a
   * dropout. The camera is released while sharing — its indicator light goes out, which is what
   * anyone who has ever presented from a laptop expects to happen.
   */
  /**
   * Puts a screen or window on air in place of the camera.
   *
   * Goes through the same track swap as a device change, so it lands mid-broadcast without a
   * dropout. The camera is released while sharing — its indicator light goes out, which is what
   * anyone who has ever presented from a laptop expects to happen.
   *
   * It is also a way *into* the studio. On a machine with no webcam this is the whole broadcast, so
   * it grants access on success rather than assuming access was granted earlier.
   */
  const shareScreen = useCallback(
    (mode?: ScreenCaptureMode) =>
      runSwitch(async () => {
        const wanted = mode ?? screenModeRef.current;

        // An explicit rate chosen earlier wins; otherwise the mode's own default applies, so
        // picking "games" gets 60 without a second decision.
        const rate = mode === undefined ? screenFrameRateRef.current : SCREEN_PROFILES[mode].frameRate;
        const capture = await requestScreenCapture(wanted, rate);

        // Capture and grading are decided together, inside requestScreenCapture, so they cannot
        // disagree about what this share is.
        screenModeRef.current = wanted;
        setScreenModeState(wanted);
        screenFrameRateRef.current = rate;
        setScreenFrameRateState(rate);

        videoSourceRef.current = "screen";
        setVideoSource("screen");

        try {
          await adoptTrack(capture.video);
        } catch (shareError) {
          // The picture never went on air, so its sound must not go out either — and the capture
          // has to be released, or the browser keeps showing this tab as sharing.
          capture.audio?.stop();
          throw shareError;
        }

        // The share's sound, when the sharer ticked the box. Replaces whatever a previous share
        // left behind, so re-sharing without audio does not keep broadcasting the old one.
        const previousScreenAudio = screenAudioTrackRef.current;
        screenAudioTrackRef.current = capture.audio;
        setScreenAudioTrack(capture.audio);

        try {
          await reconcileAudioRef.current();
        } finally {
          previousScreenAudio?.stop();
        }

        if (capture.audio) {
          setNotice(null);
        } else {
          setNotice(
            "Sharing without the screen's sound. To include it, share again and tick “Also share tab audio” or “Share system audio”.",
          );
        }

        setPermission("granted");
        await refreshDeviceList();
      }),
    [runSwitch, adoptTrack, refreshDeviceList],
  );

  /**
   * Applies content hint and frame rate to a live screen share.
   *
   * Frame rate is a capture constraint, so the track is re-constrained in place rather than
   * re-captured — Chrome honours `applyConstraints` on a display track, and re-capturing would put
   * the screen picker back in front of somebody mid-broadcast.
   *
   * When the browser will not re-constrain, the setting is still remembered and the operator is
   * told that sharing again applies it. Silently keeping the old rate under a new label would be
   * worse than saying so.
   */
  const applyScreenSettings = useCallback(async (contentHint: string, rate: ScreenFrameRate) => {
    const track = videoTrackRef.current;
    if (!track || videoSourceRef.current !== "screen") {
      return; // Remembered for the next share.
    }

    track.contentHint = contentHint;

    try {
      await track.applyConstraints({ frameRate: { ideal: rate } });
    } catch {
      setNotice("Press Share screen again to capture at the new frame rate.");
    }

    // The sender's degradation preference follows the content hint, so it has to be re-read.
    await handlersRef.current.onVideoTrackChanged?.(track);
  }, []);

  /**
   * Changes what a shared screen is treated as, which also moves it to that content's frame rate.
   */
  const setScreenMode = useCallback(
    (mode: ScreenCaptureMode) =>
      runSwitch(async () => {
        screenModeRef.current = mode;
        setScreenModeState(mode);

        // Choosing what the screen carries also moves the frame rate to that content's default,
        // which is what somebody selecting "games" means. An explicit rate chosen afterwards wins.
        const rate = SCREEN_PROFILES[mode].frameRate;
        screenFrameRateRef.current = rate;
        setScreenFrameRateState(rate);

        await applyScreenSettings(SCREEN_PROFILES[mode].contentHint, rate);
      }),
    [runSwitch, applyScreenSettings],
  );

  const setScreenFrameRate = useCallback(
    (rate: ScreenFrameRate) =>
      runSwitch(async () => {
        screenRateChosenRef.current = true;
        screenFrameRateRef.current = rate;
        setScreenFrameRateState(rate);

        await applyScreenSettings(SCREEN_PROFILES[screenModeRef.current].contentHint, rate);
      }),
    [runSwitch, applyScreenSettings],
  );

  /**
   * Ends a screen share.
   *
   * Returns to the camera when there is one. When there is not — the case this whole path exists
   * for — the video track is dropped and the broadcast carries on with sound alone, rather than
   * failing because the thing it wanted to fall back to was never there.
   */
  const stopSharing = useCallback(
    () =>
      runSwitch(async () => {
        await dropScreenAudio();

        if (cameraCountRef.current === 0) {
          await dropTrack("video");
          return;
        }

        await openCamera(cameraIdRef.current ?? undefined);
      }),
    [runSwitch, openCamera, dropTrack, dropScreenAudio],
  );

  /**
   * Changes how the browser treats the microphone.
   *
   * Re-opens the device, because echo cancellation and the rest are capture constraints applied
   * when the track is created — `applyConstraints` on a live audio track does not re-engage the
   * processing chain. The re-open is a brief gap in sound for anyone watching, which is why this is
   * a deliberate choice rather than something adjusted while hunting for the right setting.
   */
  const setMicrophoneMode = useCallback(
    (mode: MicrophoneMode) =>
      runSwitch(async () => {
        microphoneModeRef.current = mode;
        setMicrophoneModeState(mode);
        if (microphoneTrackRef.current) {
          await openMicrophone(microphoneTrackRef.current.getSettings?.().deviceId ?? undefined);
        }
      }),
    [runSwitch, openMicrophone],
  );

  // Muting disables the track instead of stopping it: the publisher's transport stays up, so a
  // mute never looks like a dropped connection to the server.
  const toggleCamera = useCallback(() => {
    const track = videoTrackRef.current;
    if (!track) return;
    track.enabled = !track.enabled;
    setCameraEnabled(track.enabled);
  }, []);

  // Mutes the microphone, not the mix: a broadcaster stepping away should silence themselves and
  // leave the game playing. A disabled track feeds the mixer silence, so the mix keeps running and
  // the published track is never swapped.
  const toggleMicrophone = useCallback(() => {
    const track = microphoneTrackRef.current;
    if (!track) return;
    track.enabled = !track.enabled;
    setMicrophoneEnabled(track.enabled);
  }, []);

  /**
   * Balances the two sources against each other.
   *
   * Applied through the mixer, which adjusts a gain node in a graph whose output track stays the
   * same object — so moving a slider mid-broadcast changes the balance without re-publishing
   * anything. With only one source open there is no graph, and the levels are simply remembered
   * for when a second one arrives.
   */
  const setAudioLevels = useCallback((levels: CaptureAudioLevels) => {
    audioLevelsRef.current = levels;
    setAudioLevelsState(levels);
    void reconcileAudioRef.current();
  }, []);

  const dismissNotice = useCallback(() => setNotice(null), []);

  const release = useCallback(() => {
    // The mixer holds an AudioContext, which the browser keeps alive on its own. Left behind, it
    // is a live audio graph attached to a studio that has been torn down.
    void captureAudioRef.current.disposeAsync();

    // The screen's audio is not in the preview stream while mixing — the mix output is — so
    // `stopStream` would leave the share running and its indicator lit.
    screenAudioTrackRef.current?.stop();
    microphoneTrackRef.current?.stop();
    stopStream(streamRef.current);

    streamRef.current = null;
    videoTrackRef.current = null;
    microphoneTrackRef.current = null;
    screenAudioTrackRef.current = null;
    publishedAudioRef.current = null;

    setVideoTrack(null);
    setAudioTrack(null);
    setMicrophoneTrack(null);
    setScreenAudioTrack(null);
    setStream(null);
    setPermission("idle");
  }, []);

  /**
   * Watches for hardware appearing or disappearing.
   *
   * A newly connected camera is announced but never selected. Switching the shot because someone
   * plugged in a dock would be a far worse failure than making them click once.
   */
  useEffect(() => {
    if (typeof navigator === "undefined" || !navigator.mediaDevices?.addEventListener) return;

    const handler = (): void => {
      void (async () => {
        const videoDevices = await refreshDeviceList();
        const previousCount = cameraCountRef.current;
        cameraCountRef.current = videoDevices.length;

        const added = videoDevices[videoDevices.length - 1];
        if (previousCount !== null && videoDevices.length > previousCount && added) {
          setNotice(`New camera available: ${added.label}. Select it to switch.`);
        }
      })();
    };

    navigator.mediaDevices.addEventListener("devicechange", handler);
    return () => navigator.mediaDevices.removeEventListener("devicechange", handler);
  }, [refreshDeviceList]);

  // Release hardware when the studio unmounts, so the camera and sharing indicators go out. The
  // capture tracks are stopped by name because while mixing the preview holds the mix, not them.
  useEffect(
    () => () => {
      const captureAudio = captureAudioRef.current;
      void captureAudio.disposeAsync();
      screenAudioTrackRef.current?.stop();
      microphoneTrackRef.current?.stop();
      stopStream(streamRef.current);
      streamRef.current = null;
    },
    [],
  );

  return {
    permission,
    stream,
    videoTrack,
    audioTrack,
    microphoneTrack,
    screenAudioTrack,
    audioLevels,
    setAudioLevels,
    cameras,
    microphones,
    cameraAvailable: cameras.length > 0,
    microphoneAvailable: microphones.length > 0,
    hasCapture: videoTrack !== null || audioTrack !== null,
    selectedCameraId,
    selectedMicrophoneId,
    cameraEnabled,
    microphoneEnabled,
    quality,
    videoFormat,
    videoSource,
    screenMode,
    screenFrameRate,
    microphoneMode,
    switching,
    notice,
    error,
    requestAccess,
    selectCamera,
    selectMicrophone,
    setQuality,
    setMicrophoneMode,
    shareScreen,
    setScreenMode,
    setScreenFrameRate,
    stopSharing,
    toggleCamera,
    toggleMicrophone,
    dismissNotice,
    release,
  };
}
