import { describe, expect, it } from "vitest";
import {
  type AdaptiveState,
  INITIAL_ADAPTIVE_STATE,
  MOBILE_LADDER,
  adapt,
  resetLadder,
} from "@/lib/media/adaptive";
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

/** Feeds a run of identical samples, returning the state and everything the ladder decided. */
function feed(
  state: AdaptiveState,
  measurement: PublishQuality | null,
  count: number,
  startMs = 0,
): { state: AdaptiveState; changes: string[] } {
  let current = state;
  const changes: string[] = [];

  for (let index = 0; index < count; index += 1) {
    // Samples arrive every two seconds, which is what the publisher's interval produces.
    const decision = adapt(current, measurement, startMs + index * 2000);
    current = decision.state;
    if (decision.change) changes.push(decision.change.rung.id);
  }

  return { state: current, changes };
}

describe("adapt", () => {
  it("does nothing while the broadcast is healthy", () => {
    const { changes } = feed(INITIAL_ADAPTIVE_STATE, sample(), 30);

    expect(changes).toEqual([]);
  });

  it("ignores a single bad sample", () => {
    // A lift, a doorway, one late receiver report. Reacting to these is how a ladder becomes a
    // thing operators disable.
    let state = INITIAL_ADAPTIVE_STATE;
    state = adapt(state, STRAINED, 0).state;
    const decision = adapt(state, sample(), 2000);

    expect(decision.change).toBeNull();
    expect(decision.state.index).toBe(0);
  });

  it("steps down after sustained strain", () => {
    const { state, changes } = feed(INITIAL_ADAPTIVE_STATE, STRAINED, 3);

    expect(changes).toEqual(["reduced"]);
    expect(state.index).toBe(1);
  });

  it("explains a CPU limit differently from a bandwidth one", () => {
    const cpu = feed(INITIAL_ADAPTIVE_STATE, sample({ limitation: "cpu", verdict: "fair" }), 2);
    const decision = adapt(cpu.state, sample({ limitation: "cpu", verdict: "fair" }), 4000);

    // The two have opposite remedies, so telling somebody to find better signal when their phone
    // is the bottleneck wastes the only minutes they have.
    expect(decision.change?.reason).toContain("encode");

    const bandwidth = feed(INITIAL_ADAPTIVE_STATE, STRAINED, 2);
    expect(adapt(bandwidth.state, STRAINED, 4000).change?.reason).toContain("connection");
  });

  it("holds still after a step down, however bad it looks", () => {
    const stepped = feed(INITIAL_ADAPTIVE_STATE, STRAINED, 3);

    // Immediately after a change the encoder has not yet had a chance to show what the new rung
    // can do. Stepping again on that evidence walks straight to the bottom of the ladder.
    const during = feed(stepped.state, STRAINED, 3, 2000);
    expect(during.changes).toEqual([]);

    const after = feed(stepped.state, STRAINED, 3, 10_000);
    expect(after.changes).toEqual(["low"]);
  });

  it("never falls off the bottom of the ladder", () => {
    const { state, changes } = feed(INITIAL_ADAPTIVE_STATE, STRAINED, 200);

    expect(state.index).toBe(MOBILE_LADDER.length - 1);
    expect(changes.at(-1)).toBe("minimum");
  });

  it("climbs back, but far more slowly than it fell", () => {
    const down = feed(INITIAL_ADAPTIVE_STATE, STRAINED, 3);

    // Three strained samples took it down; fifteen calm ones are needed to bring it back. The
    // asymmetry is what stops the ladder oscillating, which looks worse than sitting one rung low.
    const short = feed(down.state, sample(), 10, 10_000);
    expect(short.changes).toEqual([]);

    const long = feed(down.state, sample(), 16, 10_000);
    expect(long.changes).toEqual(["full"]);
  });

  it("does not climb past a rung the broadcaster pinned", () => {
    const state: AdaptiveState = { index: 2, strain: 0, calm: 0, holdUntilMs: 0 };

    let current = state;
    const changes: string[] = [];
    for (let index = 0; index < 60; index += 1) {
      const decision = adapt(current, sample(), index * 2000, { ceilingIndex: 1 });
      current = decision.state;
      if (decision.change) changes.push(decision.change.rung.id);
    }

    expect(changes).toEqual(["reduced"]);
    expect(current.index).toBe(1);
  });

  it("treats missing measurements as no evidence rather than bad news", () => {
    // Stats can go missing while a transport is rebuilt. A ladder that stepped down on silence
    // would reach the bottom during exactly the reconnect it was supposed to survive.
    const strained = feed(INITIAL_ADAPTIVE_STATE, STRAINED, 2);
    const blanked = feed(strained.state, null, 5, 4000);

    expect(blanked.changes).toEqual([]);
    expect(blanked.state.strain).toBe(0);
    expect(blanked.state.index).toBe(0);
  });

  it("does not count the first reading of a connection as calm", () => {
    // The first sample after publishing reports a zero bitrate, because a rate needs two readings.
    // Counting it would let the ladder climb on no evidence at all.
    const down = feed(INITIAL_ADAPTIVE_STATE, STRAINED, 3);
    const measuring = feed(down.state, sample({ bitrateKbps: 0 }), 20, 10_000);

    expect(measuring.changes).toEqual([]);
  });
});

describe("resetLadder", () => {
  it("returns to the top and holds briefly", () => {
    const reset = resetLadder(50_000);

    expect(reset.index).toBe(0);
    // A freshly re-opened camera at a different rung needs a moment before its measurements mean
    // anything.
    expect(reset.holdUntilMs).toBeGreaterThan(50_000);
  });
});
