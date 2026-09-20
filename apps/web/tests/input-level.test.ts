import { describe, expect, it, vi } from "vitest";
import {
  type AnalyserLike,
  type LevelContextLike,
  LevelMonitor,
  type LevelNodeLike,
  describeLevel,
} from "@/lib/media/input-level";

function audioTrack(id: string): MediaStreamTrack {
  return { kind: "audio", id, stop: vi.fn() } as unknown as MediaStreamTrack;
}

/**
 * jsdom has no Web Audio. This plays back a fixed waveform, so the arithmetic the meter does can be
 * checked against a signal whose level is known rather than against whatever a real device produced.
 */
function fakeContext(initialSignal: number[] = []) {
  const connections: string[] = [];
  const disconnected: string[] = [];
  let signal = initialSignal;
  let nodes = 0;
  let closed = false;

  const context: LevelContextLike = {
    state: "running",
    createMediaStreamSource(): LevelNodeLike {
      const id = `source-${(nodes += 1)}`;
      return {
        connect: () => connections.push(id),
        disconnect: () => disconnected.push(id),
      };
    },
    createAnalyser(): AnalyserLike {
      const id = `analyser-${(nodes += 1)}`;
      return {
        fftSize: 2048,
        getFloatTimeDomainData(target: Float32Array) {
          for (let i = 0; i < target.length; i++) {
            target[i] = signal[i % Math.max(signal.length, 1)] ?? 0;
          }
        },
        connect: () => connections.push(id),
        disconnect: () => disconnected.push(id),
      };
    },
    resume: async () => undefined,
    close: async () => {
      closed = true;
    },
  };

  return {
    context,
    connections,
    disconnected,
    /** Changes what the microphone is producing, without touching the graph reading it. */
    play(next: number[]) {
      signal = next;
    },
    get closed() {
      return closed;
    },
  };
}

/** Reads repeatedly, because the meter smooths — one sample is deliberately not the whole answer. */
function settle(monitor: LevelMonitor, id: string, reads = 40) {
  let level = monitor.read(id);
  for (let i = 1; i < reads; i++) level = monitor.read(id);
  return level;
}

describe("describeLevel", () => {
  it("calls a clipping signal too loud, whatever its average", () => {
    // Judged on peak, not RMS. A quiet passage with one sample slamming into the ceiling is still
    // distorting, and averaging would hide exactly the moment worth reporting.
    expect(describeLevel(0.05, 1)).toBe("loud");
  });

  it("calls a healthy speaking level good", () => {
    // About -18 dBFS RMS, which is where a voice should sit.
    expect(describeLevel(0.125, 0.4)).toBe("good");
  });

  it("calls a faint signal quiet rather than silent", () => {
    // The distinction matters: quiet means move closer, silent means the device is not working.
    expect(describeLevel(0.008, 0.02)).toBe("quiet");
  });

  it("calls a dead input silent", () => {
    expect(describeLevel(0, 0)).toBe("silent");
  });

  it("does not call a pause between words silent", () => {
    // Room tone sits above the silence floor. Reporting "no sound" every time somebody drew breath
    // would make the meter worse than none at all.
    expect(describeLevel(0.003, 0.01)).toBe("quiet");
  });
});

