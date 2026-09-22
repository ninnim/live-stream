/**
 * Adaptive encoder control.
 *
 * The studio measures how well video is going out (`@/lib/media/quality`) and, until now, only
 * ever *said* so: ADR 0011 §7 makes dropping quality the operator's decision, because a resolution
 * drop mid-sentence is indistinguishable from a fault to everyone watching, and the operator is
 * the one who knows whether the shot matters more than the smoothness.
 *
 * That reasoning holds at a desk and fails on a phone. Nobody filming from a phone is watching a
 * health panel — they are holding the camera and looking at the thing they are filming — so an
 * offer to step down is an offer nobody sees, and the broadcast stutters until it ends. ADR 0021
 * records the narrower policy this module implements: **on mobile, adapt automatically; say so;
 * let it be overridden.**
 *
 * What is adapted matters as much as when. Every rung here is expressed as sender parameters —
 * bitrate, resolution scale, frame rate — which `RTCRtpSender.setParameters` applies to the
 * running encoder. No renegotiation, no new peer connection, no gap in the stream: the picture
 * softens for a moment and the broadcast continues. Re-opening the camera at a lower rung, which
 * is what the desktop quality selector does, would black the outgoing video out for as long as the
 * device takes to re-open, and doing that automatically would be worse than the problem.
 *
 * The module is pure: samples and a clock go in, a decision comes out. No timers, no WebRTC.
 */

import type { PublishQuality } from "@/lib/media/quality";

/** Limits applied to the running encoder. Every field maps to an `RTCRtpEncodingParameters` key. */
export interface EncodingLimits {
  maxBitrate?: number;
  /** Divides the captured dimensions. 1 is full size; 2 is quarter area. */
  scaleResolutionDownBy?: number;
  maxFramerate?: number;
}

export interface EncodingRung extends EncodingLimits {
  id: string;
  /** Shown to the broadcaster. Intent, not pixels — the pixels depend on what the camera gave us. */
  label: string;
}

/**
 * The mobile ladder, best first.
 *
 * Sized for a 720p30 capture, which is where {@link import("./mobile").chooseMobileStartingQuality}
 * starts a phone. The rungs shed resolution before frame rate: a phone broadcast is almost always
 * a person or a place moving, and a soft moving picture reads as live television where a sharp
 * juddering one reads as broken. Only the bottom two rungs touch the frame rate, and by then the
 * alternative is not sending video at all.
 *
 * The bitrates are ceilings rather than targets; WebRTC sends less whenever less will do.
 */
export const MOBILE_LADDER: EncodingRung[] = [
  { id: "full", label: "Full", scaleResolutionDownBy: 1, maxFramerate: 30, maxBitrate: 2_500_000 },
  { id: "reduced", label: "Reduced", scaleResolutionDownBy: 1.5, maxFramerate: 30, maxBitrate: 1_200_000 },
  { id: "low", label: "Low", scaleResolutionDownBy: 2, maxFramerate: 24, maxBitrate: 700_000 },
  { id: "minimum", label: "Minimum", scaleResolutionDownBy: 3, maxFramerate: 20, maxBitrate: 350_000 },
];

export interface AdaptiveState {
  /** Index into the ladder. 0 is the top. */
  index: number;
  /** Consecutive samples that looked strained. */
  strain: number;
  /** Consecutive samples that looked comfortable. */
  calm: number;
  /** No further change before this instant. Absolute milliseconds, same clock as `nowMs`. */
  holdUntilMs: number;
}

export const INITIAL_ADAPTIVE_STATE: AdaptiveState = {
  index: 0,
  strain: 0,
  calm: 0,
  holdUntilMs: 0,
};

export interface AdaptiveChange {
  rung: EncodingRung;
  direction: "down" | "up";
  /** One sentence for the broadcaster. Always present, because every change is worth explaining. */
  reason: string;
}

export interface AdaptiveDecision {
  state: AdaptiveState;
  /** Null on the overwhelming majority of samples: nothing needs doing. */
  change: AdaptiveChange | null;
}

export interface AdaptiveOptions {
  ladder?: EncodingRung[];
  /**
   * The best rung allowed. Set when the broadcaster has pinned a quality by hand — the ladder may
   * still descend below it to keep the stream alive, but it will not climb back past their choice.
   */
  ceilingIndex?: number;
}

/**
 * Consecutive strained samples before stepping down.
 *
 * Samples arrive every two seconds, so this is roughly six seconds of trouble. Long enough that a
 * lift, a doorway or a single bad receiver report does not move anything; short enough that a cell
 * handover is corrected before a viewer has finished deciding the stream is broken.
 */
const STRAIN_SAMPLES_TO_STEP_DOWN = 3;

/**
 * Consecutive comfortable samples before stepping back up — about half a minute.
 *
 * Deliberately five times slower than stepping down, and that asymmetry is the whole trick.
 * Climbing is optional and costs a quality change nobody asked for; descending is what keeps the
 * broadcast watchable. A ladder that climbs as eagerly as it descends oscillates, and oscillation
 * looks far worse than simply sitting one rung low.
 */
