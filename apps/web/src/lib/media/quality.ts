/**
 * Publish-side quality measurement.
 *
 * The server already reports whether a session is healthy, but it can only see what arrives. When a
 * broadcast looks bad, the cause is almost always upstream of the gateway — an overloaded encoder
 * or an upload link that cannot carry the bitrate — and only the browser can tell those apart.
 * `qualityLimitationReason` is the one signal that distinguishes them, which is why this module
 * exists rather than inferring "poor" from throughput alone.
 *
 * Everything here is pure: stats in, verdict out. No timers, no DOM, no peer connection.
 */

/** Why WebRTC is holding the encoder back. Mirrors the spec value, narrowed to what we act on. */
export type QualityLimitation = "none" | "cpu" | "bandwidth" | "other";

export type QualityVerdict = "good" | "fair" | "poor";

/** What the browser is encoding outgoing video with. */
export interface EncoderInfo {
  /** The browser's own name for it, shown as-is rather than guessed at. */
  name: string;
  /**
   * Whether a GPU is doing the encoding. Null only when the browser genuinely will not say.
   */
  hardware: boolean | null;
}

/**
 * Software encoders name themselves, and the names are stable across browser versions.
 *
 * Only used as a fallback. Hardware encoder names are *not* stable — every platform has its own,
 * they change between releases, and guessing at an unrecognised one would eventually tell somebody
 * their GPU was idle when it was not.
 */
const SOFTWARE_NAMES = ["libvpx", "libaom", "openh264", "ffmpeg", "libx264"];

/**
 * What is doing the encoding, and whether it is the GPU.
 *
 * `powerEfficientEncoder` is the answer whenever the browser gives it: a standard boolean, defined
 * to mean hardware-accelerated, reported by Chrome for every outbound video stream. Reading it is
 * the whole point of this function.
 *
 * It was not always read here, and the cost of that was the studio reporting **Unknown** on a
 * machine whose GPU was plainly doing the work — because the fallback below only recognises names
 * it has been taught, and Chrome long ago stopped reporting the `ExternalEncoder` it was taught.
 * A boolean the browser hands over beats a string this code tries to interpret.
 */
export function describeEncoder(
  implementation: string | null | undefined,
  powerEfficient?: boolean | null,
): EncoderInfo | null {
  const name = implementation?.trim();
  if (!name) return null;

  if (typeof powerEfficient === "boolean") {
    return { name, hardware: powerEfficient };
  }

  const lower = name.toLowerCase();

  // A simulcast adapter names what it wraps — "SimulcastEncoderAdapter (libvpx, libvpx)" — so a
  // substring match reads the real encoder through the wrapper.
  if (SOFTWARE_NAMES.some((software) => lower.includes(software))) {
    return { name, hardware: false };
  }

  if (lower.includes("external") || lower.includes("hardware")) {
    return { name, hardware: true };
  }

  return { name, hardware: null };
}

/** A single reading of the outbound video stream. Counters are cumulative, as WebRTC reports them. */
export interface OutboundSample {
  timestampMs: number;
  bytesSent: number;
  packetsSent: number;
  /** Reported by the receiver, so it lags and can go backwards when packets arrive out of order. */
  packetsLost: number;
  roundTripMs: number | null;
  framesPerSecond: number | null;
  width: number | null;
  height: number | null;
  limitation: QualityLimitation;
  /**
   * What the browser is encoding with, verbatim — e.g. `libvpx`, `OpenH264`. Shown to the operator
   * as-is; never the primary evidence for whether a GPU is involved.
   */
  encoder: string | null;
  /** The browser's own answer to "is this hardware accelerated". Null when it does not say. */
  powerEfficientEncoder: boolean | null;
}

export interface PublishQuality {
  bitrateKbps: number;
  framesPerSecond: number;
  packetLossPercent: number;
  roundTripMs: number | null;
  width: number | null;
  height: number | null;
  limitation: QualityLimitation;
  /** The encoder in use, and whether it is hardware. Null when the browser does not say. */
  encoder: EncoderInfo | null;
  verdict: QualityVerdict;
  /** What is happening, in one sentence. Always present. */
  headline: string;
  /** What to do about it. `null` when nothing needs doing. */
  advice: string | null;
}

