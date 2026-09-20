import { AudioMixer, type AudioMixerOptions } from "@/lib/media/audio-mixer";

/**
 * Recording the studio to a file on this computer, without broadcasting anything.
 *
 * Not every session is a show. Recording a walkthrough, a bug report or a lesson needs the same
 * capture, the same screen share and the same microphone handling as going live — and none of the
 * network, the platforms, or the audience.
 *
 * Nothing here touches the publish path. A recording is a second consumer of tracks the studio has
 * already opened, so starting or stopping one cannot disturb a broadcast that is also running.
 */

/** Video bitrate for a local recording. */
const VIDEO_BITRATE = 12_000_000;

/** Audio bitrate for a local recording. */
const AUDIO_BITRATE = 192_000;

/**
 * How often the recorder hands over a chunk.
 *
 * Chunks are written to disk as they arrive, so this is also how much work is lost if the browser
 * is killed outright. One second is frequent enough to keep memory flat on a long recording and
 * infrequent enough not to fragment the file.
 */
const CHUNK_MS = 1000;

export interface RecordingFormat {
  mimeType: string;
  extension: string;
  /** What to call it in front of somebody choosing where to save. */
  label: string;
}

/**
 * Candidates, best first.
 *
 * MP4 wins where it is offered: it opens in everything, including the editors and the phone
 * galleries a recording actually ends up in, where a `.webm` is a file people have to go and find
 * a player for. Chrome only gained MP4 recording recently, so WebM remains the fallback rather
 * than the assumption — and VP9 before VP8, since the difference on screen content is large.
 */
const FORMATS: RecordingFormat[] = [
  { mimeType: "video/mp4;codecs=avc1.42E01E,mp4a.40.2", extension: "mp4", label: "MP4 video" },
  { mimeType: "video/mp4", extension: "mp4", label: "MP4 video" },
  { mimeType: "video/webm;codecs=vp9,opus", extension: "webm", label: "WebM video" },
  { mimeType: "video/webm;codecs=vp8,opus", extension: "webm", label: "WebM video" },
  { mimeType: "video/webm", extension: "webm", label: "WebM video" },
];

/** Used when there is no camera and no screen — recording a microphone alone is a real thing to do. */
const AUDIO_ONLY_FORMATS: RecordingFormat[] = [
  { mimeType: "audio/mp4", extension: "m4a", label: "M4A audio" },
  { mimeType: "audio/webm;codecs=opus", extension: "webm", label: "WebM audio" },
  { mimeType: "audio/webm", extension: "webm", label: "WebM audio" },
];

export type SupportCheck = (mimeType: string) => boolean;

const defaultSupportCheck: SupportCheck = (mimeType) =>
  typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(mimeType);

/**
 * The best container this browser will actually produce.
 *
 * Returns null when none of them are supported, which is a browser that cannot record at all
 * rather than one that should be handed a guess.
 */
export function pickRecordingFormat(
  hasVideo: boolean,
  isSupported: SupportCheck = defaultSupportCheck,
): RecordingFormat | null {
  const candidates = hasVideo ? FORMATS : AUDIO_ONLY_FORMATS;

  for (const format of candidates) {
    if (isSupported(format.mimeType)) return format;
  }

  return null;
}

/**
 * A file name somebody can find again.
 *
 * Dated, because the second recording of the same thing is the common case, and sorted by name is
 * how a downloads folder is read.
 */
export function recordingFileName(title: string, extension: string, at: Date = new Date()): string {
  const stamp = [
    at.getFullYear(),
    String(at.getMonth() + 1).padStart(2, "0"),
    String(at.getDate()).padStart(2, "0"),
  ].join("-");

  const time = [
    String(at.getHours()).padStart(2, "0"),
    String(at.getMinutes()).padStart(2, "0"),
  ].join("");

  const slug =
    title
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, 60) || "recording";

  return `${slug}-${stamp}-${time}.${extension}`;
}

/**
 * Where the bytes go.
 *
 * Abstracted because the two available answers are very different. Chrome and Edge can stream
 * straight to a file the operator picked, which keeps memory flat however long the recording runs.
 * Everywhere else the only option is to hold the whole thing in memory and hand it over at the end,
 * which is fine for a few minutes and is not fine for an hour of 1080p.
 */
export interface RecordingSink {
  write(chunk: Blob): Promise<void>;
  /** Finishes the file. Returns nothing: by this point the bytes are the operator's, not ours. */
  close(): Promise<void>;
  /** Abandons it. Best-effort — a partial file left on disk is better than a thrown error. */
  abort(): Promise<void>;
  /** Whether memory grows with the recording's length, which the studio warns about. */
  readonly buffersInMemory: boolean;
}

