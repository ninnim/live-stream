import { describe, expect, it } from "vitest";
import {
  type OutboundSample,
  describeEncoder,
  nextLowerQuality,
  readOutboundVideo,
  summarizeQuality,
} from "@/lib/media/quality";

/**
 * `RTCStatsReport` is a read-only Map, so a plain Map is a faithful stand-in — the module only
 * iterates it.
 */
function statsReport(stats: Record<string, unknown>[]): Map<string, unknown> {
  return new Map(stats.map((stat, index) => [`stat-${index}`, stat]));
}

function sample(overrides: Partial<OutboundSample> = {}): OutboundSample {
  return {
    timestampMs: 0,
    bytesSent: 0,
    packetsSent: 0,
    packetsLost: 0,
    roundTripMs: 20,
    framesPerSecond: 30,
    width: 1280,
    height: 720,
    limitation: "none",
    encoder: null,
    powerEfficientEncoder: null,
    ...overrides,
  };
}

describe("readOutboundVideo", () => {
  it("combines the outgoing stream with the receiver's report of it", () => {
    // Loss and round-trip time only exist on remote-inbound: our own counters cannot know what
    // failed to arrive.
    const report = statsReport([
      {
        type: "outbound-rtp",
        kind: "video",
        timestamp: 1000,
        bytesSent: 500_000,
        packetsSent: 400,
        framesPerSecond: 30,
        frameWidth: 1280,
        frameHeight: 720,
        qualityLimitationReason: "bandwidth",
      },
      { type: "remote-inbound-rtp", kind: "video", packetsLost: 12, roundTripTime: 0.085 },
      { type: "outbound-rtp", kind: "audio", bytesSent: 9_000 },
    ]);

    expect(readOutboundVideo(report)).toEqual({
      timestampMs: 1000,
      bytesSent: 500_000,
      packetsSent: 400,
      packetsLost: 12,
      roundTripMs: 85,
      framesPerSecond: 30,
      width: 1280,
      height: 720,
      limitation: "bandwidth",
      encoder: null,
      powerEfficientEncoder: null,
    });
  });

  it("ignores audio-only reports", () => {
    const report = statsReport([{ type: "outbound-rtp", kind: "audio", bytesSent: 9_000 }]);

    expect(readOutboundVideo(report)).toBeNull();
  });

  it("treats an unrecognised limitation reason as 'other' rather than 'none'", () => {
    // A future browser inventing a reason must not be read as "nothing is wrong".
    const report = statsReport([
      { type: "outbound-rtp", kind: "video", qualityLimitationReason: "something-new" },
    ]);

    expect(readOutboundVideo(report)?.limitation).toBe("other");
  });
});

describe("summarizeQuality", () => {
  it("reports that it is measuring on the first reading", () => {
    // Every figure worth showing is a rate, and a rate needs two samples.
    const result = summarizeQuality(sample(), null);

    expect(result.bitrateKbps).toBe(0);
    expect(result.headline).toContain("Measuring");
  });

  it("computes bitrate from the change between readings", () => {
    const previous = sample({ timestampMs: 0, bytesSent: 0 });
    const current = sample({ timestampMs: 2000, bytesSent: 500_000 });

    // 500 kB over 2 s = 2000 kbps.
    expect(summarizeQuality(current, previous).bitrateKbps).toBe(2000);
  });

  it("grades a healthy stream as smooth", () => {
    const result = summarizeQuality(
      sample({ timestampMs: 2000, bytesSent: 700_000, packetsSent: 1000 }),
      sample(),
    );

    expect(result.verdict).toBe("good");
    expect(result.advice).toBeNull();
  });

  it("blames the computer when the encoder cannot keep up", () => {
    const result = summarizeQuality(
      sample({ timestampMs: 2000, bytesSent: 700_000, packetsSent: 1000, limitation: "cpu" }),
      sample(),
    );

    expect(result.verdict).toBe("fair");
    expect(result.headline).toContain("encode");
    expect(result.advice).toContain("Close other applications");
  });

  it("blames the connection when bandwidth is the limit, and suggests a cable", () => {
    // The two causes need opposite responses. Telling someone to close applications when their
    // Wi-Fi is the problem wastes the only minutes they have.
    const result = summarizeQuality(
      sample({ timestampMs: 2000, bytesSent: 700_000, packetsSent: 1000, limitation: "bandwidth" }),
      sample(),
    );

    expect(result.headline).toContain("upload");
    expect(result.advice).toContain("cable");
    expect(result.advice).not.toContain("Close other applications");
  });

  it("grades heavy packet loss as poor", () => {
    const result = summarizeQuality(
      sample({ timestampMs: 2000, bytesSent: 700_000, packetsSent: 1000, packetsLost: 100 }),
      sample(),
    );

    expect(result.verdict).toBe("poor");
    expect(result.packetLossPercent).toBeCloseTo(9.1, 1);
  });

  it("never reports negative loss when a receiver report arrives out of order", () => {
    // Cumulative counters can go backwards on reordering; a negative loss rate is not a thing
    // anyone should be shown.
    const previous = sample({ packetsSent: 1000, packetsLost: 50 });
    const current = sample({ timestampMs: 2000, bytesSent: 700_000, packetsSent: 900, packetsLost: 40 });

    const result = summarizeQuality(current, previous);

    expect(result.packetLossPercent).toBe(0);
    expect(result.bitrateKbps).toBeGreaterThanOrEqual(0);
  });

  it("grades a juddering stream as poor even when nothing else complains", () => {
    const result = summarizeQuality(
      sample({ timestampMs: 2000, bytesSent: 700_000, packetsSent: 1000, framesPerSecond: 4 }),
      sample(),
    );

    expect(result.verdict).toBe("poor");
  });

  it("does not condemn an idle encoder reporting no frames", () => {
    // Zero frames is also what a paused camera looks like, and calling that "poor" would light the
    // indicator every time someone turns their camera off.
    const result = summarizeQuality(
      sample({ timestampMs: 2000, bytesSent: 700_000, packetsSent: 1000, framesPerSecond: 0 }),
      sample(),
    );

    expect(result.verdict).toBe("good");
  });

  it("flags a high round trip as strained without calling it broken", () => {
    const result = summarizeQuality(
      sample({ timestampMs: 2000, bytesSent: 700_000, packetsSent: 1000, roundTripMs: 450 }),
      sample(),
    );

    expect(result.verdict).toBe("fair");
  });
});

