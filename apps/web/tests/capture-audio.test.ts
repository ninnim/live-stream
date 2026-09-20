import { describe, expect, it, vi } from "vitest";
import type {
  AudioContextLike,
  GainNodeLike,
  MediaStreamDestinationLike,
} from "@/lib/media/audio-mixer";
import {
  CaptureAudio,
  DEFAULT_CAPTURE_LEVELS,
  MICROPHONE_SOURCE,
  SCREEN_SOURCE,
  planCaptureAudio,
} from "@/lib/media/capture-audio";

function audioTrack(id: string): MediaStreamTrack {
  return { kind: "audio", id, stop: vi.fn() } as unknown as MediaStreamTrack;
}

/**
 * jsdom has no Web Audio. This records only what these tests assert on: whether a context was
 * built at all, what gains were set, and whether it was closed.
 */
function fakeContext() {
  const gains: GainNodeLike[] = [];
  const sources: MediaStream[] = [];
  let closed = false;

  const mixedTrack = { kind: "audio", id: "mixed" } as MediaStreamTrack;
  const destination = {
    stream: { getAudioTracks: () => [mixedTrack] },
    connect: () => undefined,
    disconnect: () => undefined,
  } as unknown as MediaStreamDestinationLike;

  const context: AudioContextLike = {
    state: "suspended",
    createMediaStreamSource(stream) {
      sources.push(stream);
      return { connect: () => undefined, disconnect: () => undefined };
    },
    createGain() {
      const gain: GainNodeLike = {
        gain: { value: 1 },
        connect: () => undefined,
        disconnect: () => undefined,
      };
      gains.push(gain);
      return gain;
    },
    createMediaStreamDestination: () => destination,
    resume: async () => undefined,
    close: async () => {
      closed = true;
    },
  };

  return {
    context,
    gains,
    sources,
    mixedTrack,
    get closed() {
      return closed;
    },
  };
}

describe("planCaptureAudio", () => {
  it("sums both when a game and a voice are open", () => {
    const microphone = audioTrack("mic");
    const screen = audioTrack("screen");

    const plan = planCaptureAudio({ microphone, screen });

    expect(plan).toMatchObject({ kind: "mixed" });
    if (plan.kind !== "mixed") throw new Error("unreachable");

    expect(plan.sources).toEqual([
      { id: MICROPHONE_SOURCE, track: microphone, gain: DEFAULT_CAPTURE_LEVELS.microphone },
      { id: SCREEN_SOURCE, track: screen, gain: DEFAULT_CAPTURE_LEVELS.screen },
    ]);
  });

  it("starts the screen below the voice, so a game does not bury the person playing it", () => {
    expect(DEFAULT_CAPTURE_LEVELS.screen).toBeLessThan(DEFAULT_CAPTURE_LEVELS.microphone);
  });

  it("publishes a lone microphone untouched", () => {
    // A Web Audio graph in the path costs CPU, adds latency, and can be suspended into perfect
    // silence. None of that is worth paying for a mix of one.
    const microphone = audioTrack("mic");

    expect(planCaptureAudio({ microphone, screen: null })).toEqual({
      kind: "microphone",
      track: microphone,
    });
  });

  it("publishes a lone screen untouched, because a machine with no mic still has a broadcast", () => {
    const screen = audioTrack("screen");

    expect(planCaptureAudio({ microphone: null, screen })).toEqual({ kind: "screen", track: screen });
  });

  it("reports nothing to publish when neither is open", () => {
    expect(planCaptureAudio({ microphone: null, screen: null })).toEqual({ kind: "none" });
  });

  it("carries the levels it is given into the mix", () => {
    const plan = planCaptureAudio(
      { microphone: audioTrack("mic"), screen: audioTrack("screen") },
      { microphone: 0.8, screen: 0.2 },
    );

    if (plan.kind !== "mixed") throw new Error("unreachable");
    expect(plan.sources.map((source) => source.gain)).toEqual([0.8, 0.2]);
  });
});

