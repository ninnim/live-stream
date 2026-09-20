"use client";

import { Badge, Button, Card } from "@/components/ui/primitives";
import type { UseProgramResult } from "@/hooks/useProgram";

interface AudioMixerPanelProps {
  program: UseProgramResult;
  sessionIsEnded: boolean;
}

/**
 * The audio mixer.
 *
 * The default routing is "audio follows the picture, and the studio microphone is always heard",
 * which is right most of the time and wrong exactly when it matters — a guest whose room is noisy,
 * a second camera picking up the same speaker twice. These are the overrides for that.
 *
 * Only shown while composing: with a single camera going out untouched there is nothing to mix, and
 * a fader that does nothing is worse than no fader.
 */
export function AudioMixerPanel({ program, sessionIsEnded }: AudioMixerPanelProps) {
  if (!program.composing) return null;

  const audible = program.feeds.filter((feed) => feed.onAir || feed.source.role === "Host");

  return (
    <Card className="flex flex-col gap-4">
      <div>
        <h2 className="text-sm font-semibold text-slate-100">Audio</h2>
        <p className="mt-0.5 text-xs text-slate-500">
          Everything on air is heard, and the studio microphone always is.
        </p>
      </div>

      <ul className="flex flex-col gap-3">
        {audible.map((feed) => {
          const level = program.audio[feed.source.id] ?? { muted: false, gain: 1 };
          const hasAudio = (feed.stream?.getAudioTracks().length ?? 0) > 0;

          return (
            <li key={feed.source.id} className="flex flex-wrap items-center gap-3">
              <div className="min-w-40 flex-1">
                <div className="flex items-center gap-2">
                  <p className="truncate text-sm text-slate-200">{feed.source.displayName}</p>
                  {feed.source.role === "Host" ? <Badge tone="neutral">Studio</Badge> : null}
                </div>
                {!hasAudio ? (
                  <p className="text-xs text-amber-300">This source is not sending any audio.</p>
                ) : null}
              </div>

              <input
                type="range"
                min={0}
                max={100}
                step={5}
                value={Math.round(level.gain * 100)}
                aria-label={`${feed.source.displayName} level`}
                disabled={sessionIsEnded || level.muted}
                onChange={(event) =>
                  program.setSourceAudio(feed.source.id, { gain: Number(event.target.value) / 100 })
                }
                className="w-40"
              />

              <Button
                variant="secondary"
                aria-pressed={level.muted}
                disabled={sessionIsEnded}
                onClick={() => program.setSourceAudio(feed.source.id, { muted: !level.muted })}
              >
                <Badge tone={level.muted ? "critical" : "positive"}>{level.muted ? "Muted" : "On"}</Badge>
                {feed.source.displayName === "Studio" ? "Mic" : "Audio"}
              </Button>
            </li>
          );
        })}
      </ul>
    </Card>
  );
}