describe("nextLowerQuality", () => {
  it("steps down one rung at a time and stops at the bottom", () => {
    expect(nextLowerQuality("1080p")).toBe("720p");
    expect(nextLowerQuality("auto")).toBe("720p");
    expect(nextLowerQuality("720p")).toBe("540p");
    expect(nextLowerQuality("540p")).toBeNull();
  });
});

/**
 * Whether this machine's GPU is doing the encoding.
 *
 * The browser is the only thing that knows, and it says so in one free-text field. Guessing from
 * anything else — frame rate, CPU pressure — would be telling somebody their GPU is idle when it is
 * not, which is worse than saying nothing.
 */
describe("describeEncoder", () => {
  it("believes the browser's own answer over any name", () => {
    // `powerEfficientEncoder` is a standard boolean meaning "hardware accelerated". Reading it is
    // the whole job; the name matching below is only what happens when it is absent.
    const result = describeEncoder("VaapiVideoEncodeAccelerator", true);

    expect(result).toEqual({ name: "VaapiVideoEncodeAccelerator", hardware: true });
  });

  it("reports a GPU even when the encoder name means nothing to us", () => {
    // The regression this exists for. Hardware encoder names are not stable — they differ per
    // platform and change between browser versions — so a machine whose GPU was plainly doing the
    // work was told its encoder was Unknown, because the name was not one this code was taught.
    const result = describeEncoder("SomeEncoderShippedNextYear", true);

    expect(result?.hardware).toBe(true);
  });

  it("reports the CPU when the browser says the encoder is not power efficient", () => {
    expect(describeEncoder("SomeEncoderShippedNextYear", false)?.hardware).toBe(false);
  });

  it("falls back to recognising the software encoders by name", () => {
    // Software names are stable across versions, which is what makes them safe to match on.
    expect(describeEncoder("libvpx")?.hardware).toBe(false);
    expect(describeEncoder("OpenH264")?.hardware).toBe(false);
    expect(describeEncoder("FFmpeg")?.hardware).toBe(false);
    expect(describeEncoder("libaom")?.hardware).toBe(false);
  });

  it("reads the real encoder through a simulcast wrapper", () => {
    expect(describeEncoder("SimulcastEncoderAdapter (libvpx, libvpx, libvpx)")?.hardware).toBe(false);
  });

  it("still recognises the older hardware names when no boolean is given", () => {
    expect(describeEncoder("ExternalEncoder")?.hardware).toBe(true);
    expect(describeEncoder("SimulcastEncoderAdapter (HardwareAccelerated)")?.hardware).toBe(true);
  });

  it("says it does not know rather than guessing, when nothing tells it", () => {
    // Distinct from "no GPU", and the panel renders it as "Not reported" for exactly that reason.
    expect(describeEncoder("SomeFutureEncoder")).toEqual({
      name: "SomeFutureEncoder",
      hardware: null,
    });
  });

  it("reports nothing when the browser reports nothing", () => {
    expect(describeEncoder(undefined)).toBeNull();
    expect(describeEncoder("")).toBeNull();
    expect(describeEncoder("   ")).toBeNull();
  });

  it("is carried through from the stats report to the summary", () => {
    const report = statsReport([
      {
        type: "outbound-rtp",
        kind: "video",
        timestamp: 1000,
        bytesSent: 100_000,
        packetsSent: 100,
        framesPerSecond: 60,
        encoderImplementation: "SomeVendorEncoder",
        powerEfficientEncoder: true,
      },
    ]);

    const read = readOutboundVideo(report);
    expect(read?.encoder).toBe("SomeVendorEncoder");
    expect(read?.powerEfficientEncoder).toBe(true);

    const summary = summarizeQuality(
      sample({ encoder: "SomeVendorEncoder", powerEfficientEncoder: true }),
      null,
    );
    expect(summary.encoder).toEqual({ name: "SomeVendorEncoder", hardware: true });
  });

  it("reports no boolean when the browser omits the field", () => {
    // Safari and older Chrome do not report it. The name fallback has to still be reachable.
    const report = statsReport([
      {
        type: "outbound-rtp",
        kind: "video",
        timestamp: 1000,
        bytesSent: 100_000,
        packetsSent: 100,
        encoderImplementation: "libvpx",
      },
    ]);

    expect(readOutboundVideo(report)?.powerEfficientEncoder).toBeNull();
  });
});