describe("LevelMonitor", () => {
  it("measures a steady tone at its real level", () => {
    // A square wave at ±0.5 has an RMS of exactly 0.5, so the reading is checkable rather than
    // merely plausible.
    const harness = fakeContext([0.5, -0.5]);
    const monitor = new LevelMonitor({ createContext: () => harness.context });

    monitor.setTracks({ microphone: audioTrack("mic") });

    expect(settle(monitor, "microphone").rms).toBeCloseTo(0.5, 2);
  });

  it("reports silence for a track that is producing nothing", () => {
    const harness = fakeContext([0]);
    const monitor = new LevelMonitor({ createContext: () => harness.context });

    monitor.setTracks({ microphone: audioTrack("mic") });

    expect(settle(monitor, "microphone")).toMatchObject({ rms: 0, verdict: "silent" });
  });

  it("reports silence for an input it was never given", () => {
    const monitor = new LevelMonitor({ createContext: () => fakeContext().context });

    expect(monitor.read("microphone").verdict).toBe("silent");
  });

  it("lets the peak fall back so one loud moment does not pin it", () => {
    // The same input throughout — a swapped track would reset the peak and prove nothing.
    const harness = fakeContext([1, -1]);
    const monitor = new LevelMonitor({ createContext: () => harness.context });

    monitor.setTracks({ microphone: audioTrack("mic") });
    const loud = settle(monitor, "microphone", 5);
    expect(loud.peak).toBeCloseTo(1, 2);
    expect(loud.verdict).toBe("loud");

    // The broadcaster moves back and the signal drops. The peak has to fall with it, or the meter
    // reads "too loud" for the rest of the show and there is nothing they can do about it.
    harness.play([0.1, -0.1]);

    const after = settle(monitor, "microphone", 60);
    expect(after.peak).toBeLessThan(loud.peak);
    expect(after.verdict).toBe("good");
  });

  it("rebuilds the graph when the microphone is swapped", () => {
    // A source node binds to the track it was given and does not follow a stream's later changes,
    // so a changed device with the same id has to be reattached or the meter reads a stopped track.
    const harness = fakeContext([0.5, -0.5]);
    const monitor = new LevelMonitor({ createContext: () => harness.context });

    monitor.setTracks({ microphone: audioTrack("mic-1") });
    const connectedBefore = harness.connections.length;

    monitor.setTracks({ microphone: audioTrack("mic-2") });

    expect(harness.disconnected.length).toBeGreaterThan(0);
    expect(harness.connections.length).toBeGreaterThan(connectedBefore);
  });

  it("leaves an unchanged track attached", () => {
    // Reattaching per render would drop a reading every time any studio state changed.
    const harness = fakeContext([0.5, -0.5]);
    const monitor = new LevelMonitor({ createContext: () => harness.context });
    const microphone = audioTrack("mic");

    monitor.setTracks({ microphone });
    const connected = harness.connections.length;

    monitor.setTracks({ microphone });

    expect(harness.connections).toHaveLength(connected);
    expect(harness.disconnected).toHaveLength(0);
  });

  it("measures two sources independently", () => {
    const harness = fakeContext([0.5, -0.5]);
    const monitor = new LevelMonitor({ createContext: () => harness.context });

    monitor.setTracks({ microphone: audioTrack("mic"), screen: audioTrack("screen") });

    expect(settle(monitor, "microphone").verdict).toBe("good");
    expect(settle(monitor, "screen").verdict).toBe("good");
  });

  it("opens no audio context until there is something to measure", () => {
    // A studio that never opens a microphone should never open an AudioContext either.
    const createContext = vi.fn(() => fakeContext().context);
    const monitor = new LevelMonitor({ createContext });

    monitor.setTracks({ microphone: null });

    expect(createContext).not.toHaveBeenCalled();
  });

  it("gives up quietly when the browser will not build an audio graph", async () => {
    // No meter is a worse studio. A studio that fails to open is a worse product.
    const monitor = new LevelMonitor({
      createContext: () => {
        throw new Error("no Web Audio here");
      },
    });

    monitor.setTracks({ microphone: audioTrack("mic") });

    expect(monitor.read("microphone")).toMatchObject({ verdict: "silent" });
    await expect(monitor.disposeAsync()).resolves.toBeUndefined();
  });

  it("stops retrying a context that cannot be created", () => {
    // This runs on an animation frame. Retrying per frame would be absurd, and the failure is a
    // missing browser capability rather than a transient one.
    const createContext = vi.fn(() => {
      throw new Error("no Web Audio here");
    });
    const monitor = new LevelMonitor({ createContext });

    monitor.setTracks({ microphone: audioTrack("mic-1") });
    monitor.setTracks({ microphone: audioTrack("mic-2") });
    monitor.setTracks({ microphone: audioTrack("mic-3") });

    expect(createContext).toHaveBeenCalledTimes(1);
  });

  it("closes the context on disposal", () => {
    const harness = fakeContext([0.5]);
    const monitor = new LevelMonitor({ createContext: () => harness.context });

    monitor.setTracks({ microphone: audioTrack("mic") });
    void monitor.disposeAsync();

    expect(harness.disconnected.length).toBeGreaterThan(0);
  });

  it("survives an analyser that throws mid-reading", async () => {
    // It is read from an animation frame. One that threw would flood the console sixty times a
    // second and take the studio's render loop with it.
    const context: LevelContextLike = {
      ...fakeContext().context,
      createAnalyser: () => ({
        fftSize: 1024,
        getFloatTimeDomainData() {
          throw new Error("context closed underneath us");
        },
        connect: () => undefined,
        disconnect: () => undefined,
      }),
    };

    const monitor = new LevelMonitor({ createContext: () => context });
    monitor.setTracks({ microphone: audioTrack("mic") });

    expect(monitor.read("microphone").verdict).toBe("silent");
    await monitor.disposeAsync();
  });
});
