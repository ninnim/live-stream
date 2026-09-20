import { describe, expect, it, vi } from "vitest";
import type { AudioContextLike, GainNodeLike, MediaStreamDestinationLike } from "@/lib/media/audio-mixer";
import {
  LocalRecorder,
  type LocalRecorderOptions,
  type RecordingSink,
  openRecordingSink,
  pickRecordingFormat,
  recordingFileName,
} from "@/lib/media/local-recording";

function track(kind: "video" | "audio", id: string = kind): MediaStreamTrack {
  const listeners = new Map<string, Set<() => void>>();

  return {
    kind,
    id,
    enabled: true,
    stop: vi.fn(),
    addEventListener: (type: string, handler: () => void) => {
      const existing = listeners.get(type) ?? new Set();
      existing.add(handler);
      listeners.set(type, existing);
    },
    removeEventListener: (type: string, handler: () => void) => listeners.get(type)?.delete(handler),
    emit: (type: string) => listeners.get(type)?.forEach((handler) => handler()),
  } as unknown as MediaStreamTrack & { emit(type: string): void };
}

/** A MediaRecorder stand-in: jsdom has none, and the interesting part is what it is handed. */
function fakeRecorder() {
  const instances: {
    stream: MediaStream;
    options: MediaRecorderOptions;
    state: string;
    timeslice: number | undefined;
    emit(data: Blob): void;
    fail(): void;
  }[] = [];

  const create = (stream: MediaStream, options: MediaRecorderOptions) => {
    const recorder = {
      stream,
      options,
      state: "inactive",
      timeslice: undefined as number | undefined,
      ondataavailable: null as ((event: BlobEvent) => void) | null,
      onerror: null as (() => void) | null,
      onstop: null as (() => void) | null,
      start(timeslice?: number) {
        this.state = "recording";
        this.timeslice = timeslice;
      },
      stop() {
        this.state = "inactive";
        this.onstop?.();
      },
      emit(data: Blob) {
        this.ondataavailable?.({ data } as BlobEvent);
      },
      fail() {
        this.onerror?.();
      },
    };

    instances.push(recorder as unknown as (typeof instances)[number]);
    return recorder as unknown as MediaRecorder;
  };

  return { create, instances };
}

function recordingSink() {
  const chunks: Blob[] = [];
  let closed = 0;
  let aborted = 0;

  const sink: RecordingSink = {
    buffersInMemory: false,
    write: async (chunk) => void chunks.push(chunk),
    close: async () => void (closed += 1),
    abort: async () => void (aborted += 1),
  };

  return {
    sink,
    chunks,
    get closed() {
      return closed;
    },
    get aborted() {
      return aborted;
    },
  };
}

/** Enough Web Audio for the mixer the recorder routes audio through. */
function fakeAudio() {
  const mixedTrack = { kind: "audio", id: "mixed" } as MediaStreamTrack;
  const sources: MediaStream[] = [];

  const context: AudioContextLike = {
    state: "running",
    createMediaStreamSource(stream) {
      sources.push(stream);
      return { connect: () => undefined, disconnect: () => undefined };
    },
    createGain(): GainNodeLike {
      return { gain: { value: 1 }, connect: () => undefined, disconnect: () => undefined };
    },
    createMediaStreamDestination: () =>
      ({
        stream: { getAudioTracks: () => [mixedTrack] },
        connect: () => undefined,
        disconnect: () => undefined,
      }) as unknown as MediaStreamDestinationLike,
    resume: async () => undefined,
    close: async () => undefined,
  };

  return { context, mixedTrack, sources };
}

const MP4 = "video/mp4;codecs=avc1.42E01E,mp4a.40.2";

describe("pickRecordingFormat", () => {
  it("prefers MP4, because that is what opens everywhere", () => {
    // A .webm is a file people have to go and find a player for; editors and phone galleries take
    // MP4 without a thought.
    const format = pickRecordingFormat(true, () => true);

    expect(format?.extension).toBe("mp4");
  });

  it("falls back to VP9 WebM before VP8", () => {
    // The gap between them is large on screen content, which is most of what gets recorded here.
    const format = pickRecordingFormat(true, (type) => type.includes("webm"));

    expect(format?.mimeType).toContain("vp9");
  });

  it("falls back again when only bare WebM is offered", () => {
    const format = pickRecordingFormat(true, (type) => type === "video/webm");

    expect(format).toMatchObject({ mimeType: "video/webm", extension: "webm" });
  });

  it("picks an audio container when there is no picture", () => {
    // Recording a microphone alone is a real thing to do, and wrapping it in a video container
    // would produce a file that plays as a black rectangle.
    const format = pickRecordingFormat(false, () => true);

    expect(format?.extension).toBe("m4a");
  });

  it("reports that a browser cannot record rather than guessing at a container", () => {
    expect(pickRecordingFormat(true, () => false)).toBeNull();
  });
});

