/**
 * Camera and microphone access.
 *
 * Browser media errors are notoriously opaque (`NotAllowedError`, `OverconstrainedError`), so every
 * failure is translated into a cause the UI can act on and a sentence a person can act on
 * (docs/01-product-requirements.md: "Errors must explain what happened and what the user can do").
 *
 * Tracks are acquired one at a time rather than as a camera+microphone pair. That is what lets the
 * studio swap a camera mid-broadcast without touching the microphone: a paired `getUserMedia` call
 * re-opens both devices, and re-opening a live microphone is an audible glitch.
 */

export type DeviceKind = "camera" | "microphone";

export type MediaErrorReason =
  | "permission-denied"
  | "no-device"
  | "device-in-use"
  | "device-disconnected"
  | "unsupported-browser"
  | "insecure-context"
  | "constraints-unsatisfiable"
  | "unknown";

/**
 * How a device is attached, inferred from its label.
 *
 * This is a heuristic over browser labelling conventions, not a fact the platform reports — no web
 * API exposes the transport. It earns its place because the distinction is the one users actually
 * reason about ("use the camera I just plugged in"), and because being wrong is cosmetic: the
 * grouping changes, the device still works. Anything unrecognised is `unknown`, never guessed.
 */
export type DeviceConnection = "built-in" | "wired" | "wireless" | "virtual" | "unknown";

export interface MediaDeviceOption {
  deviceId: string;
  label: string;
  kind: DeviceKind;
  connection: DeviceConnection;
  /** Shared by the camera and microphone of one physical device, so a headset pairs with itself. */
  groupId: string;
}

export class MediaAccessError extends Error {
  constructor(
    readonly reason: MediaErrorReason,
    readonly userMessage: string,
    readonly recovery: string,
    readonly technicalDetail?: string,
  ) {
    super(userMessage);
    this.name = "MediaAccessError";
  }
}

/** Feature detection: never assume a browser exposes the capture APIs (docs/04-native-broadcasting.md). */
export function detectMediaSupport(): { supported: boolean; error?: MediaAccessError } {
  if (typeof navigator === "undefined" || !navigator.mediaDevices?.getUserMedia) {
    // getUserMedia is only exposed in secure contexts, so this is usually plain HTTP.
    if (typeof window !== "undefined" && !window.isSecureContext) {
      return {
        supported: false,
        error: new MediaAccessError(
          "insecure-context",
          "Your browser will not share the camera on an insecure connection.",
          "Open this page over HTTPS, or use http://localhost during development.",
        ),
      };
    }

    return {
      supported: false,
      error: new MediaAccessError(
        "unsupported-browser",
        "This browser cannot access your camera and microphone.",
        "Use a recent version of Chrome, Edge, Firefox, or Safari.",
      ),
    };
  }

  if (typeof RTCPeerConnection === "undefined") {
    return {
      supported: false,
      error: new MediaAccessError(
        "unsupported-browser",
        "This browser cannot broadcast live video.",
        "Use a recent version of Chrome, Edge, Firefox, or Safari.",
      ),
    };
  }

  return { supported: true };
}

/** Translates a `getUserMedia` rejection into something the studio can display and act on. */
export function toMediaAccessError(error: unknown, kind: DeviceKind): MediaAccessError {
  const label = kind === "camera" ? "camera" : "microphone";
  const name = error instanceof Error ? error.name : "";
  const detail = error instanceof Error ? error.message : String(error);

  switch (name) {
    case "NotAllowedError":
    case "SecurityError":
      return new MediaAccessError(
        "permission-denied",
        `Access to your ${label} was blocked.`,
        `Allow ${label} access for this site in your browser's address bar, then try again.`,
        detail,
      );

    case "NotFoundError":
    case "DevicesNotFoundError":
      return new MediaAccessError(
        "no-device",
        `No ${label} was found.`,
        `Connect a ${label} and select it from the list.`,
        detail,
      );

    case "NotReadableError":
    case "AbortError":
      return new MediaAccessError(
        "device-in-use",
        `Your ${label} is being used by another application.`,
        `Close any other app using the ${label} — video calls are the usual culprit — and try again.`,
        detail,
      );

    case "OverconstrainedError":
      return new MediaAccessError(
        "constraints-unsatisfiable",
        `Your ${label} does not support the requested quality.`,
        "Pick a different device or a lower quality setting.",
        detail,
      );

    default:
      return new MediaAccessError(
        "unknown",
        `We could not start your ${label}.`,
        "Check that the device is connected and try again.",
        detail,
      );
  }
}

