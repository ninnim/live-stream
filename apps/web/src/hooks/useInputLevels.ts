"use client";

import { useEffect, useRef, useState } from "react";
import {
  type InputLevel,
  LevelMonitor,
  type LevelMonitorOptions,
  SILENT_LEVEL,
} from "@/lib/media/input-level";

export const MICROPHONE_INPUT = "microphone";
export const SCREEN_INPUT = "screen";

export type InputLevels = Record<string, InputLevel>;

/**
 * Drives the input meters.
 *
 * On an animation frame rather than an interval, for two reasons. It is the rate the meter is
 * actually drawn at, so measuring faster would be work nobody sees; and the browser stops animation
 * frames entirely for a hidden tab, which parks the meter for a broadcaster who has switched away
 * instead of spinning a timer behind their back.
 *
 * React state is updated at a fixed rate rather than per frame. A 60 Hz setState drives 60 renders
 * a second through a studio that also holds a compositor and a switcher, and a meter is perfectly
 * readable at a fifth of that.
 */
const UPDATE_INTERVAL_MS = 100;

export function useInputLevels(
  tracks: Record<string, MediaStreamTrack | null>,
  options: LevelMonitorOptions = {},
): InputLevels {
  const [levels, setLevels] = useState<InputLevels>({});

  // Built once and never rebuilt. The exact null check the lint rule asks for: it is the form that
  // is provably a one-time initialisation rather than a read of a value that might have changed.
  const monitorRef = useRef<LevelMonitor | null>(null);
  if (monitorRef.current == null) {
    monitorRef.current = new LevelMonitor(options);
  }

  // Held in a ref so the animation loop reads the current tracks without being restarted whenever
  // a device changes — restarting it would drop a frame of every meter on every render.
  const idsRef = useRef<string[]>([]);

  // Deliberately on every render, with no dependency array. `tracks` is a fresh object each time, so
  // any key derived from it would be guesswork — and `setTracks` already decides what changed by
  // comparing the track objects themselves. A key on `track.id` was the first attempt and was both
  // redundant and wrong: it made the meter depend on tracks carrying an id, and silently attached
  // nothing when one did not.
  useEffect(() => {
    const monitor = monitorRef.current!;
    idsRef.current = Object.keys(tracks);

    // Resumed only when the graph actually changed. A browser starts a context suspended, and a
    // suspended analyser reads a flat zero — a silence that is not real.
    if (monitor.setTracks(tracks)) {
      void monitor.resume();
    }
  });

  useEffect(() => {
    const monitor = monitorRef.current!;
    let frame = 0;
    let lastUpdate = 0;

    const tick = (now: number): void => {
      frame = requestAnimationFrame(tick);

      if (now - lastUpdate < UPDATE_INTERVAL_MS) return;
      lastUpdate = now;

      const next: InputLevels = {};
      for (const id of idsRef.current) {
        next[id] = monitor.read(id);
      }

      setLevels((previous) => (unchanged(previous, next) ? previous : next));
    };

    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, []);

  useEffect(() => {
    const monitor = monitorRef.current!;
    return () => void monitor.disposeAsync();
  }, []);

  return levels;
}

/**
 * Whether a new reading is worth a render.
 *
 * The bar is drawn to whole percent, so a change below that is invisible; comparing on it keeps a
 * silent studio from re-rendering ten times a second on floating-point noise.
 */
function unchanged(previous: InputLevels, next: InputLevels): boolean {
  const ids = Object.keys(next);
  if (ids.length !== Object.keys(previous).length) return false;

  return ids.every((id) => {
    const before = previous[id] ?? SILENT_LEVEL;
    const after = next[id]!;

    return (
      before.verdict === after.verdict &&
      Math.round(before.rms * 100) === Math.round(after.rms * 100) &&
      Math.round(before.peak * 100) === Math.round(after.peak * 100)
    );
  });
}