const CALM_SAMPLES_TO_STEP_UP = 15;

/** Nothing changes for this long after a step down: the encoder needs time to show what it can do. */
const HOLD_AFTER_STEP_DOWN_MS = 8_000;

/** Longer after a step up, because an over-eager climb is the failure mode worth being slow about. */
const HOLD_AFTER_STEP_UP_MS = 20_000;

/** Loss above this is strain whatever else the sample says. Matches the "fair" threshold in quality.ts. */
const STRAIN_LOSS_PERCENT = 2;

/** Below this, the link is genuinely quiet rather than merely not-bad. */
const CALM_LOSS_PERCENT = 0.5;

/** A sample the encoder is visibly struggling with. */
function isStrained(sample: PublishQuality): boolean {
  return (
    sample.limitation === "cpu"
    || sample.limitation === "bandwidth"
    || sample.packetLossPercent >= STRAIN_LOSS_PERCENT
    || sample.verdict === "poor"
  );
}

/** A sample with room to spare. Stricter than "not strained", on purpose. */
function isCalm(sample: PublishQuality): boolean {
  return (
    sample.verdict === "good"
    && sample.limitation === "none"
    && sample.packetLossPercent < CALM_LOSS_PERCENT
    // A first reading of a connection reports zero bitrate and means nothing yet; counting it
    // towards a climb would let the ladder rise on no evidence at all.
    && sample.bitrateKbps > 0
  );
}

function stepDownReason(sample: PublishQuality, rung: EncodingRung): string {
  if (sample.limitation === "cpu") {
    return `This phone could not encode fast enough, so the quality dropped to ${rung.label.toLowerCase()} to stay smooth.`;
  }

  if (sample.limitation === "bandwidth" || sample.packetLossPercent >= STRAIN_LOSS_PERCENT) {
    return `Your connection could not carry the video, so the quality dropped to ${rung.label.toLowerCase()}.`;
  }

  return `Video was not going out smoothly, so the quality dropped to ${rung.label.toLowerCase()}.`;
}

/**
 * Decides what the encoder should do next, given one more measurement.
 *
 * Called once per quality sample. Returns the state to carry into the next call, and a change only
 * on the samples where something should actually happen — which is almost none of them.
 *
 * A null sample (nothing is publishing, or the browser refused stats) clears both counters without
 * touching the rung. Losing the measurements is not evidence of anything, and a broadcast that
 * stepped down every time stats went missing would walk itself to the bottom of the ladder.
 */
export function adapt(
  state: AdaptiveState,
  sample: PublishQuality | null,
  nowMs: number,
  options: AdaptiveOptions = {},
): AdaptiveDecision {
  const ladder = options.ladder ?? MOBILE_LADDER;
  const ceiling = Math.max(0, Math.min(options.ceilingIndex ?? 0, ladder.length - 1));

  if (!sample) {
    return { state: { ...state, strain: 0, calm: 0 }, change: null };
  }

  const strained = isStrained(sample);
  const calm = isCalm(sample);

  const next: AdaptiveState = {
    ...state,
    strain: strained ? state.strain + 1 : 0,
    calm: calm ? state.calm + 1 : 0,
  };

  // Inside the hold window the counters still run — so the evidence is ready the moment the hold
  // expires — but nothing moves.
  if (nowMs < state.holdUntilMs) {
    return { state: next, change: null };
  }

  if (next.strain >= STRAIN_SAMPLES_TO_STEP_DOWN && state.index < ladder.length - 1) {
    const index = state.index + 1;
    const rung = ladder[index]!;

    return {
      state: { index, strain: 0, calm: 0, holdUntilMs: nowMs + HOLD_AFTER_STEP_DOWN_MS },
      change: { rung, direction: "down", reason: stepDownReason(sample, rung) },
    };
  }

  if (next.calm >= CALM_SAMPLES_TO_STEP_UP && state.index > ceiling) {
    const index = state.index - 1;
    const rung = ladder[index]!;

    return {
      state: { index, strain: 0, calm: 0, holdUntilMs: nowMs + HOLD_AFTER_STEP_UP_MS },
      change: {
        rung,
        direction: "up",
        reason: `Your connection has settled, so the quality went back up to ${rung.label.toLowerCase()}.`,
      },
    };
  }

  return { state: next, change: null };
}

/**
 * Resets the ladder to the top, for use when the broadcaster changes the capture rung by hand.
 *
 * Their choice is about the camera, and the ladder's position is about a link that has just been
 * given a different amount of work to do. Carrying a "minimum" rung across into a freshly chosen
 * 540p capture would quietly halve what they asked for.
 */
export function resetLadder(nowMs: number): AdaptiveState {
  return { ...INITIAL_ADAPTIVE_STATE, holdUntilMs: nowMs + HOLD_AFTER_STEP_DOWN_MS };
}