// Label conventions, most specific first. A phone-as-webcam bridge (Camo, EpocCam, DroidCam) is
// software presenting a phone over either USB or Wi-Fi, so it matches `virtual` before anything
// else — calling it "wired" would be a guess about which mode the user chose.
const VIRTUAL_PATTERNS =
  /\b(obs|virtual|ndi|manycam|xsplit|snap camera|droidcam|epoccam|iriun|camo|vcam|screen capture)\b/i;
const WIRELESS_PATTERNS = /\b(bluetooth|airpods|wireless|wi-?fi|airplay|continuity)\b/i;
const BUILT_IN_PATTERNS = /\b(built-?in|internal|integrated|facetime|default|macbook)\b/i;
// Chrome appends a USB vendor:product pair to UVC device labels — "Cam Link 4K (0fd9:0066)".
const USB_ID_PATTERN = /\([0-9a-f]{4}:[0-9a-f]{4}\)/i;
const WIRED_PATTERNS = /\b(usb|hdmi|capture|cam ?link|elgato|avermedia|magewell|webcam|dock)\b/i;

/**
 * Infers how a device is attached from its label.
 *
 * Order matters: a label can satisfy several patterns ("USB Bluetooth Headset"), and the more
 * specific claim should win rather than whichever regex happens to be tested first.
 */
export function classifyConnection(label: string): DeviceConnection {
  if (!label) return "unknown";
  if (VIRTUAL_PATTERNS.test(label)) return "virtual";
  if (WIRELESS_PATTERNS.test(label)) return "wireless";
  if (BUILT_IN_PATTERNS.test(label)) return "built-in";
  if (USB_ID_PATTERN.test(label) || WIRED_PATTERNS.test(label)) return "wired";
  return "unknown";
}

/** Short human label for a connection kind, used to group devices in the picker. */
export function connectionLabel(connection: DeviceConnection): string {
  switch (connection) {
    case "built-in":
      return "Built-in";
    case "wired":
      return "Connected by cable";
    case "wireless":
      return "Wireless";
    case "virtual":
      return "Virtual";
    default:
      return "Other";
  }
}

/**
 * Lists available cameras and microphones.
 *
 * Labels are only populated once permission has been granted, so a device with an empty label is
 * given a stable positional name rather than being hidden.
 */
export async function enumerateDevices(): Promise<{
  cameras: MediaDeviceOption[];
  microphones: MediaDeviceOption[];
}> {
  if (typeof navigator === "undefined" || !navigator.mediaDevices?.enumerateDevices) {
    return { cameras: [], microphones: [] };
  }

  const devices = await navigator.mediaDevices.enumerateDevices();

  const map = (kind: MediaDeviceKind, deviceKind: DeviceKind): MediaDeviceOption[] =>
    devices
      .filter((device) => device.kind === kind)
      .map((device, index) => ({
        deviceId: device.deviceId,
        label: device.label || `${deviceKind === "camera" ? "Camera" : "Microphone"} ${index + 1}`,
        kind: deviceKind,
        connection: classifyConnection(device.label),
        groupId: device.groupId ?? "",
      }));

  return {
    cameras: map("videoinput", "camera"),
    microphones: map("audioinput", "microphone"),
  };
}

/**
 * Capture quality. `auto` lets the browser choose, which is right for an unknown webcam; the fixed
 * rungs exist because capture cards and external cameras regularly default far below what they can
 * deliver unless a resolution is asked for explicitly.
 */