interface FileSystemWritableLike {
  write(data: Blob): Promise<void>;
  close(): Promise<void>;
}

interface FileHandleLike {
  createWritable(): Promise<FileSystemWritableLike>;
}

interface SavePickerOptions {
  suggestedName?: string;
  types?: { description: string; accept: Record<string, string[]> }[];
}

type SavePicker = (options: SavePickerOptions) => Promise<FileHandleLike>;

function savePicker(): SavePicker | null {
  const picker = (globalThis as { showSaveFilePicker?: SavePicker }).showSaveFilePicker;
  return typeof picker === "function" ? picker.bind(globalThis) : null;
}

/** Raised when the operator dismisses the save dialog. Not a failure — they changed their mind. */
export class RecordingCancelled extends Error {
  constructor() {
    super("Saving was cancelled.");
    this.name = "RecordingCancelled";
  }
}

/**
 * Opens a sink, asking where to save if the browser allows it.
 *
 * Must be called from a user gesture: the file picker will not open otherwise, which is the reason
 * the destination is chosen when Record is pressed rather than when Stop is.
 */
export async function openRecordingSink(
  fileName: string,
  format: RecordingFormat,
  deliver: (blob: Blob, name: string) => void = downloadBlob,
): Promise<RecordingSink> {
  const picker = savePicker();

  if (picker) {
    let handle: FileHandleLike;

    try {
      handle = await picker({
        suggestedName: fileName,
        types: [{ description: format.label, accept: { [format.mimeType.split(";")[0]!]: [`.${format.extension}`] } }],
      });
    } catch (error) {
      // AbortError is the operator pressing Cancel. Anything else means the picker is unusable,
      // and falling back to an in-memory recording is far better than refusing to record.
      if (error instanceof Error && error.name === "AbortError") {
        throw new RecordingCancelled();
      }

      return memorySink(fileName, deliver);
    }

    const writable = await handle.createWritable();

    return {
      buffersInMemory: false,
      write: (chunk) => writable.write(chunk),
      close: () => writable.close(),
      abort: async () => {
        try {
          await writable.close();
        } catch {
          // Nothing to do: the partial file either landed or it did not.
        }
      },
    };
  }

  return memorySink(fileName, deliver);
}

function memorySink(
  fileName: string,
  deliver: (blob: Blob, name: string) => void,
): RecordingSink {
  const chunks: Blob[] = [];

  return {
    buffersInMemory: true,
    write: async (chunk) => void chunks.push(chunk),
    close: async () => {
      if (chunks.length > 0) deliver(new Blob(chunks), fileName);
      chunks.length = 0;
    },
    abort: async () => {
      chunks.length = 0;
    },
  };
}

function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");

  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();

  // Revoked on the next turn: revoking synchronously races the download starting in some browsers.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

export type RecordingStopReason = "requested" | "source-ended" | "error";

export interface LocalRecorderHandlers {
  /** Called as the file grows, so the studio can show what has been written. */
  onProgress?: (bytes: number) => void;
  onStopped?: (reason: RecordingStopReason, message: string | null) => void;
}

export interface LocalRecorderOptions extends LocalRecorderHandlers {
  mixer?: AudioMixerOptions;
  createRecorder?: (stream: MediaStream, options: MediaRecorderOptions) => MediaRecorder;
}

export interface RecordingSources {
  video: MediaStreamTrack | null;
  audio: MediaStreamTrack | null;
}

/**
 * Runs one recording.
 *
 * Two things here exist to survive the studio being used normally while it records.
 *
 * The recorder is handed **its own `MediaStream`**, never the studio's. A `MediaRecorder` whose
 * stream gains or loses a track is required to discard everything it has gathered — not stop, not
 * truncate, *discard* — and the studio swaps tracks inside its preview stream constantly: every
 * camera change, every screen share, every microphone swap. Sharing that stream would mean an hour
 * of recording vanishing because somebody changed camera.
 *
 * Audio goes through the **mixer**, so the one audio track handed to the recorder stays the same
 * object while the sources feeding it change. That matters because the audio genuinely does change
 * mid-recording in normal use: sharing a screen with sound adds a source to the mix.
 *
 * Video is the source track itself, at whatever the display or camera is producing. Putting it
 * through a canvas would make it survive a source change too, and would cost the recording its
 * resolution and frame rate — which for a screen recording is the entire point of making one.
 * A video source that ends instead stops the recording, keeping every byte written so far.
 */
