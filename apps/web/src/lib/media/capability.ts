import type { CaptureQuality } from "@/lib/media/devices";

/**
 * What this machine can actually encode, asked before anything goes on air.
 *
 * The studio already *reports* whether a GPU is doing the work (ADR 0017 §6), and until now did
 * nothing with the answer: a laptop encoding 1080p60 in software was told so, and left to discover
 * for itself that the frame rate collapses once the fans spin up.
 *
 * This is the acting half. It runs **before the broadcast starts** and only picks a starting rung —
 * which is deliberately the one place automatic adaptation belongs. Lowering the rung *during* a
 * broadcast stays manual for the reason in ADR 0011 §7: a resolution drop mid-sentence is
 * indistinguishable from a fault to everyone watching, and the operator is the one who knows
 * whether the shot matters more than the smoothness. Choosing well before anyone is watching
 * costs nothing and surprises nobody.
 */

/** Candidate codecs, in the order this platform would choose between equals. */
export const CODEC_CANDIDATES = [
  "video/H264;codecs=avc1.42E01E",
  "video/VP9",
  "video/VP8",
] as const;

/** The `video/...` prefix the sender's codec list is matched on. */
export function codecFamily(contentType: string): string {
  return contentType.split(";")[0]!;
}

interface Rung {
  quality: Exclude<CaptureQuality, "auto">;
  width: number;
  height: number;
  frameRate: number;
  bitrate: number;
}

/**
 * The configurations worth asking about, hardest first.
 *
 * Bitrates match what the publisher would actually send; asking about 1080p60 at a trivial bitrate
 * would get an answer about a broadcast nobody is going to make.
 */
const RUNGS: Rung[] = [
  { quality: "1080p", width: 1920, height: 1080, frameRate: 60, bitrate: 6_000_000 },
  { quality: "1080p", width: 1920, height: 1080, frameRate: 30, bitrate: 4_500_000 },
  { quality: "720p", width: 1280, height: 720, frameRate: 60, bitrate: 3_500_000 },
  { quality: "720p", width: 1280, height: 720, frameRate: 30, bitrate: 2_500_000 },
  { quality: "540p", width: 960, height: 540, frameRate: 30, bitrate: 1_500_000 },
];

export interface RungSupport {
  quality: Exclude<CaptureQuality, "auto">;
  frameRate: number;
  supported: boolean;
  /** The browser's guess at whether it can keep up. Optimistic — see `chooseStartingQuality`. */
  smooth: boolean;
  /** Hardware-accelerated. The signal with real meaning. */
  powerEfficient: boolean;
}

export interface MachineCapability {
  /** Whether the browser answered at all. Everything below is a default when it did not. */
  probed: boolean;
  /** Logical CPU cores, or null where the browser will not say. */
  cores: number | null;
  /** Codec families the GPU can encode, in this platform's preference order. */
  hardwareCodecs: string[];
  /** Per-rung answers for the best codec available, hardest first. */
  rungs: RungSupport[];
  /** Where the studio should start. */
  recommended: { quality: Exclude<CaptureQuality, "auto">; screenFrameRate: 30 | 60 };
  /** One sentence for the operator, naming the evidence. */
  reason: string;
}

/** The slice of `MediaCapabilities` this needs, so the policy can be tested without a browser. */
export interface EncodingProbe {
  (configuration: {
    type: "webrtc";
    video: {
      contentType: string;
      width: number;
      height: number;
      bitrate: number;
      framerate: number;
    };
  }): Promise<{ supported: boolean; smooth: boolean; powerEfficient: boolean }>;
}

export interface CapabilityInputs {
  cores: number | null;
  /** Which codec families reported hardware acceleration, in candidate order. */
  hardwareCodecs: string[];
  rungs: RungSupport[];
  probed: boolean;
}

/**
 * Cores below which software encoding of 1080p is a bad bet.
 *
 * WebRTC encoding is threaded, but a broadcast is not the only thing running: there is a
 * compositor, a browser, and whatever is being demonstrated. Twelve is where 1080p software
 * encoding stops competing with the thing being broadcast.
 */
const CORES_FOR_SOFTWARE_1080P = 12;

/** Below this, even 720p software encoding is asking a lot alongside everything else. */
const CORES_FOR_SOFTWARE_720P = 6;

/**
 * Picks where to start.
 *
 * `powerEfficient` decides, not `smooth`. Measured in a container with no GPU at all, Chrome
 * reports `smooth: true` for 1080p60 software encoding — it is an optimistic answer about whether
 * the codec can run, not a load test of this machine. `powerEfficient` means hardware-accelerated,
 * which is a fact rather than a prediction, and it is the fact that decides whether a laptop holds
 * 1080p60 or cooks.
 */
