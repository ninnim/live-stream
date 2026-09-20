import { vi } from "vitest";

/**
 * A global `AudioContext` for jsdom, which ships no Web Audio at all.
 *
 * The mixer takes an injectable context for its own unit tests. Component tests cannot inject
 * anything — the studio builds its mixer itself — so without this the mixer's constructor throws,
 * the studio quietly falls back to the microphone alone, and a test asserting that the two sources
 * are summed would pass for the wrong reason.
 */
export interface FakeWebAudio {
  /** Every gain node the graph created, in creation order. */
  gains: { gain: { value: number } }[];
  /** The single output track, which is what a mixing studio publishes. */
  mixedTrack: MediaStreamTrack;
  contexts: number;
  closed: number;
  /** What every analyser reports. Mutate it to change what the microphone is "hearing". */
  signal: number[];
}

export function installWebAudioStub(): FakeWebAudio {
  const state: FakeWebAudio = {
    gains: [],
    mixedTrack: { kind: "audio", id: "mixed-output", enabled: true, stop: vi.fn() } as unknown as MediaStreamTrack,
    contexts: 0,
    closed: 0,
    signal: [0],
  };

  class FakeAudioContext {
    state = "running";

    constructor() {
      state.contexts += 1;
    }

    createMediaStreamSource() {
      return { connect: () => undefined, disconnect: () => undefined };
    }

    /**
     * The studio's input meters read through this. It plays back whatever `signal` currently holds,
     * so a test can put a known level into the microphone and assert on what the studio says
     * about it.
     */
    createAnalyser() {
      return {
        fftSize: 1024,
        getFloatTimeDomainData(target: Float32Array) {
          for (let i = 0; i < target.length; i++) {
            target[i] = state.signal[i % state.signal.length] ?? 0;
          }
        },
        connect: () => undefined,
        disconnect: () => undefined,
      };
    }

    createGain() {
      const gain = {
        gain: { value: 1 },
        connect: () => undefined,
        disconnect: () => undefined,
      };
      state.gains.push(gain);
      return gain;
    }

    createMediaStreamDestination() {
      return {
        stream: { getAudioTracks: () => [state.mixedTrack] },
        connect: () => undefined,
        disconnect: () => undefined,
      };
    }

    async resume() {
      return undefined;
    }

    async close() {
      state.closed += 1;
    }
  }

  vi.stubGlobal("AudioContext", FakeAudioContext);
  return state;
}
