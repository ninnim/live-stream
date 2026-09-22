import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { useAdaptiveQuality } from "@/hooks/useAdaptiveQuality";
import type { EncodingLimits } from "@/lib/media/adaptive";
import type { PublishQuality } from "@/lib/media/quality";

function sample(overrides: Partial<PublishQuality> = {}): PublishQuality {
  return {
    bitrateKbps: 2200,
    framesPerSecond: 30,
    packetLossPercent: 0,
    roundTripMs: 40,
    width: 1280,
    height: 720,
    limitation: "none",
    encoder: null,
    verdict: "good",
    headline: "Video is going out smoothly.",
    advice: null,
    ...overrides,
  };
}

const STRAINED = sample({ limitation: "bandwidth", verdict: "fair" });

/**
 * The controller around the pure ladder: it holds the position across samples, calls the
 * publisher, and keeps one sentence for the interface. The decisions themselves are tested in
 * `adaptive-quality.test.ts`; these are about what the hook does with them.
 */
describe("useAdaptiveQuality", () => {
  it("does nothing at all while adaptation is off", () => {
    // The desktop policy (ADR 0011 §7). A quality change nobody asked for is a change somebody has
    // to diagnose, and at a desk there is somebody watching.
    const applyEncoding = vi.fn<(limits: EncodingLimits) => Promise<void>>().mockResolvedValue();

    const { rerender, result } = renderHook(
      ({ quality }) => useAdaptiveQuality({ enabled: false, quality, applyEncoding }),
      { initialProps: { quality: STRAINED as PublishQuality | null } },
    );

    for (let index = 0; index < 10; index += 1) {
      rerender({ quality: sample({ ...STRAINED, bitrateKbps: 2200 - index }) });
    }

    expect(applyEncoding).not.toHaveBeenCalled();
    expect(result.current.reduced).toBe(false);
  });

  it("retunes the encoder and explains itself after sustained strain", () => {
    const applyEncoding = vi.fn<(limits: EncodingLimits) => Promise<void>>().mockResolvedValue();

    const { rerender, result } = renderHook(
      ({ quality }) => useAdaptiveQuality({ enabled: true, quality, applyEncoding }),
      { initialProps: { quality: STRAINED as PublishQuality | null } },
    );

    // Each rerender is one measurement; the publisher samples every two seconds.
    rerender({ quality: sample({ ...STRAINED, bitrateKbps: 2100 }) });
    rerender({ quality: sample({ ...STRAINED, bitrateKbps: 2000 }) });

    expect(applyEncoding).toHaveBeenCalledTimes(1);
    expect(applyEncoding.mock.calls[0]?.[0].scaleResolutionDownBy).toBeGreaterThan(1);
    expect(result.current.reduced).toBe(true);
    expect(result.current.notice).toContain("connection");
  });

  it("starts each broadcast at the top of the ladder", () => {
    // The bug this exists to prevent: a new broadcast gets a new publisher, which starts at full
    // quality with no limits. A ladder that carried its old position across would believe the
    // encoder was held down when it was not — and its first "climb" would apply a reduced rung to
    // a perfectly healthy stream.
    const applyEncoding = vi.fn<(limits: EncodingLimits) => Promise<void>>().mockResolvedValue();

    const { rerender, result } = renderHook(
      ({ enabled, quality }) => useAdaptiveQuality({ enabled, quality, applyEncoding }),
      { initialProps: { enabled: true, quality: STRAINED as PublishQuality | null } },
    );

    rerender({ enabled: true, quality: sample({ ...STRAINED, bitrateKbps: 2100 }) });
    rerender({ enabled: true, quality: sample({ ...STRAINED, bitrateKbps: 2000 }) });
    expect(result.current.reduced).toBe(true);

    // The broadcast stops, and a later one begins.
    rerender({ enabled: false, quality: null });
    rerender({ enabled: true, quality: sample() });

    expect(result.current.reduced).toBe(false);
    expect(result.current.rung.id).toBe("full");
  });

  it("puts the ladder back at the top when a rung is chosen by hand", async () => {
    const applyEncoding = vi.fn<(limits: EncodingLimits) => Promise<void>>().mockResolvedValue();

    const { rerender, result } = renderHook(
      ({ quality }) => useAdaptiveQuality({ enabled: true, quality, applyEncoding }),
      { initialProps: { quality: STRAINED as PublishQuality | null } },
    );

    rerender({ quality: sample({ ...STRAINED, bitrateKbps: 2100 }) });
    rerender({ quality: sample({ ...STRAINED, bitrateKbps: 2000 }) });
    expect(result.current.reduced).toBe(true);

    await act(async () => result.current.reset());

    // Their choice is about the camera; carrying "reduced" into a freshly chosen rung would
    // quietly halve what they asked for.
    expect(result.current.reduced).toBe(false);
    expect(result.current.notice).toBeNull();
    expect(applyEncoding.mock.calls.at(-1)?.[0].scaleResolutionDownBy).toBe(1);
  });

  it("survives a publisher that refuses the change", () => {
    // `applyEncoding` swallows its own failures, but a rejected promise must not surface as an
    // unhandled rejection either — the broadcast is fine, the rung simply did not apply.
    const applyEncoding = vi
      .fn<(limits: EncodingLimits) => Promise<void>>()
      .mockRejectedValue(new Error("setParameters refused"));

    const { rerender, result } = renderHook(
      ({ quality }) => useAdaptiveQuality({ enabled: true, quality, applyEncoding }),
      { initialProps: { quality: STRAINED as PublishQuality | null } },
    );

    expect(() => {
      rerender({ quality: sample({ ...STRAINED, bitrateKbps: 2100 }) });
      rerender({ quality: sample({ ...STRAINED, bitrateKbps: 2000 }) });
    }).not.toThrow();

    expect(result.current.reduced).toBe(true);
  });
});
