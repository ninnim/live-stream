import { describe, expect, it, vi } from "vitest";
import {
  type AudioContextLike,
  AudioMixer,
  type GainNodeLike,
  type MediaStreamDestinationLike,
} from "@/lib/media/audio-mixer";

/**
 * jsdom has no Web Audio. This records the graph the mixer builds, which is the thing worth
 * asserting — that sources are connected once, disconnected when they leave, and never doubled.
 */
function fakeContext() {
  const connections: { from: string; to: string }[] = [];
  const created: { streams: MediaStream[]; gains: GainNodeLike[] } = { streams: [], gains: [] };
  let nodeId = 0;
  let closed = false;
  let resumed = 0;

  // One track object for the fake's whole life. A fresh one per call would make the stability
  // assertion pass or fail on the fixture rather than on the mixer.
  const mixedTrack = { kind: "audio", id: "mixed" } as MediaStreamTrack;

  const destination = {
    stream: { getAudioTracks: () => [mixedTrack] },
    connect: () => undefined,
    disconnect: () => undefined,
  } as unknown as MediaStreamDestinationLike;

  const context: AudioContextLike = {
    state: "suspended",
    createMediaStreamSource(stream) {
      created.streams.push(stream);
      const id = `source-${(nodeId += 1)}`;
      return {
        connect: (to) => connections.push({ from: id, to: (to as { id?: string }).id ?? "gain" }),
        disconnect: () => connections.splice(0, connections.length, ...connections.filter((c) => c.from !== id)),
      };
    },
    createGain() {
      const id = `gain-${(nodeId += 1)}`;
      const gain: GainNodeLike = {
        gain: { value: 1 },
        connect: (to) => connections.push({ from: id, to: (to as { id?: string }).id ?? "destination" }),
        disconnect: () =>
          connections.splice(0, connections.length, ...connections.filter((c) => c.from !== id)),
      };
      created.gains.push(gain);
      return gain;
    },
    createMediaStreamDestination: () => destination,
    resume: async () => {
      resumed += 1;
    },
    close: async () => {
      closed = true;
    },
  };

  return {
    context,
    connections,
    created,
    get closed() {
      return closed;
    },
    get resumed() {
      return resumed;
    },
  };
}

function audioTrack(id: string): MediaStreamTrack {
  return { kind: "audio", id, stop: vi.fn() } as unknown as MediaStreamTrack;
}

describe("AudioMixer", () => {
  it("exposes one stable output track", () => {
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    const first = mixer.track;
    mixer.setSources([{ id: "a", track: audioTrack("a") }]);

    // Stability is the point: the studio publishes this track, and every later change to who is
    // audible happens inside the graph rather than by swapping what is being sent.
    expect(mixer.track).toBe(first);
  });

  it("connects each source once", () => {
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    mixer.setSources([
      { id: "studio", track: audioTrack("studio") },
      { id: "guest", track: audioTrack("guest") },
    ]);

    expect(mixer.sourceCount).toBe(2);
    expect(harness.created.gains).toHaveLength(2);
  });

  it("does not rebuild a source that has not changed", () => {
    // Re-creating a source node produces a click, and this runs on every React render.
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });
    const track = audioTrack("studio");

    mixer.setSources([{ id: "studio", track }]);
    mixer.setSources([{ id: "studio", track }]);
    mixer.setSources([{ id: "studio", track }]);

    expect(harness.created.gains).toHaveLength(1);
  });

  it("follows a changed microphone rather than staying on the old track", () => {
    // Keying on the stream would leave the graph attached to a track that has been stopped, and
    // the studio would go silent with nothing reporting an error.
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    mixer.setSources([{ id: "studio", track: audioTrack("built-in") }]);
    mixer.setSources([{ id: "studio", track: audioTrack("usb-interface") }]);

    expect(harness.created.gains).toHaveLength(2);
    // Still one input: the replacement took the old one's place rather than joining it.
    expect(mixer.sourceCount).toBe(1);
  });

  it("removes a source that is no longer audible", () => {
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    mixer.setSources([
      { id: "studio", track: audioTrack("studio") },
      { id: "guest", track: audioTrack("guest") },
    ]);
    mixer.setSources([{ id: "studio", track: audioTrack("studio") }]);

    expect(mixer.sourceCount).toBe(1);
  });

  it("silences a muted source without disconnecting it", () => {
    // Disconnecting and reconnecting on every mute would click; a gain of zero does not.
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });
    const track = audioTrack("guest");

    mixer.setSources([{ id: "guest", track }]);
    mixer.setSources([{ id: "guest", track, muted: true }]);

    expect(mixer.sourceCount).toBe(1);
    expect(harness.created.gains).toHaveLength(1);
    expect(harness.created.gains[0]?.gain.value).toBe(0);
  });

  it("clamps a nonsensical gain instead of amplifying into clipping", () => {
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    mixer.setSources([{ id: "guest", track: audioTrack("guest"), gain: 9 }]);
    expect(harness.created.gains[0]?.gain.value).toBe(1);

    mixer.setSources([{ id: "guest", track: audioTrack("other"), gain: Number.NaN }]);
    expect(harness.created.gains[1]?.gain.value).toBe(1);
  });

  it("ignores a video track handed to it by mistake", () => {
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    mixer.setSources([{ id: "camera", track: { kind: "video" } as MediaStreamTrack }]);

    expect(mixer.sourceCount).toBe(0);
  });

  it("resumes a suspended context, because a suspended one is silent with no error", () => {
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    return mixer.resume().then(() => expect(harness.resumed).toBe(1));
  });

  it("closes the context on dispose", async () => {
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    mixer.setSources([{ id: "studio", track: audioTrack("studio") }]);
    await mixer.dispose();

    expect(harness.closed).toBe(true);
    expect(mixer.sourceCount).toBe(0);
  });

  it("ignores work after disposal", async () => {
    const harness = fakeContext();
    const mixer = new AudioMixer({ createContext: () => harness.context });

    await mixer.dispose();
    mixer.setSources([{ id: "studio", track: audioTrack("studio") }]);

    expect(mixer.sourceCount).toBe(0);
  });
});