describe("recordingFileName", () => {
  it("names a file after the session and when it was made", () => {
    const name = recordingFileName("Product Launch", "mp4", new Date(2026, 8, 12, 14, 30));

    expect(name).toBe("product-launch-2026-09-12-1430.mp4");
  });

  it("sorts by name into date order", () => {
    // Which is how a downloads folder is actually read.
    const first = recordingFileName("Demo", "mp4", new Date(2026, 8, 12, 9, 5));
    const second = recordingFileName("Demo", "mp4", new Date(2026, 8, 12, 14, 5));

    expect([second, first].sort()).toEqual([first, second]);
  });

  it("survives a title that is all punctuation", () => {
    expect(recordingFileName("!!! ???", "mp4", new Date(2026, 8, 12, 9, 5))).toBe(
      "recording-2026-09-12-0905.mp4",
    );
  });

  it("keeps a long title to a sensible length", () => {
    const name = recordingFileName("x".repeat(200), "webm", new Date(2026, 8, 12, 9, 5));

    expect(name.length).toBeLessThan(90);
  });
});

describe("LocalRecorder", () => {
  const format = { mimeType: MP4, extension: "mp4", label: "MP4 video" };

  function build(
    sink: RecordingSink,
    overrides: Partial<LocalRecorderOptions> = {},
  ) {
    const recorders = fakeRecorder();
    const audio = fakeAudio();

    const recorder = new LocalRecorder(sink, format, {
      createRecorder: recorders.create,
      mixer: { createContext: () => audio.context },
      ...overrides,
    });

    return { recorder, recorders, audio };
  }

  it("builds its own stream, which the studio cannot then modify", async () => {
    // The reason this class exists. A MediaRecorder whose stream gains or loses a track must
    // discard everything it has gathered — not stop, *discard* — and the studio swaps tracks
    // inside its preview stream on every camera change, screen share and microphone swap.
    //
    // Taking tracks rather than a stream is what makes that impossible to get wrong: there is no
    // stream for a caller to hand over and then mutate.
    const video = track("video");
    const sink = recordingSink();
    const { recorder, recorders } = build(sink.sink);

    recorder.start({ video, audio: track("audio") });

    const recorded = recorders.instances[0]!.stream;
    expect(recorded.getVideoTracks()).toEqual([video]);

    // A studio that now swaps its own camera builds a different stream entirely; this one is
    // untouched, and keeps exactly the track set it started with.
    expect(recorded.getTracks()).toHaveLength(2);
    await recorder.stop();
  });

  it("routes audio through the mixer, so a microphone swap is invisible", async () => {
    // The audio genuinely does change mid-recording in normal use: sharing a screen with sound
    // adds a source. The track handed to the recorder has to outlive that.
    const sink = recordingSink();
    const { recorder, recorders, audio } = build(sink.sink);

    recorder.start({ video: track("video"), audio: track("audio", "mic-1") });

    const recorded = recorders.instances[0]!.stream.getAudioTracks()[0];
    expect(recorded).toBe(audio.mixedTrack);

    recorder.setAudio(track("audio", "mic-2"));

    // Same track object throughout: only what feeds it changed.
    expect(recorders.instances[0]!.stream.getAudioTracks()[0]).toBe(audio.mixedTrack);
    await recorder.stop();
  });

  it("records the video source itself, at whatever it is producing", async () => {
    // Putting it through a canvas would make it survive a source change and cost the recording its
    // resolution and frame rate — which for a screen recording is the whole point of making one.
    const source = track("video");
    const sink = recordingSink();
    const { recorder, recorders } = build(sink.sink);

    recorder.start({ video: source, audio: null });

    expect(recorders.instances[0]!.stream.getVideoTracks()[0]).toBe(source);
    await recorder.stop();
  });

  it("writes chunks as they arrive rather than at the end", async () => {
    // Which is what keeps memory flat on a long recording, and what limits the loss if the browser
    // is killed outright.
    const sink = recordingSink();
    const { recorder, recorders } = build(sink.sink);

    recorder.start({ video: track("video"), audio: null });
    expect(recorders.instances[0]?.timeslice).toBeGreaterThan(0);

    recorders.instances[0]!.emit(new Blob(["aaa"]));
    recorders.instances[0]!.emit(new Blob(["bb"]));
    await Promise.resolve();

    expect(sink.chunks).toHaveLength(2);
    expect(recorder.bytesWritten).toBe(5);

    await recorder.stop();
  });

  it("ignores empty chunks", async () => {
    const sink = recordingSink();
    const { recorder, recorders } = build(sink.sink);

    recorder.start({ video: track("video"), audio: null });
    recorders.instances[0]!.emit(new Blob([]));

    expect(sink.chunks).toHaveLength(0);
    await recorder.stop();
  });

  it("keeps the file when the screen share ends mid-recording", async () => {
    // The one source change that cannot be hidden. Everything written up to that point is the
    // operator's, and reporting why matters more than pretending it did not happen.
    const source = track("video") as MediaStreamTrack & { emit(type: string): void };
    const sink = recordingSink();
    const stopped: string[] = [];
    const { recorder, recorders } = build(sink.sink, {
      onStopped: (reason) => stopped.push(reason),
    });

    recorder.start({ video: source, audio: null });
    recorders.instances[0]!.emit(new Blob(["recorded"]));
    await Promise.resolve();

    source.emit("ended");
    await vi.waitFor(() => expect(stopped).toEqual(["source-ended"]));

    expect(sink.closed).toBe(1);
    expect(sink.aborted).toBe(0);
    expect(sink.chunks).toHaveLength(1);
  });

  it("stops only once, however many things ask it to", async () => {
    // The source ending and the operator pressing Stop can happen in either order, and the second
    // must not discard a file the first has already closed.
    const source = track("video") as MediaStreamTrack & { emit(type: string): void };
    const sink = recordingSink();
    const { recorder } = build(sink.sink);

    recorder.start({ video: source, audio: null });

    source.emit("ended");
    await recorder.stop();
    await recorder.stop();

    expect(sink.closed).toBe(1);
  });

  it("abandons a file that never received a byte", async () => {
    // An empty recording is not worth leaving on somebody's disk to puzzle over.
    const sink = recordingSink();
    const { recorder, recorders } = build(sink.sink);

    recorder.start({ video: track("video"), audio: null });
    recorders.instances[0]!.fail();

    await vi.waitFor(() => expect(sink.aborted).toBe(1));
    expect(sink.closed).toBe(0);
  });

  it("keeps what was written when the recorder fails part-way", async () => {
    const sink = recordingSink();
    const { recorder, recorders } = build(sink.sink);

    recorder.start({ video: track("video"), audio: null });
    recorders.instances[0]!.emit(new Blob(["some real footage"]));
    await Promise.resolve();
    recorders.instances[0]!.fail();

    await vi.waitFor(() => expect(sink.closed).toBe(1));
    expect(sink.aborted).toBe(0);
  });

  it("records without Web Audio rather than refusing to record", async () => {
    // A browser that will not give us an AudioContext should still produce a file. It loses only
    // the ability to hide a device change.
    const source = track("audio");
    const sink = recordingSink();
    const recorders = fakeRecorder();

    const recorder = new LocalRecorder(sink.sink, format, {
      createRecorder: recorders.create,
      mixer: {
        createContext: () => {
          throw new Error("no Web Audio here");
        },
      },
    });

    recorder.start({ video: track("video"), audio: source });

    expect(recorders.instances[0]!.stream.getAudioTracks()[0]).toBe(source);
    await recorder.stop();
  });

  it("asks for a bitrate worth keeping", async () => {
    // A local recording has no network to fit inside, so sizing it like a stream would throw away
    // quality for nothing.
    const sink = recordingSink();
    const { recorder, recorders } = build(sink.sink);

    recorder.start({ video: track("video"), audio: track("audio") });

    expect(recorders.instances[0]!.options.videoBitsPerSecond).toBeGreaterThanOrEqual(8_000_000);
    expect(recorders.instances[0]!.options.mimeType).toBe(MP4);

    await recorder.stop();
  });
});