describe("CaptureAudio", () => {
  it("builds no audio graph for a single source", async () => {
    const harness = fakeContext();
    const capture = new CaptureAudio({ createContext: () => harness.context });
    const microphone = audioTrack("mic");

    const published = await capture.applyAsync({ kind: "microphone", track: microphone });

    expect(published).toBe(microphone);
    expect(capture.isMixing).toBe(false);
    expect(harness.gains).toHaveLength(0);
  });

  it("publishes the mix once there are two sources", async () => {
    const harness = fakeContext();
    const capture = new CaptureAudio({ createContext: () => harness.context });

    const published = await capture.applyAsync({
      kind: "mixed",
      sources: [
        { id: MICROPHONE_SOURCE, track: audioTrack("mic"), gain: 1 },
        { id: SCREEN_SOURCE, track: audioTrack("screen"), gain: 0.7 },
      ],
    });

    expect(published).toBe(harness.mixedTrack);
    expect(capture.isMixing).toBe(true);
    expect(harness.gains.map((gain) => gain.gain.value)).toEqual([1, 0.7]);
  });

  it("keeps the same published track while a level moves", async () => {
    // Swapping the published audio track is audible, and doing it on every slider tick would be
    // absurd. Moving a level has to reach a gain node inside a graph that stays put.
    const harness = fakeContext();
    const capture = new CaptureAudio({ createContext: () => harness.context });
    const microphone = audioTrack("mic");
    const screen = audioTrack("screen");

    const first = await capture.applyAsync({
      kind: "mixed",
      sources: [
        { id: MICROPHONE_SOURCE, track: microphone, gain: 1 },
        { id: SCREEN_SOURCE, track: screen, gain: 0.7 },
      ],
    });

    const second = await capture.applyAsync({
      kind: "mixed",
      sources: [
        { id: MICROPHONE_SOURCE, track: microphone, gain: 1 },
        { id: SCREEN_SOURCE, track: screen, gain: 0.2 },
      ],
    });

    expect(second).toBe(first);
    expect(harness.gains.at(-1)?.gain.value).toBe(0.2);
  });

  it("tears the graph down when the mix drops back to one source", async () => {
    const harness = fakeContext();
    const capture = new CaptureAudio({ createContext: () => harness.context });
    const microphone = audioTrack("mic");

    await capture.applyAsync({
      kind: "mixed",
      sources: [
        { id: MICROPHONE_SOURCE, track: microphone, gain: 1 },
        { id: SCREEN_SOURCE, track: audioTrack("screen"), gain: 0.7 },
      ],
    });

    const published = await capture.applyAsync({ kind: "microphone", track: microphone });

    expect(published).toBe(microphone);
    expect(harness.closed).toBe(true);
  });

  it("falls back to the microphone when a graph cannot be built at all", async () => {
    // A browser that will not give us an AudioContext should still broadcast the voice rather
    // than fail on a mixer it only needed for a nicety.
    const microphone = audioTrack("mic");
    const capture = new CaptureAudio({
      createContext: () => {
        throw new Error("no Web Audio here");
      },
    });

    const published = await capture.applyAsync({
      kind: "mixed",
      sources: [
        { id: MICROPHONE_SOURCE, track: microphone, gain: 1 },
        { id: SCREEN_SOURCE, track: audioTrack("screen"), gain: 0.7 },
      ],
    });

    expect(published).toBe(microphone);
    expect(capture.isMixing).toBe(false);
  });

  it("closes the context on disposal, so a torn-down studio leaves no live graph behind", async () => {
    const harness = fakeContext();
    const capture = new CaptureAudio({ createContext: () => harness.context });

    await capture.applyAsync({
      kind: "mixed",
      sources: [
        { id: MICROPHONE_SOURCE, track: audioTrack("mic"), gain: 1 },
        { id: SCREEN_SOURCE, track: audioTrack("screen"), gain: 0.7 },
      ],
    });

    await capture.disposeAsync();

    expect(harness.closed).toBe(true);
    expect(capture.track).toBeNull();
  });
});
