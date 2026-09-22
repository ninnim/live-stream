/**
 * Mobile capture policy.
 *
 * Phase 3 asks for a broadcaster a creator can go live from on a phone
 * (implementation/phase-3-mobile-broadcasting.md). A phone is not a small desktop, and the
 * differences that matter are not cosmetic:
 *
 * - **The uplink is the scarce resource.** A laptop broadcasts over a link somebody chose; a phone
 *   broadcasts over whatever cell it happens to be attached to, and that changes while they walk.
 * - **Capture costs battery, and battery is the length of the show.** Asking a phone for 1080p60
 *   when 720p30 is what will survive the uplink spends charge on frames that get thrown away.
 * - **Nobody is watching a health panel.** The person is holding the camera and looking at the
 *   thing they are filming. Anything that needs noticing will not be noticed.
 *
 * Everything here is pure — signals in, decision out — so the policy can be tested without a
 * browser and without a phone. `@/hooks/useMobileCapture` does the acting.
 *
 * ADR 0021 records why this is a separate layer rather than options bolted onto the desktop one.
 */

import type { CaptureQuality } from "@/lib/media/devices";
import type { QualityLimitation, QualityVerdict } from "@/lib/media/quality";

/** Which broadcaster to put in front of somebody. */
export type FormFactor = "desktop" | "mobile";

/** Which way a phone camera points. Mirrors the `facingMode` constraint. */
export type CameraFacing = "user" | "environment";

export interface FormFactorSignals {
  /** `(pointer: coarse)` — the primary pointer is a finger rather than a mouse. */
  coarsePointer: boolean;
  /** iPadOS reports itself as a Mac, and this is the signal that gives it away. */
  maxTouchPoints: number;
  viewportWidth: number;
  userAgent: string;
}

/**
 * Below this width the desktop studio's panel stack stops being usable, whatever the device is.
 *
 * Taken from the studio itself rather than from a device list: its controls sit in a `max-w-5xl`
 * column of cards that assume a mouse and a keyboard, and a tablet in landscape can still work
 * them. A phone cannot.
 */
export const MOBILE_VIEWPORT_WIDTH = 900;

/**
 * Picks the broadcaster this device should get.
 *
 * A mouse decides it outright: a narrow desktop window is somebody who resized a browser, not
 * somebody holding a phone, and moving them to a touch interface because of the window width
 * would be wrong every time. Among touch devices a phone identifies itself — `Mobi` is the token
 * every mobile browser has carried since it was specified — and anything else falls back to the
 * width, which puts a phone-sized tablet on the mobile broadcaster and leaves a landscape iPad on
 * the full studio.
 *
 * Being wrong is recoverable rather than fatal: both screens drive the same session through the
 * same API, and each offers a link to the other.
 */
export function detectFormFactor(signals: FormFactorSignals): FormFactor {
  const touch = signals.coarsePointer || signals.maxTouchPoints > 1;
  if (!touch) return "desktop";

  if (/\bMobi/i.test(signals.userAgent)) return "mobile";

  return signals.viewportWidth < MOBILE_VIEWPORT_WIDTH ? "mobile" : "desktop";
}

/**
 * Capture frame rate on a phone.
 *
 * 30, not the 60 the desktop rungs ask for. A phone that captures at 60 encodes at 60, and the
 * cost lands on the two things a mobile broadcast has least of: the uplink, which needs roughly
 * twice the bitrate for the same picture, and the battery, which is how long the show can run.
 * Nobody watching a phone broadcast has wished it were 60fps; plenty have watched one end early
 * because the phone was hot and flat.
 */
export const MOBILE_FRAME_RATE = 30;

/** Where a phone starts when nothing is known about the network. */
export const DEFAULT_MOBILE_QUALITY: CaptureQuality = "720p";

/**
 * The rungs offered on a phone, best first.
 *
 * 1080p is offered but never chosen automatically — see {@link chooseMobileStartingQuality}. It
 * exists for the case the automatic policy cannot see: a phone on a good Wi-Fi uplink filming
 * something that deserves the detail, held by somebody who knows that is what they are doing.
 */
export const MOBILE_QUALITIES: Exclude<CaptureQuality, "auto">[] = ["1080p", "720p", "540p"];

/** What a browser will say about the connection, where it says anything at all. */
export interface NetworkSignals {
  /** `slow-2g` | `2g` | `3g` | `4g`, or null where the Network Information API is absent. */
  effectiveType: string | null;
  /** Estimated **downlink** in Mbps. Read the caveat in {@link chooseMobileStartingQuality}. */
  downlinkMbps: number | null;
  /** The operating system's data saver. An instruction, not an estimate. */
  saveData: boolean;
}

export interface MobileStartInputs {
  network: NetworkSignals;
  /** Logical CPU cores, or null where the browser will not say. */
  cores: number | null;
}

/**
 * Cores below which a phone should not be asked for 720p.
 *
 * Phones both under-report and over-state this number — the cores are heterogeneous and the
 * browser counts them all — so it is used only as a floor, to catch the genuinely old device.
 */
