"use client";

import { LEVEL_ADVICE, type InputLevel, SILENT_LEVEL } from "@/lib/media/input-level";

/**
 * What a microphone is actually picking up.
 *
 * The single most useful control in the studio for anyone who has ever been told "we cannot hear
 * you". Everything else about audio — the device picker, the mute button, the mode — reports what
 * was *asked for*. This reports what arrived.
 */

const BAR_TONE: Record<InputLevel["verdict"], string> = {
  silent: "bg-slate-600",
  quiet: "bg-amber-400",
  good: "bg-emerald-400",
  loud: "bg-rose-500",
};

const LABEL_TONE: Record<InputLevel["verdict"], string> = {
  silent: "text-slate-400",
  quiet: "text-amber-300",
  good: "text-emerald-300",
  loud: "text-rose-300",
};

const VERDICT_LABEL: Record<InputLevel["verdict"], string> = {
  silent: "No sound",
  quiet: "Too quiet",
  good: "Good",
  loud: "Too loud",
};

export function InputLevelMeter({
  label,
  level,
  present,
}: {
  label: string;
  level: InputLevel | undefined;
  /** Whether the device is open at all. A closed device has no level, only an absence. */
  present: boolean;
}) {
  const reading = level ?? SILENT_LEVEL;
  const percent = present ? Math.min(100, Math.round(reading.rms * 320)) : 0;
  const verdict = present ? reading.verdict : "silent";
  const advice = present ? LEVEL_ADVICE[verdict] : null;

  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-xs font-medium text-slate-400">{label}</span>
        <span className={`text-xs font-medium ${present ? LABEL_TONE[verdict] : "text-slate-500"}`}>
          {present ? VERDICT_LABEL[verdict] : "Not open"}
        </span>
      </div>

      {/*
        `meter` rather than `progressbar`: this is a measurement inside a known range, and the
        distinction is what lets a screen reader announce it as a level rather than as progress
        towards finishing something.
      */}
      <div
        role="meter"
        aria-label={`${label} level`}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        aria-valuetext={present ? VERDICT_LABEL[verdict] : "Not open"}
        className="h-2 w-full overflow-hidden rounded-full bg-slate-800"
      >
        <div
          className={`h-full rounded-full transition-[width] duration-75 ${BAR_TONE[verdict]}`}
          style={{ width: `${percent}%` }}
        />
      </div>

      {/*
        Only when there is something to do about it. A meter that explains itself while everything
        is fine trains people to stop reading it.
      */}
      {advice ? <p className="text-xs text-slate-500">{advice}</p> : null}
    </div>
  );
}