export function chooseStartingQuality(inputs: CapabilityInputs): {
  quality: Exclude<CaptureQuality, "auto">;
  screenFrameRate: 30 | 60;
  reason: string;
} {
  const usable = inputs.rungs.filter((rung) => rung.supported);

  // Nothing to go on: keep the platform default rather than inventing a worse one.
  if (!inputs.probed || usable.length === 0) {
    return {
      quality: "720p",
      screenFrameRate: 30,
      reason: "This browser does not report what it can encode, so the usual defaults apply.",
    };
  }

  const accelerated = usable.filter((rung) => rung.powerEfficient);

  if (accelerated.length > 0) {
    // Hardware encoding: take the best rung the GPU will accelerate.
    const best = accelerated[0]!;
    return {
      quality: best.quality,
      screenFrameRate: best.frameRate >= 60 ? 60 : 30,
      reason: `Your graphics card encodes ${best.quality}${best.frameRate} in hardware, so the studio starts there.`,
    };
  }

  // Software encoding. The only honest signal left is how much CPU there is.
  const cores = inputs.cores ?? 0;

  if (cores >= CORES_FOR_SOFTWARE_1080P) {
    return {
      quality: has(usable, "1080p") ? "1080p" : "720p",
      // Not 60: software 1080p60 is where a machine without a hardware encoder falls over, and it
      // falls over live rather than here.
      screenFrameRate: 30,
      reason: `No hardware encoder, but ${cores} CPU cores — 1080p at 30 fps is a safe start.`,
    };
  }

  if (cores >= CORES_FOR_SOFTWARE_720P) {
    return {
      quality: has(usable, "720p") ? "720p" : "540p",
      screenFrameRate: 30,
      reason: `No hardware encoder and ${cores} CPU cores, so the studio starts at 720p to stay smooth.`,
    };
  }

  return {
    quality: has(usable, "540p") ? "540p" : "720p",
    screenFrameRate: 30,
    reason: cores > 0
      ? `No hardware encoder and only ${cores} CPU cores, so the studio starts low to stay smooth.`
      : "No hardware encoder found, so the studio starts low to stay smooth.",
  };
}

function has(rungs: RungSupport[], quality: Exclude<CaptureQuality, "auto">): boolean {
  return rungs.some((rung) => rung.quality === quality);
}

/**
 * Asks the browser what it can encode.
 *
 * Every failure answers "could not tell" rather than throwing: this runs on the way into the
 * studio, and a browser without `mediaCapabilities` must still be able to broadcast.
 */
export async function probeMachine(
  encodingInfo: EncodingProbe | null = defaultProbe(),
  cores: number | null = defaultCores(),
): Promise<MachineCapability> {
  if (!encodingInfo) {
    return unprobed(cores);
  }

  const hardwareCodecs: string[] = [];
  let best: { codec: string; rungs: RungSupport[] } | null = null;

  for (const codec of CODEC_CANDIDATES) {
    const rungs: RungSupport[] = [];

    for (const rung of RUNGS) {
      try {
        const info = await encodingInfo({
          type: "webrtc",
          video: {
            contentType: codec,
            width: rung.width,
            height: rung.height,
            bitrate: rung.bitrate,
            framerate: rung.frameRate,
          },
        });

        rungs.push({
          quality: rung.quality,
          frameRate: rung.frameRate,
          supported: info.supported,
          smooth: info.smooth,
          powerEfficient: info.powerEfficient,
        });
      } catch {
        // One refused configuration is not a refused browser; the rung is simply unknown.
      }
    }

    if (rungs.some((rung) => rung.supported && rung.powerEfficient)) {
      hardwareCodecs.push(codecFamily(codec));
    }

    // The first codec that answered at all becomes the baseline; a later one only replaces it by
    // offering hardware where the baseline offered none.
    const acceleratedHere = rungs.some((rung) => rung.powerEfficient);
    const acceleratedBest = best?.rungs.some((rung) => rung.powerEfficient) ?? false;

    if (rungs.length > 0 && (best === null || (acceleratedHere && !acceleratedBest))) {
      best = { codec, rungs };
    }
  }

  if (best === null) {
    return unprobed(cores);
  }

  const inputs: CapabilityInputs = {
    cores,
    hardwareCodecs,
    rungs: best.rungs,
    probed: true,
  };

  const choice = chooseStartingQuality(inputs);

  return {
    probed: true,
    cores,
    hardwareCodecs,
    rungs: best.rungs,
    recommended: { quality: choice.quality, screenFrameRate: choice.screenFrameRate },
    reason: choice.reason,
  };
}

function unprobed(cores: number | null): MachineCapability {
  const choice = chooseStartingQuality({ cores, hardwareCodecs: [], rungs: [], probed: false });

  return {
    probed: false,
    cores,
    hardwareCodecs: [],
    rungs: [],
    recommended: { quality: choice.quality, screenFrameRate: choice.screenFrameRate },
    reason: choice.reason,
  };
}

function defaultProbe(): EncodingProbe | null {
  if (typeof navigator === "undefined") return null;

  const capabilities = navigator.mediaCapabilities;
  if (typeof capabilities?.encodingInfo !== "function") return null;

  return (configuration) =>
    capabilities.encodingInfo(configuration as unknown as MediaEncodingConfiguration);
}

function defaultCores(): number | null {
  if (typeof navigator === "undefined") return null;
  return typeof navigator.hardwareConcurrency === "number" ? navigator.hardwareConcurrency : null;
}