export type CaptureQuality = "auto" | "1080p" | "720p" | "540p";

interface VideoProfile {
  width: number;
  height: number;
  frameRate: number;
}

/**
 * Camera rungs.
 *
 * The frame rate is `ideal`, so a camera that only manages 30 gives 30 and one that manages 60
 * gives 60 — asking for 60 costs nothing on hardware that cannot do it. The lowest rung stays at 30
 * deliberately: it exists for a connection that is struggling, and doubling its frame rate would
 * work against the reason somebody chose it.
 */
const VIDEO_PROFILES: Record<Exclude<CaptureQuality, "auto">, VideoProfile> = {
  "1080p": { width: 1920, height: 1080, frameRate: 60 },
  "720p": { width: 1280, height: 720, frameRate: 60 },
  "540p": { width: 960, height: 540, frameRate: 30 },
};

/** The default rung. 720p30 publishes reliably on domestic upload and looks right on every player. */
export const DEFAULT_CAPTURE_QUALITY: CaptureQuality = "720p";

/**
 * Builds video constraints for a quality rung.
 *
 * Every dimension is `ideal`, never `exact`: an exact constraint the hardware cannot meet fails the
 * whole request, and a camera that tops out at 720p should give us 720p rather than an error. That
 * keeps `OverconstrainedError` genuinely exceptional rather than routine.
 */
export function videoConstraints(
  quality: CaptureQuality = DEFAULT_CAPTURE_QUALITY,
  deviceId?: string,
  /**
   * Overrides the rung's frame rate.
   *
   * Exists for the phone, which asks for 30 at every rung: capturing 60 on a phone doubles the
   * bitrate the uplink has to carry and the heat the encoder has to make, for frames that a
   * handheld broadcast never needed (`@/lib/media/mobile`). Left undefined, the rung decides, and
   * nothing about the studio changes.
   */
  frameRate?: number,
): MediaTrackConstraints {
  const base: MediaTrackConstraints = deviceId ? { deviceId: { exact: deviceId } } : {};

  if (quality === "auto") {
    return frameRate ? { ...base, frameRate: { ideal: frameRate } } : base;
  }

  const profile = VIDEO_PROFILES[quality];
  return {
    ...base,
    width: { ideal: profile.width },
    height: { ideal: profile.height },
    frameRate: { ideal: frameRate ?? profile.frameRate },
  };
}

/**
 * What the microphone is carrying, which decides how the browser treats it.
 *
 * A choice between two intents rather than an effect to switch on, because the two are genuinely
 * opposed and there is no setting that serves both: what rescues a voice in a live room destroys
 * a piece of music.
 */
export type MicrophoneMode = "voice" | "music";

export const DEFAULT_MICROPHONE_MODE: MicrophoneMode = "voice";

/**
 * Audio constraints.
 *
 * `voice` turns on the browser's processing — echo cancellation, noise suppression and automatic
 * gain control. It is the default because the overwhelmingly common broadcast is a person talking,
 * usually into a laptop microphone in an untreated room, where all three are what stand between
 * them and sounding like a bad phone call. Somebody broadcasting music knows they are, and will
 * come looking for the setting; somebody talking does not know their room is the problem.
 *
 * `music` turns all three off. Noise suppression mangles anything sustained, and gain control
 * pumps the level of a properly set-up microphone. Nothing is removed and nothing is levelled.
 *
 * 48 kHz stereo is asked for explicitly in both modes, rather than left to the browser. It is what
 * Opus carries natively and what every platform ingests, so nothing downstream resamples.
 *
 * A note on a claim that used to live here: this comment previously said voice processing "forces
 * mono at 16 kHz", and that was the stated reason for leaving it off. Measured in the browser the
 * studio actually runs in, a processed track reports 48 kHz and two channels — so the reason was
 * not true, and the default was wrong because of it. See ADR 0017 §10.
 */