const CORES_FOR_MOBILE_720P = 4;

/** A downlink estimate below this is the only pre-broadcast hint that the link is thin. */
const THIN_DOWNLINK_MBPS = 2;

/**
 * Where a phone starts.
 *
 * The honest limitation, stated plainly because it shapes the whole design: **nothing here
 * measures the uplink.** `downlink` is a downstream estimate, and a connection that pulls 50 Mbps
 * can comfortably push 1. There is no browser API for the number that actually decides whether a
 * broadcast survives, and there cannot be one before the broadcast starts.
 *
 * So this picks a rung likely to survive rather than the best one that might, and the ladder in
 * `@/lib/media/adaptive` takes over with measurements from the broadcast itself. Starting low and
 * climbing is recoverable within seconds; starting high and stuttering is what the audience
 * remembers.
 *
 * 1080p is deliberately not reachable from here. On a phone it is a manual choice.
 */
export function chooseMobileStartingQuality(inputs: MobileStartInputs): {
  quality: CaptureQuality;
  reason: string;
} {
  const { effectiveType, downlinkMbps, saveData } = inputs.network;

  if (saveData) {
    return {
      quality: "540p",
      reason: "Data Saver is on, so the broadcast starts at 540p to use less of your data.",
    };
  }

  if (effectiveType === "slow-2g" || effectiveType === "2g" || effectiveType === "3g") {
    return {
      quality: "540p",
      reason: `Your connection reports ${effectiveType}, so the broadcast starts at 540p.`,
    };
  }

  const cores = inputs.cores ?? 0;
  if (cores > 0 && cores < CORES_FOR_MOBILE_720P) {
    return {
      quality: "540p",
      reason: "This phone has few processor cores, so the broadcast starts at 540p to stay smooth.",
    };
  }

  // A downlink estimate this low is weak evidence about the uplink, but it is the only evidence
  // available, and it is never wrong in the reassuring direction.
  if (downlinkMbps !== null && downlinkMbps < THIN_DOWNLINK_MBPS) {
    return {
      quality: "540p",
      reason: "Your connection looks slow, so the broadcast starts at 540p.",
    };
  }

  return {
    quality: DEFAULT_MOBILE_QUALITY,
    reason: "Starting at 720p. Quality adjusts itself once your connection has been measured.",
  };
}

/** Reads the Network Information API where there is one. Absent on iOS, which is why it is optional. */
export function readNetworkSignals(connection?: unknown): NetworkSignals {
  const source = (connection ?? null) as {
    effectiveType?: unknown;
    downlink?: unknown;
    saveData?: unknown;
  } | null;

  return {
    effectiveType: typeof source?.effectiveType === "string" ? source.effectiveType : null,
    downlinkMbps: typeof source?.downlink === "number" ? source.downlink : null,
    saveData: source?.saveData === true,
  };
}

/**
 * What to do about a struggling broadcast, phrased for somebody holding a phone.
 *
 * The studio's own advice (`explainQuality` in `@/lib/media/quality`) is written for a desk, and on
 * a phone one line of it is actively absurd: "plug into the network with a cable". Nobody filming
 * from a phone has a cable, and advice that cannot be followed teaches people to stop reading it.
 *
 * The other difference is subtler and matters more. On a phone the quality ladder has already
 * dropped the rung by the time anybody opens this panel (ADR 0021), so telling them to lower the
 * quality is telling them to do something that has been done. What is left is the part only they
 * can do: move, or put the phone down for a minute.
 *
 * Returns null when nothing needs doing, which is the normal case.
 */
export function mobileAdvice(measured: {
  limitation: QualityLimitation;
  packetLossPercent: number;
  verdict: QualityVerdict;
}): string | null {
  if (measured.limitation === "cpu") {
    return "Your phone cannot encode fast enough. Close other apps, and let it cool if it feels hot — the quality lowers itself meanwhile.";
  }

  if (measured.limitation === "bandwidth" || measured.packetLossPercent >= 2) {
    return "Your connection cannot carry this quality. Move somewhere with better signal, or switch between Wi-Fi and mobile data.";
  }

  if (measured.verdict === "poor") {
    return "Video is not going out smoothly. Check your signal — the quality adjusts itself while you do.";
  }

  return null;
}

/** The other camera. Flipping is the one camera control a phone needs and the one it always has. */
export function oppositeFacing(facing: CameraFacing): CameraFacing {
  return facing === "user" ? "environment" : "user";
}

/**
 * Which way a track is actually pointing, which is not always which way it was asked to point.
 *
 * A phone with one camera answers an `ideal: "environment"` request with the front camera, and the
 * preview has to know: a front camera shown unmirrored is the thing every person notices
 * immediately about their own face.
 */
export function facingOfTrack(track: MediaStreamTrack | null, fallback: CameraFacing): CameraFacing {
  const reported = track?.getSettings?.().facingMode;
  return reported === "user" || reported === "environment" ? reported : fallback;
}
