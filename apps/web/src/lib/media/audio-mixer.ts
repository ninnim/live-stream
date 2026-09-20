/**
 * Audio mixing for the program.
 *
 * Sums every contributing source into one outgoing track. Like the compositor, the output track is
 * created once and kept: sources joining, leaving, being muted or being cut to all happen inside
 * the graph rather than by replacing the track the studio is publishing. Swapping an audio track
 * mid-broadcast is audible, and doing it on every program change would make cutting sound broken.
 *
 * The routing policy — who is audible when — deliberately lives in the caller. This class only
 * knows how to sum and how to set a level.
 */

/** The Web Audio surface this class needs, so tests can supply one without an AudioContext. */
export interface AudioContextLike {
  readonly state: string;
  createMediaStreamSource(stream: MediaStream): AudioNodeLike;
  createGain(): GainNodeLike;
  createMediaStreamDestination(): MediaStreamDestinationLike;
  resume(): Promise<void>;
  close(): Promise<void>;
}

export interface AudioNodeLike {
  connect(destination: AudioNodeLike | MediaStreamDestinationLike): unknown;
  disconnect(): void;
}

export interface GainNodeLike extends AudioNodeLike {
  gain: { value: number };
}

export interface MediaStreamDestinationLike extends AudioNodeLike {
  stream: MediaStream;
}

export interface MixerSource {
  id: string;
  /**
   * The track carrying this source's audio.
   *
   * A track, not a stream. `createMediaStreamSource` binds to the track a stream holds at the
   * moment it is called and does not follow later changes — so keying on the stream means that
   * changing microphone mid-show leaves the graph attached to a track that has been stopped, and
   * the studio goes silent with nothing reporting an error.
   */
  track: MediaStreamTrack;
  muted?: boolean;
  /** 0 to 1. Defaults to unity — this is a mixer, not a normaliser. */
  gain?: number;
}

interface MixerInput {
  track: MediaStreamTrack;
  source: AudioNodeLike;
  gain: GainNodeLike;
}

export interface AudioMixerOptions {
  createContext?: () => AudioContextLike;
}

export class AudioMixer {
  private readonly context: AudioContextLike;
  private readonly destination: MediaStreamDestinationLike;
  private readonly inputs = new Map<string, MixerInput>();
  private disposed = false;

  constructor(options: AudioMixerOptions = {}) {
    this.context =
      options.createContext?.() ?? (new AudioContext() as unknown as AudioContextLike);
    this.destination = this.context.createMediaStreamDestination();
  }

  /** The mixed output. Stable for the mixer's whole life. */
  get track(): MediaStreamTrack | null {
    return this.destination.stream.getAudioTracks()[0] ?? null;
  }

  get sourceCount(): number {
    return this.inputs.size;
  }

  /**
   * Reconciles the graph against the sources that should be audible.
   *
   * Existing inputs are kept rather than rebuilt: re-creating a source node for a stream that has
   * not changed produces a click, and doing that on every render would produce one per frame of
   * React state.
   */
  setSources(sources: MixerSource[]): void {
    if (this.disposed) return;

    const wanted = new Set<string>();

    for (const source of sources) {
      if (source.track.kind !== "audio") continue;
      wanted.add(source.id);

      const existing = this.inputs.get(source.id);

      if (existing && existing.track === source.track) {
        existing.gain.gain.value = levelOf(source);
        continue;
      }

      // A different track under the same id is a genuinely new source — a changed microphone, say.
      // The old one has to come out first, or both stay connected and the mix doubles.
      if (existing) this.disconnect(source.id);

      this.connect(source);
    }

    for (const id of [...this.inputs.keys()]) {
      if (!wanted.has(id)) this.disconnect(id);
    }
  }

  /**
   * Resumes the audio graph.
   *
   * Browsers start an `AudioContext` suspended until a user gesture. The studio always has one —
   * nobody broadcasts without pressing a button — but the resume has to actually be requested, and
   * a suspended context produces perfect silence with no error anywhere.
   */
  async resume(): Promise<void> {
    if (this.disposed || this.context.state === "running") return;

    try {
      await this.context.resume();
    } catch {
      // Nothing useful to do: the mix is silent until the next gesture, which the caller cannot
      // manufacture. Failing the broadcast over it would be worse.
    }
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;

    for (const id of [...this.inputs.keys()]) {
      this.disconnect(id);
    }

    try {
      await this.context.close();
    } catch {
      // Already closed.
    }
  }

  private connect(source: MixerSource): void {
    // Wrapped in a stream of its own so the graph node is bound to exactly this track and nothing
    // else that happens to share the source's original stream.
    const node = this.context.createMediaStreamSource(new MediaStream([source.track]));
    const gain = this.context.createGain();

    gain.gain.value = levelOf(source);
    node.connect(gain);
    gain.connect(this.destination);

    this.inputs.set(source.id, { track: source.track, source: node, gain });
  }

  private disconnect(id: string): void {
    const input = this.inputs.get(id);
    if (!input) return;

    try {
      input.source.disconnect();
      input.gain.disconnect();
    } catch {
      // Already detached.
    }

    this.inputs.delete(id);
  }
}

function levelOf(source: MixerSource): number {
  if (source.muted) return 0;
  const gain = source.gain ?? 1;
  return Number.isFinite(gain) ? Math.min(Math.max(gain, 0), 1) : 1;
}
