import { AudioMixer, type AudioMixerOptions, type MixerSource } from "@/lib/media/audio-mixer";

/**
 * Combines what the broadcaster is saying with what their screen is playing.
 *
 * The publisher sends exactly one audio track, so a game and a voice cannot both be attached to it —
 * they have to be summed into one first. That is the whole job here.
 *
 * The mixer is only built when there is something to mix. With a microphone alone the track goes
 * out untouched: a Web Audio graph in the path costs CPU, adds a buffer of latency, and introduces a
 * component that can be suspended by the browser and produce perfect silence. None of that is worth
 * paying for a mix of one.
 */

export const MICROPHONE_SOURCE = "microphone";
export const SCREEN_SOURCE = "screen";

export interface CaptureAudioSources {
  microphone: MediaStreamTrack | null;
  screen: MediaStreamTrack | null;
}

export interface CaptureAudioLevels {
  /** 0 to 1. */
  microphone: number;
  screen: number;
}

export const DEFAULT_CAPTURE_LEVELS: CaptureAudioLevels = {
  microphone: 1,

  // Screen audio starts below the voice deliberately. A game mixed at unity buries the person
  // playing it, and a broadcaster who cannot be heard over their own game will turn the game down —
  // so the default should be the one they would have chosen.
  screen: 0.7,
};

/** What the studio should publish, and why. */
export type CaptureAudioPlan =
  | { kind: "none" }
  | { kind: "microphone"; track: MediaStreamTrack }
  | { kind: "screen"; track: MediaStreamTrack }
  | { kind: "mixed"; sources: MixerSource[] };

/**
 * Decides what to publish for a given pair of sources.
 *
 * Pure, because the interesting part is the policy rather than the Web Audio plumbing: when to mix,
 * when not to bother, and what happens when one side goes away mid-broadcast.
 */
export function planCaptureAudio(
  sources: CaptureAudioSources,
  levels: CaptureAudioLevels = DEFAULT_CAPTURE_LEVELS,
): CaptureAudioPlan {
  const { microphone, screen } = sources;

  if (microphone && screen) {
    return {
      kind: "mixed",
      sources: [
        { id: MICROPHONE_SOURCE, track: microphone, gain: levels.microphone },
        { id: SCREEN_SOURCE, track: screen, gain: levels.screen },
      ],
    };
  }

  if (microphone) return { kind: "microphone", track: microphone };

  // Screen audio with no microphone is a legitimate broadcast — someone showing a video, or a
  // machine with no mic at all — and publishing it unmixed keeps that case free of a graph too.
  if (screen) return { kind: "screen", track: screen };

  return { kind: "none" };
}

/**
 * Holds the mixer across plan changes, building and tearing it down as the plan requires.
 *
 * The output track has to stay the same object for as long as mixing continues: it is what the
 * publisher is sending, and swapping it is audible. So the mixer survives a source changing, and is
 * only disposed when there is nothing left to mix.
 */
export class CaptureAudio {
  private mixer: AudioMixer | null = null;
  private plan: CaptureAudioPlan = { kind: "none" };

  constructor(private readonly options: AudioMixerOptions = {}) {}

  /** The track to publish, or null when there is no audio at all. */
  get track(): MediaStreamTrack | null {
    switch (this.plan.kind) {
      case "microphone":
      case "screen":
        return this.plan.track;
      case "mixed":
        return this.mixer?.track ?? null;
      default:
        return null;
    }
  }

  get isMixing(): boolean {
    return this.plan.kind === "mixed";
  }

  /**
   * Applies a plan. Returns the track to publish, which the caller compares against what it is
   * already sending — an unchanged track must not be re-published.
   */
  async applyAsync(plan: CaptureAudioPlan): Promise<MediaStreamTrack | null> {
    this.plan = plan;

    if (plan.kind !== "mixed") {
      await this.disposeMixer();
      return this.track;
    }

    // Built on first use, and only ever here: a browser that cannot give us an AudioContext should
    // still broadcast, on the microphone alone, rather than fail on a mixer it never needed.
    if (!this.mixer) {
      try {
        this.mixer = new AudioMixer(this.options);
      } catch {
        this.plan = { kind: "microphone", track: plan.sources[0]!.track };
        return this.track;
      }
    }

    this.mixer.setSources(plan.sources);
    await this.mixer.resume();

    return this.mixer.track;
  }

  async disposeAsync(): Promise<void> {
    this.plan = { kind: "none" };
    await this.disposeMixer();
  }

  private async disposeMixer(): Promise<void> {
    const mixer = this.mixer;
    this.mixer = null;
    await mixer?.dispose();
  }
}
