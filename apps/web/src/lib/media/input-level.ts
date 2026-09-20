/**
 * Shows the broadcaster what their microphone is actually picking up.
 *
 * The studio could report a microphone as "On" while it delivered perfect silence — a muted
 * interface, a headset docked on the wrong input, a gain knob at zero — and nothing anywhere would
 * say so. The broadcaster finds out when somebody watching tells them, which is far too late.
 *
 * This taps the signal to measure it and never sits in the path that carries it. An analyser is a
 * leaf: it reads what is connected to it and outputs nothing. So a fault here cannot make the
 * broadcast quieter, louder, or silent — the worst it can do is show a wrong number.
 */

/** The Web Audio surface this needs, so it can be tested without an AudioContext. */
export interface LevelNodeLike {
  connect(destination: LevelNodeLike): unknown;
  disconnect(): void;
}

export interface AnalyserLike extends LevelNodeLike {
  fftSize: number;
  getFloatTimeDomainData(target: Float32Array): void;
}

export interface LevelContextLike {
  readonly state: string;
  createMediaStreamSource(stream: MediaStream): LevelNodeLike;
  createAnalyser(): AnalyserLike;
  resume(): Promise<void>;
  close(): Promise<void>;
}

export type LevelVerdict = "silent" | "quiet" | "good" | "loud";

export interface InputLevel {
  /** Smoothed loudness, 0 to 1. What the bar shows. */
  rms: number;
  /** Recent maximum, 0 to 1, decaying. What clipping is judged on. */
  peak: number;
  verdict: LevelVerdict;
}

export const SILENT_LEVEL: InputLevel = { rms: 0, peak: 0, verdict: "silent" };

const decibels = (value: number): number => 10 ** (value / 20);

/**
 * Thresholds, in dBFS, chosen for a person speaking rather than for a mastering meter.
 *
 * `Loud` is judged on peak and the others on RMS, because they are different faults: clipping is a
 * momentary sample hitting the ceiling, while "too quiet" is a sustained average nobody can hear.
 * Judging both on the same number would either miss a clipping transient or call every pause silent.
 */
const CLIPPING = decibels(-1);
const TOO_QUIET = decibels(-34);
const NO_SIGNAL = decibels(-55);

/** How fast the peak indicator falls, per sample of the meter. Slow enough to be readable. */
const PEAK_DECAY = 0.92;

/** Weight of each new reading. Low enough that the bar does not flicker on every syllable. */
const RMS_SMOOTHING = 0.35;

export function describeLevel(rms: number, peak: number): LevelVerdict {
  // Checked first: a signal that is clipping is also loud by every other measure, and clipping is
  // the one the broadcaster has to act on.
  if (peak >= CLIPPING) return "loud";
  if (rms < NO_SIGNAL) return "silent";
  if (rms < TOO_QUIET) return "quiet";
  return "good";
}

/** What to tell the broadcaster, and whether it is worth telling them at all. */
export const LEVEL_ADVICE: Record<LevelVerdict, string | null> = {
  silent: "No sound is reaching this microphone. Check it is not muted on the device itself.",
  quiet: "Very quiet. Move closer, or raise the input level in your system sound settings.",
  good: null,
  loud: "Too loud — this will distort. Move back, or lower the input level.",
};

interface Input {
  track: MediaStreamTrack;
  source: LevelNodeLike;
  analyser: AnalyserLike;
  samples: Float32Array;
  rms: number;
  peak: number;
}

export interface LevelMonitorOptions {
  createContext?: () => LevelContextLike;
}

/**
 * Measures one or more capture tracks.
 *
 * Keyed on the track rather than on a stream, for the same reason the mixer is: a source node binds
 * to the track a stream holds at the moment it is created and does not follow later changes, so
 * keying on the stream would leave the meter reading a microphone that has been swapped out.
 */
export class LevelMonitor {
  private context: LevelContextLike | null = null;
  private readonly inputs = new Map<string, Input>();
  private readonly options: LevelMonitorOptions;
  private failed = false;