export function audioConstraints(mode: MicrophoneMode, deviceId?: string): MediaTrackConstraints {
  const processing = mode === "voice";

  return {
    ...(deviceId ? { deviceId: { exact: deviceId } } : {}),
    echoCancellation: processing,
    noiseSuppression: processing,
    autoGainControl: processing,
    sampleRate: { ideal: 48000 },
    channelCount: { ideal: 2 },
  };
}

export interface CameraRequest {
  deviceId?: string;
  quality?: CaptureQuality;
  /** Prefers the front or rear camera. Ignored on hardware that reports no facing mode. */
  facingMode?: "user" | "environment";
  /** Overrides the rung's frame rate. See {@link videoConstraints}. */
  frameRate?: number;
  /**
   * How the encoder should grade this camera under load, set on the track before it is published.
   *
   * `motion` is right for a handheld phone and is what the mobile broadcaster asks for: shed
   * resolution, keep the frame rate. The publisher reads the hint back off the track when it sets
   * the sender's degradation preference, so this is where the decision belongs.
   */
  contentHint?: "motion" | "detail";
}

/**
 * Opens a single camera track.
 *
 * Requesting a named device that has gone away falls back to any camera, so unplugging a webcam
 * degrades to "use the built-in one" instead of failing outright.
 */
export async function requestCameraTrack(request: CameraRequest = {}): Promise<MediaStreamTrack> {
  const quality = request.quality ?? DEFAULT_CAPTURE_QUALITY;

  const constraints = videoConstraints(quality, request.deviceId, request.frameRate);
  if (request.facingMode && !request.deviceId) {
    // `ideal`, because a laptop has no rear camera and should still open the one it has.
    constraints.facingMode = { ideal: request.facingMode };
  }

  try {
    return hinted(
      firstTrack(await getUserMedia({ video: constraints, audio: false }), "camera"),
      request.contentHint,
    );
  } catch (error) {
    const name = error instanceof Error ? error.name : "";

    if ((name === "OverconstrainedError" || name === "NotFoundError") && request.deviceId) {
      try {
        return hinted(
          firstTrack(
            await getUserMedia({ video: videoConstraints(quality, undefined, request.frameRate), audio: false }),
            "camera",
          ),
          request.contentHint,
        );
      } catch (fallbackError) {
        throw await describeFailure(fallbackError, "camera");
      }
    }

    throw await describeFailure(error, "camera");
  }
}

/**
 * Labels a track with what it is carrying, where the caller said.
 *
 * Best effort: `contentHint` is a hint in the specification's own words, a browser that ignores it
 * simply grades the track the way it would have anyway, and no broadcast should fail over one.
 */
function hinted(track: MediaStreamTrack, contentHint?: string): MediaStreamTrack {
  if (contentHint) {
    try {
      track.contentHint = contentHint;
    } catch {
      // Unsupported on this browser. The encoder picks its own grading.
    }
  }

  return track;
}

export interface MicrophoneRequest {
  deviceId?: string;
  /** What this microphone is carrying. Defaults to speech — see `audioConstraints`. */
  mode?: MicrophoneMode;
}

/** Opens a single microphone track, with the same disappeared-device fallback as the camera. */
export async function requestMicrophoneTrack(
  request: MicrophoneRequest = {},
): Promise<MediaStreamTrack> {
  const mode = request.mode ?? DEFAULT_MICROPHONE_MODE;

  try {
    return firstTrack(
      await getUserMedia({
        video: false,
        audio: audioConstraints(mode, request.deviceId),
      }),
      "microphone",
    );
  } catch (error) {
    const name = error instanceof Error ? error.name : "";

    if ((name === "OverconstrainedError" || name === "NotFoundError") && request.deviceId) {
      try {
        return firstTrack(
          await getUserMedia({ video: false, audio: audioConstraints(mode) }),
          "microphone",
        );
      } catch (fallbackError) {
        throw await describeFailure(fallbackError, "microphone");
      }
    }

    throw await describeFailure(error, "microphone");
  }
}