export class LocalRecorder {
  private readonly options: LocalRecorderOptions;
  private readonly sink: RecordingSink;
  private readonly format: RecordingFormat;

  private recorder: MediaRecorder | null = null;
  private mixer: AudioMixer | null = null;
  private stream: MediaStream | null = null;
  private videoTrack: MediaStreamTrack | null = null;
  private bytes = 0;
  private stopping = false;
  private finished = false;

  private readonly onVideoEnded = (): void => {
    void this.stop("source-ended");
  };

  constructor(sink: RecordingSink, format: RecordingFormat, options: LocalRecorderOptions = {}) {
    this.sink = sink;
    this.format = format;
    this.options = options;
  }

  get bytesWritten(): number {
    return this.bytes;
  }

  get isRecording(): boolean {
    return this.recorder !== null && !this.finished;
  }

  start(sources: RecordingSources): void {
    const stream = new MediaStream();
    this.stream = stream;

    if (sources.video) {
      this.videoTrack = sources.video;
      stream.addTrack(sources.video);
      sources.video.addEventListener("ended", this.onVideoEnded);
    }

    if (sources.audio) {
      // Through the mixer even for a single source: its output track is what makes a microphone
      // change or a screen's sound joining invisible to the recording.
      try {
        this.mixer = new AudioMixer(this.options.mixer);
        this.mixer.setSources([{ id: "program", track: sources.audio }]);
        void this.mixer.resume();

        const mixed = this.mixer.track;
        if (mixed) stream.addTrack(mixed);
      } catch {
        // No Web Audio. Record the track directly and accept that a device change will end the
        // recording — far better than refusing to record at all.
        this.mixer = null;
        stream.addTrack(sources.audio);
      }
    }

    const recorder = (this.options.createRecorder ?? defaultRecorder)(stream, {
      mimeType: this.format.mimeType,
      videoBitsPerSecond: VIDEO_BITRATE,
      audioBitsPerSecond: AUDIO_BITRATE,
    });

    this.recorder = recorder;

    recorder.ondataavailable = (event: BlobEvent) => {
      if (!event.data || event.data.size === 0) return;

      this.bytes += event.data.size;
      this.options.onProgress?.(this.bytes);

      // Not awaited: this fires from the recorder and must not block it. Writes are serialised by
      // the sink's own queue.
      void this.sink.write(event.data).catch(() => void this.stop("error"));
    };

    recorder.onerror = () => void this.stop("error");

    recorder.start(CHUNK_MS);
  }

  /** Keeps the audio mix current. Called whenever the studio's published audio changes. */
  setAudio(track: MediaStreamTrack | null): void {
    if (!this.mixer) return;

    this.mixer.setSources(track ? [{ id: "program", track }] : []);
    void this.mixer.resume();
  }

  /**
   * Ends the recording and finishes the file.
   *
   * Safe to call twice: a source ending and the operator pressing Stop can happen in either order,
   * and the second one must not throw away a file the first one has already closed.
   */
  async stop(reason: RecordingStopReason = "requested"): Promise<void> {
    if (this.stopping || this.finished) return;
    this.stopping = true;

    this.videoTrack?.removeEventListener("ended", this.onVideoEnded);

    const recorder = this.recorder;

    if (recorder && recorder.state !== "inactive") {
      await new Promise<void>((resolve) => {
        // `stop` fires after the final `dataavailable`, so waiting for it is what guarantees the
        // last chunk reaches the file.
        recorder.onstop = () => resolve();

        try {
          recorder.stop();
        } catch {
          resolve();
        }
      });
    }

    // The preview stream owns these tracks and goes on using them; only the mixer is ours.
    this.stream = null;
    await this.mixer?.dispose();
    this.mixer = null;

    try {
      if (reason === "error" && this.bytes === 0) {
        await this.sink.abort();
      } else {
        await this.sink.close();
      }
    } finally {
      this.finished = true;
      this.recorder = null;
      this.options.onStopped?.(reason, MESSAGES[reason]);
    }
  }
}

const MESSAGES: Record<RecordingStopReason, string | null> = {
  requested: null,
  "source-ended":
    "Recording stopped because the screen share or camera ended. Everything up to that point was saved.",
  error: "Recording stopped because of a problem. Anything already written was saved.",
};

function defaultRecorder(stream: MediaStream, options: MediaRecorderOptions): MediaRecorder {
  return new MediaRecorder(stream, options);
}