  constructor(options: LevelMonitorOptions = {}) {
    this.options = options;
  }

  /**
   * Reconciles against the tracks that should be measured. Null entries are simply dropped.
   *
   * Returns whether the graph changed, so a caller running this on every render does not have to
   * work that out for itself — comparing the track objects here is the only reliable way to know.
   */
  setTracks(tracks: Record<string, MediaStreamTrack | null>): boolean {
    let changed = false;

    for (const [id, track] of Object.entries(tracks)) {
      const existing = this.inputs.get(id);

      if (existing && existing.track === track) continue;
      if (existing) this.detach(id);
      if (track) this.attach(id, track);
      changed = true;
    }

    for (const id of [...this.inputs.keys()]) {
      if (!(id in tracks) || !tracks[id]) {
        this.detach(id);
        changed = true;
      }
    }

    return changed;
  }

  /**
   * Takes a reading. Called from an animation frame, so it must stay cheap and must never throw:
   * a meter that throws once per frame would flood the console and stall the studio.
   */
  read(id: string): InputLevel {
    const input = this.inputs.get(id);
    if (!input) return SILENT_LEVEL;

    try {
      input.analyser.getFloatTimeDomainData(input.samples);
    } catch {
      return SILENT_LEVEL;
    }

    let sum = 0;
    let max = 0;

    for (const sample of input.samples) {
      sum += sample * sample;
      const magnitude = Math.abs(sample);
      if (magnitude > max) max = magnitude;
    }

    const rms = Math.sqrt(sum / input.samples.length);

    input.rms += (rms - input.rms) * RMS_SMOOTHING;
    input.peak = Math.max(max, input.peak * PEAK_DECAY);

    return {
      rms: input.rms,
      peak: input.peak,
      verdict: describeLevel(input.rms, input.peak),
    };
  }

  /** Browsers start a context suspended. A suspended analyser reads a flat zero — a false silence. */
  async resume(): Promise<void> {
    if (!this.context || this.context.state === "running") return;

    try {
      await this.context.resume();
    } catch {
      // Nothing useful to do. The meter reads zero until the next gesture, which is a worse meter
      // but not a worse broadcast.
    }
  }

  async disposeAsync(): Promise<void> {
    for (const id of [...this.inputs.keys()]) {
      this.detach(id);
    }

    const context = this.context;
    this.context = null;

    try {
      await context?.close();
    } catch {
      // Already closed.
    }
  }

  private attach(id: string, track: MediaStreamTrack): void {
    const context = this.ensureContext();
    if (!context) return;

    try {
      const source = context.createMediaStreamSource(new MediaStream([track]));
      const analyser = context.createAnalyser();

      // 1024 samples is about 21 ms at 48 kHz: long enough for a stable reading, short enough that
      // the meter still moves with the voice rather than lagging behind it.
      analyser.fftSize = 1024;
      source.connect(analyser);

      this.inputs.set(id, {
        track,
        source,
        analyser,
        samples: new Float32Array(analyser.fftSize),
        rms: 0,
        peak: 0,
      });
    } catch {
      // A browser that will not build the graph gets no meter. It still broadcasts.
      this.failed = true;
    }
  }

  private detach(id: string): void {
    const input = this.inputs.get(id);
    if (!input) return;

    try {
      input.source.disconnect();
      input.analyser.disconnect();
    } catch {
      // Already detached.
    }

    this.inputs.delete(id);
  }

  /**
   * Built on first use and only once, so a studio that never opens a microphone never opens an
   * AudioContext either. A context that could not be created is not retried: the failure is a
   * missing browser capability, not a transient one, and retrying per frame would be absurd.
   */
  private ensureContext(): LevelContextLike | null {
    if (this.context || this.failed) return this.context;

    try {
      this.context =
        this.options.createContext?.() ?? (new AudioContext() as unknown as LevelContextLike);
    } catch {
      this.failed = true;
    }

    return this.context;
  }
}