describe("openRecordingSink", () => {
  const format = { mimeType: "video/mp4", extension: "mp4", label: "MP4 video" };

  it("streams to a file the operator picked, where the browser allows it", async () => {
    const written: Blob[] = [];
    let closed = false;

    vi.stubGlobal("showSaveFilePicker", async () => ({
      createWritable: async () => ({
        write: async (data: Blob) => void written.push(data),
        close: async () => void (closed = true),
      }),
    }));

    const sink = await openRecordingSink("demo.mp4", format);
    await sink.write(new Blob(["a"]));
    await sink.close();

    expect(sink.buffersInMemory).toBe(false);
    expect(written).toHaveLength(1);
    expect(closed).toBe(true);

    vi.unstubAllGlobals();
  });

  it("reports a dismissed save dialog as a decision, not a failure", async () => {
    vi.stubGlobal("showSaveFilePicker", async () => {
      throw Object.assign(new Error("The user aborted a request."), { name: "AbortError" });
    });

    await expect(openRecordingSink("demo.mp4", format)).rejects.toMatchObject({
      name: "RecordingCancelled",
    });

    vi.unstubAllGlobals();
  });

  it("falls back to memory when the browser has no file picker", async () => {
    vi.stubGlobal("showSaveFilePicker", undefined);
    const delivered: { name: string; size: number }[] = [];

    const sink = await openRecordingSink("demo.mp4", format, (blob, name) =>
      delivered.push({ name, size: blob.size }),
    );

    expect(sink.buffersInMemory).toBe(true);

    await sink.write(new Blob(["abc"]));
    await sink.close();

    expect(delivered).toEqual([{ name: "demo.mp4", size: 3 }]);
    vi.unstubAllGlobals();
  });

  it("delivers nothing when an in-memory recording is abandoned", async () => {
    vi.stubGlobal("showSaveFilePicker", undefined);
    const delivered: string[] = [];

    const sink = await openRecordingSink("demo.mp4", format, (_blob, name) => delivered.push(name));
    await sink.write(new Blob(["abc"]));
    await sink.abort();
    await sink.close();

    expect(delivered).toEqual([]);
    vi.unstubAllGlobals();
  });
});