/**
 * What a shared screen is carrying, which decides how it is captured and graded.
 *
 * The two are genuinely opposed under load. A slide that blurs is unreadable, so a
 * presentation keeps its resolution and sheds frames. A game that judders is unwatchable, so
 * gameplay keeps its frame rate and sheds resolution. Guessing wrong is worse than either.
 */
export type ScreenCaptureMode = "presentation" | "gameplay";

export const DEFAULT_SCREEN_CAPTURE_MODE: ScreenCaptureMode = "presentation";

/**
 * Capture frame rates offered for a shared screen.
 *
 * 120 is included because a display that runs at it should be able to send it, but be clear about
 * where it survives: browsers encode WebRTC video at up to 60, and every RTMP platform ingests at
 * up to 60. Above that the extra frames are captured and then dropped, so 120 buys smoothness in
 * the local preview and nothing downstream.
 */
export const SCREEN_FRAME_RATES = [30, 60, 120] as const;

export type ScreenFrameRate = (typeof SCREEN_FRAME_RATES)[number];

/** Highest rate that survives encoding and reaches a platform. Above this, frames are dropped. */
export const MAX_DELIVERED_FRAME_RATE = 60;

interface ScreenProfile {
  frameRate: ScreenFrameRate;
  /** Read by the publisher to set the sender's degradation preference. */
  contentHint: string;
}

export const SCREEN_PROFILES: Record<ScreenCaptureMode, ScreenProfile> = {
  // 30 is plenty for slides and code, and every frame is worth keeping sharp.
  presentation: { frameRate: 30, contentHint: "detail" },

  // 60 for anything that moves. It costs roughly twice the bitrate of 30, which is why it is
  // a choice rather than the default.
  gameplay: { frameRate: 60, contentHint: "motion" },
};

/**
 * Captures a screen or window. Separate from the camera path: it has its own permission
 * prompt, and its own idea of what good looks like.
 *
 * Resolution is deliberately unconstrained — a shared screen should arrive at whatever the
 * display actually is, and the encoder scales it down under load according to the mode.
 */
/**
 * A shared screen, and its sound if the sharer chose to include it.
 *
 * `audio` is null far more often than not, and that is not a failure: the browser only offers the
 * checkbox for a whole screen or a tab, only Chromium-based browsers offer it at all, and the person
 * sharing has to tick it. Every caller has to work without it.
 */
export interface ScreenCapture {
  video: MediaStreamTrack;
  audio: MediaStreamTrack | null;
}

export async function requestScreenCapture(
  mode: ScreenCaptureMode = DEFAULT_SCREEN_CAPTURE_MODE,
  frameRate?: ScreenFrameRate,
): Promise<ScreenCapture> {
  if (typeof navigator === "undefined" || !navigator.mediaDevices?.getDisplayMedia) {
    throw new MediaAccessError(
      "unsupported-browser",
      "This browser cannot share your screen.",
      "Use a recent desktop version of Chrome, Edge, or Firefox.",
    );
  }

  const profile = SCREEN_PROFILES[mode];
  const wanted = frameRate ?? profile.frameRate;

  try {
    const stream = await navigator.mediaDevices.getDisplayMedia({
      // `ideal`, never `exact`: a display that cannot do the rate asked for should still share at
      // whatever it manages rather than fail the request outright.
      video: { frameRate: { ideal: wanted } },

      // Asking is what puts "Share system audio" in front of the person sharing. Whether they tick
      // it is theirs to decide, and most of the time they will not — so the track is optional
      // everywhere downstream rather than assumed.
      audio: true,
    });

    // Deliberately not `firstTrack`: that helper stops everything except the one kind it was asked
    // for, which here would stop the screen audio a moment after asking for it.
    const [video, ...surplusVideo] = stream.getVideoTracks();
    const [audio, ...surplusAudio] = stream.getAudioTracks();

    for (const track of [...surplusVideo, ...surplusAudio]) {
      track.stop();
    }

    if (!video) {
      throw new MediaAccessError(
        "no-device",
        "That share produced no picture.",
        "Press Share screen again and choose a screen, window, or tab.",
      );
    }

    // Set here rather than by the caller so the capture and the grading cannot disagree: the
    // publisher reads this hint to decide what to shed when the connection tightens.
    video.contentHint = profile.contentHint;

    // Screen audio is a game, a video, music — never speech into a headset. Telling the browser so
    // keeps any voice processing away from it.
    if (audio) audio.contentHint = "music";

    return { video, audio: audio ?? null };
  } catch (error) {
    const name = error instanceof Error ? error.name : "";

    // Dismissing the picker reports `NotAllowedError`, the same code as a blocked camera. Routing
    // it through the camera mapper would answer "allow camera access in your address bar" to
    // someone who simply pressed Cancel on a screen-sharing dialog.
    if (name === "NotAllowedError") {
      return Promise.reject(
        new MediaAccessError(
          "permission-denied",
          "Screen sharing was cancelled.",
          "Press Share screen again and choose a screen, window, or tab.",
          error instanceof Error ? error.message : undefined,
        ),
      );
    }

    throw toMediaAccessError(error, "camera");
  }
}