/** Anything with a `forEach` over stat objects: an `RTCStatsReport`, or a `Map` in tests. */
export interface StatsReportLike {
  forEach(callback: (value: unknown) => void): void;
}

interface RawStat {
  type?: string;
  kind?: string;
  mediaType?: string;
  timestamp?: number;
  bytesSent?: number;
  packetsSent?: number;
  packetsLost?: number;
  roundTripTime?: number;
  framesPerSecond?: number;
  frameWidth?: number;
  frameHeight?: number;
  qualityLimitationReason?: string;
  encoderImplementation?: string;
  /**
   * The standard boolean for "a GPU is doing this". Far better evidence than the name above, which
   * is free-form and differs per platform and per browser version.
   */
  powerEfficientEncoder?: boolean;
}

function narrowLimitation(reason: string | undefined): QualityLimitation {
  switch (reason) {
    case "cpu":
      return "cpu";
    case "bandwidth":
      return "bandwidth";
    case "none":
    case undefined:
      return "none";
    default:
      // "other" in the spec, plus anything a future browser invents.
      return "other";
  }
}

/**
 * Extracts the outbound video stream from a stats report.
 *
 * Loss and round-trip time live on `remote-inbound-rtp` — the receiver's report of what it actually
 * got — rather than on our own outbound counters, which by definition cannot know what went
 * missing. Returns `null` when no video is being sent, which is the normal state before publishing.
 */
export function readOutboundVideo(report: StatsReportLike): OutboundSample | null {
  let outbound: RawStat | null = null;
  let remoteInbound: RawStat | null = null;

  report.forEach((value) => {
    const stat = value as RawStat;
    const isVideo = stat.kind === "video" || stat.mediaType === "video";
    if (!isVideo) return;

    if (stat.type === "outbound-rtp") outbound = stat;
    else if (stat.type === "remote-inbound-rtp") remoteInbound = stat;
  });

  // TypeScript narrows assignments inside the callback to `never`; these restore the real type.
  const out = outbound as RawStat | null;
  const remote = remoteInbound as RawStat | null;

  if (!out) return null;

  return {
    timestampMs: out.timestamp ?? 0,
    bytesSent: out.bytesSent ?? 0,
    packetsSent: out.packetsSent ?? 0,
    packetsLost: remote?.packetsLost ?? 0,
    roundTripMs: typeof remote?.roundTripTime === "number" ? Math.round(remote.roundTripTime * 1000) : null,
    framesPerSecond: out.framesPerSecond ?? null,
    width: out.frameWidth ?? null,
    height: out.frameHeight ?? null,
    limitation: narrowLimitation(out.qualityLimitationReason),
    encoder: typeof out.encoderImplementation === "string" ? out.encoderImplementation : null,
    powerEfficientEncoder:
      typeof out.powerEfficientEncoder === "boolean" ? out.powerEfficientEncoder : null,
  };
}

// Thresholds. Deliberately generous: a false "poor" during a normal blip teaches operators to
// ignore the indicator, which is worse than not having one.
const POOR_LOSS_PERCENT = 5;
const FAIR_LOSS_PERCENT = 2;
const POOR_FPS = 10;
const FAIR_FPS = 20;
const POOR_BITRATE_KBPS = 200;
const FAIR_ROUND_TRIP_MS = 300;

/**
 * Compares two samples into a verdict.
 *
 * Needs a previous sample because every useful figure here is a rate. The first call after
 * publishing therefore reports "measuring" rather than inventing a number from a single reading.
 */
