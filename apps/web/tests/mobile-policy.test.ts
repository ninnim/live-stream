import { describe, expect, it } from "vitest";
import { videoConstraints } from "@/lib/media/devices";
import {
  type FormFactorSignals,
  type NetworkSignals,
  chooseMobileStartingQuality,
  detectFormFactor,
  facingOfTrack,
  mobileAdvice,
  oppositeFacing,
  readNetworkSignals,
} from "@/lib/media/mobile";

function signals(overrides: Partial<FormFactorSignals> = {}): FormFactorSignals {
  return {
    coarsePointer: false,
    maxTouchPoints: 0,
    viewportWidth: 1440,
    userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0",
    ...overrides,
  };
}

function network(overrides: Partial<NetworkSignals> = {}): NetworkSignals {
  return { effectiveType: "4g", downlinkMbps: 10, saveData: false, ...overrides };
}

describe("detectFormFactor", () => {
  it("puts a phone on the mobile broadcaster", () => {
    expect(
      detectFormFactor(
        signals({
          coarsePointer: true,
          maxTouchPoints: 5,
          viewportWidth: 390,
          userAgent: "Mozilla/5.0 (Linux; Android 14; Pixel 8) Mobile Safari/537.36",
        }),
      ),
    ).toBe("mobile");
  });

  it("keeps a narrow desktop window on the full studio", () => {
    // Somebody resized a browser. Handing them a touch interface because of the width would be
    // wrong every time, and a mouse is the signal that says so.
    expect(detectFormFactor(signals({ viewportWidth: 600 }))).toBe("desktop");
  });

  it("keeps a landscape tablet on the full studio", () => {
    expect(
      detectFormFactor(
        signals({
          coarsePointer: true,
          maxTouchPoints: 5,
          viewportWidth: 1180,
          userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Version/17.0 Safari/605.1.15",
        }),
      ),
    ).toBe("desktop");
  });

  it("recognises an iPad, which reports itself as a Mac", () => {
    // Only `maxTouchPoints` gives it away, and the width then decides. A narrow split-screen iPad
    // is a phone-shaped surface and gets the phone-shaped broadcaster.
    expect(
      detectFormFactor(
        signals({
          coarsePointer: false,
          maxTouchPoints: 5,
          viewportWidth: 507,
          userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Version/17.0 Safari/605.1.15",
        }),
      ),
    ).toBe("mobile");
  });
});

describe("chooseMobileStartingQuality", () => {
  it("starts at 720p on a healthy connection", () => {
    const choice = chooseMobileStartingQuality({ network: network(), cores: 8 });

    // Never 1080p: nothing available before publishing measures the *uplink*, so the starting rung
    // is the one likely to survive rather than the best one that might.
    expect(choice.quality).toBe("720p");
  });

  it("respects Data Saver over every other signal", () => {
    const choice = chooseMobileStartingQuality({
      network: network({ saveData: true, downlinkMbps: 50 }),
      cores: 12,
    });

    expect(choice.quality).toBe("540p");
    expect(choice.reason).toContain("Data Saver");
  });

  it("drops to 540p on a slow radio", () => {
    expect(chooseMobileStartingQuality({ network: network({ effectiveType: "3g" }), cores: 8 }).quality).toBe(
      "540p",
    );
  });

  it("drops to 540p on an old phone", () => {
    expect(chooseMobileStartingQuality({ network: network(), cores: 2 }).quality).toBe("540p");
  });

  it("falls back to 720p when the browser reports no network at all", () => {
    // iOS has no Network Information API. Missing evidence must not read as bad evidence.
    const choice = chooseMobileStartingQuality({
      network: { effectiveType: null, downlinkMbps: null, saveData: false },
      cores: null,
    });

    expect(choice.quality).toBe("720p");
  });
});

describe("readNetworkSignals", () => {
  it("reads what a browser offers", () => {
    expect(readNetworkSignals({ effectiveType: "4g", downlink: 12.5, saveData: true })).toEqual({
      effectiveType: "4g",
      downlinkMbps: 12.5,
      saveData: true,
    });
  });

  it("answers 'nothing known' rather than throwing where the API is absent", () => {
    expect(readNetworkSignals(undefined)).toEqual({
      effectiveType: null,
      downlinkMbps: null,
      saveData: false,
    });
  });
});

describe("mobileAdvice", () => {
  it("never tells somebody holding a phone to plug in a cable", () => {
    // The studio's own advice for a bandwidth limit does exactly that. Advice that cannot be
    // followed teaches people to stop reading it.
    const advice = mobileAdvice({ limitation: "bandwidth", packetLossPercent: 0, verdict: "fair" });

    expect(advice).not.toMatch(/cable|wired/i);
    expect(advice).toMatch(/signal|mobile data/i);
  });

  it("separates a phone that cannot encode from a link that cannot carry", () => {
    // Opposite remedies. Telling somebody to find better signal while their phone is the
    // bottleneck wastes the only minutes they have.
    const cpu = mobileAdvice({ limitation: "cpu", packetLossPercent: 0, verdict: "fair" });

    expect(cpu).toMatch(/apps|hot/i);
  });

  it("treats loss as a connection problem even when the browser blames nothing", () => {
    expect(mobileAdvice({ limitation: "none", packetLossPercent: 4, verdict: "fair" })).toMatch(
      /connection/i,
    );
  });

  it("says nothing when there is nothing to do", () => {
    expect(mobileAdvice({ limitation: "none", packetLossPercent: 0, verdict: "good" })).toBeNull();
  });
});

describe("camera facing", () => {
  it("flips", () => {
    expect(oppositeFacing("user")).toBe("environment");
    expect(oppositeFacing("environment")).toBe("user");
  });

  it("believes the track over the request", () => {
    // A phone with one camera answers an `ideal: environment` request with the front camera, and
    // the preview mirrors from this answer — an unmirrored face is the thing everybody notices.
    const track = { getSettings: () => ({ facingMode: "user" }) } as unknown as MediaStreamTrack;

    expect(facingOfTrack(track, "environment")).toBe("user");
  });

  it("keeps the requested facing when the track will not say", () => {
    const track = { getSettings: () => ({}) } as unknown as MediaStreamTrack;

    expect(facingOfTrack(track, "environment")).toBe("environment");
  });
});

describe("videoConstraints with a mobile frame rate", () => {
  it("overrides the rung's frame rate without touching its resolution", () => {
    const constraints = videoConstraints("720p", undefined, 30);

    expect(constraints.width).toEqual({ ideal: 1280 });
    expect(constraints.frameRate).toEqual({ ideal: 30 });
  });

  it("leaves the studio's rungs exactly as they were", () => {
    // The desktop path passes no frame rate, and 720p60 has to stay 720p60.
    expect(videoConstraints("720p").frameRate).toEqual({ ideal: 60 });
  });
});