function getUserMedia(constraints: MediaStreamConstraints): Promise<MediaStream> {
  const support = detectMediaSupport();
  if (!support.supported) {
    throw support.error ?? new MediaAccessError("unsupported-browser", "Media capture is unavailable.", "");
  }

  return navigator.mediaDevices.getUserMedia(constraints);
}

/**
 * Takes the one track a single-kind request asked for, stopping any others.
 *
 * A browser that hands back more than was requested would otherwise leave hardware open with its
 * indicator light on and no reference held to close it.
 */
function firstTrack(stream: MediaStream, kind: DeviceKind): MediaStreamTrack {
  const wanted = kind === "camera" ? stream.getVideoTracks() : stream.getAudioTracks();
  const [track, ...extra] = wanted;

  for (const other of stream.getTracks()) {
    if (other !== track) other.stop();
  }
  for (const duplicate of extra) duplicate.stop();

  if (!track) {
    throw new MediaAccessError(
      "no-device",
      `No ${kind} was found.`,
      `Connect a ${kind} and select it from the list.`,
    );
  }

  return track;
}

/**
 * Turns a capture failure into the most accurate message available.
 *
 * Browsers report a dismissed permission prompt as `NotAllowedError` whether or not the hardware
 * even exists, so "allow access in your address bar" is misleading advice on a machine with no
 * camera. Checking what devices are present first distinguishes the two.
 */
async function describeFailure(error: unknown, kind: DeviceKind): Promise<MediaAccessError> {
  const mapped = toMediaAccessError(error, kind);

  if (mapped.reason !== "permission-denied") {
    return mapped;
  }

  try {
    const { cameras, microphones } = await enumerateDevices();
    const present = kind === "camera" ? cameras : microphones;

    if (present.length === 0) {
      return new MediaAccessError(
        "no-device",
        `No ${kind} is connected to this computer.`,
        `Connect a ${kind} and try again. If you are testing without hardware, see the setup guide for how to use a virtual camera.`,
        mapped.technicalDetail,
      );
    }
  } catch {
    // Enumeration failed; fall through to the original, still-correct message.
  }

  return mapped;
}

export interface TrackFormat {
  width: number;
  height: number;
  frameRate: number;
}

/**
 * What a video track is actually producing, which is regularly not what was asked for. A capture
 * card negotiating 1080p when 720p was requested is worth showing the operator.
 */
export function describeTrackFormat(track: MediaStreamTrack | null | undefined): TrackFormat | null {
  if (!track || track.kind !== "video" || typeof track.getSettings !== "function") return null;

  const settings = track.getSettings();
  if (!settings.width || !settings.height) return null;

  return {
    width: settings.width,
    height: settings.height,
    frameRate: Math.round(settings.frameRate ?? 0),
  };
}

/** Stops every track on a stream. Safe to call more than once. */
export function stopStream(stream: MediaStream | null | undefined): void {
  stream?.getTracks().forEach((track) => track.stop());
}