export function summarizeQuality(current: OutboundSample, previous: OutboundSample | null): PublishQuality {
  const elapsedSeconds = previous ? (current.timestampMs - previous.timestampMs) / 1000 : 0;

  if (!previous || elapsedSeconds <= 0) {
    return {
      bitrateKbps: 0,
      framesPerSecond: Math.round(current.framesPerSecond ?? 0),
      packetLossPercent: 0,
      roundTripMs: current.roundTripMs,
      width: current.width,
      height: current.height,
      limitation: current.limitation,
      encoder: describeEncoder(current.encoder, current.powerEfficientEncoder),
      verdict: "good",
      headline: "Measuring connection quality…",
      advice: null,
    };
  }

  const bitrateKbps = Math.round(((current.bytesSent - previous.bytesSent) * 8) / elapsedSeconds / 1000);

  // Both deltas are clamped: a receiver report that arrives out of order can make either go
  // backwards, and a negative loss rate is not a thing anyone should be shown.
  const packetsSent = Math.max(0, current.packetsSent - previous.packetsSent);
  const packetsLost = Math.max(0, current.packetsLost - previous.packetsLost);
  const packetLossPercent =
    packetsSent + packetsLost > 0 ? (packetsLost / (packetsSent + packetsLost)) * 100 : 0;

  const framesPerSecond = Math.round(current.framesPerSecond ?? 0);

  const verdict = gradeQuality({
    bitrateKbps,
    framesPerSecond,
    packetLossPercent,
    roundTripMs: current.roundTripMs,
    limitation: current.limitation,
  });

  const { headline, advice } = explainQuality(verdict, current.limitation, packetLossPercent);

  return {
    bitrateKbps: Math.max(0, bitrateKbps),
    framesPerSecond,
    packetLossPercent: Number(packetLossPercent.toFixed(1)),
    roundTripMs: current.roundTripMs,
    width: current.width,
    height: current.height,
    limitation: current.limitation,
    encoder: describeEncoder(current.encoder, current.powerEfficientEncoder),
    verdict,
    headline,
    advice,
  };
}

interface Grading {
  bitrateKbps: number;
  framesPerSecond: number;
  packetLossPercent: number;
  roundTripMs: number | null;
  limitation: QualityLimitation;
}

function gradeQuality(signals: Grading): QualityVerdict {
  // A stream carrying nothing at all is a failure, not a slow one. But a genuinely idle encoder
  // reports zero frames too, so bitrate alone does not condemn it.
  if (signals.packetLossPercent >= POOR_LOSS_PERCENT) return "poor";
  if (signals.framesPerSecond > 0 && signals.framesPerSecond < POOR_FPS) return "poor";
  if (signals.bitrateKbps > 0 && signals.bitrateKbps < POOR_BITRATE_KBPS) return "poor";

  if (signals.packetLossPercent >= FAIR_LOSS_PERCENT) return "fair";
  if (signals.framesPerSecond > 0 && signals.framesPerSecond < FAIR_FPS) return "fair";
  if (signals.limitation !== "none") return "fair";
  if (signals.roundTripMs !== null && signals.roundTripMs >= FAIR_ROUND_TRIP_MS) return "fair";

  return "good";
}

/**
 * Turns a verdict into words.
 *
 * The limitation reason drives the advice because the two causes need opposite responses: a CPU
 * limit is fixed by asking for less work, a bandwidth limit by improving the link. Telling someone
 * to close applications when their Wi-Fi is the problem wastes the only minutes they have.
 */
function explainQuality(
  verdict: QualityVerdict,
  limitation: QualityLimitation,
  packetLossPercent: number,
): { headline: string; advice: string | null } {
  if (limitation === "cpu") {
    return {
      headline: "This computer cannot encode video fast enough.",
      advice:
        "Close other applications, or drop the quality to 540p. Screen recorders and video calls are the usual culprits.",
    };
  }

  if (limitation === "bandwidth") {
    return {
      headline: "Your upload connection cannot carry this quality.",
      advice:
        "Drop to 540p, or plug into the network with a cable — Wi-Fi upload is the commonest cause of a stuttering broadcast.",
    };
  }

  if (packetLossPercent >= FAIR_LOSS_PERCENT) {
    return {
      headline: `Losing ${packetLossPercent.toFixed(1)}% of packets on the way out.`,
      advice: "Your network is dropping data. A wired connection is far steadier than Wi-Fi for live video.",
    };
  }

  if (verdict === "poor") {
    return {
      headline: "Video is not going out smoothly.",
      advice: "Check your network connection, then lower the quality if it does not settle.",
    };
  }

  if (verdict === "fair") {
    return { headline: "Video is going out, with some strain.", advice: null };
  }

  return { headline: "Video is going out smoothly.", advice: null };
}

/**
 * The quality rung to fall back to, or `null` when already at the bottom.
 *
 * Stepping down is offered rather than applied: an automatic drop mid-sentence is indistinguishable
 * from a fault to everyone watching, and the operator is the one who knows whether the shot matters
 * more than the smoothness.
 */
export function nextLowerQuality(current: string): "720p" | "540p" | null {
  if (current === "1080p" || current === "auto") return "720p";
  if (current === "720p") return "540p";
  return null;
}
