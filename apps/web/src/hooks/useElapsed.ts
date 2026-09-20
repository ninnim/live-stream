"use client";

import { useEffect, useState } from "react";
import { secondsSince } from "@/lib/format";

/**
 * Seconds between `startedAt` and either `endedAt` or now, ticking once a second while the session
 * is still running.
 *
 * The elapsed value is derived during render from the server's timestamps rather than accumulated
 * locally, so the timer stays correct across a studio reload and cannot drift during a long
 * broadcast. Only the clock reading lives in state.
 */
export function useElapsedSeconds(
  startedAt: string | null | undefined,
  endedAt?: string | null,
): number {
  const [now, setNow] = useState(() => Date.now());
  const running = Boolean(startedAt) && !endedAt;

  useEffect(() => {
    if (!running) return;

    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);

  if (!startedAt) return 0;

  // A finished session has a fixed duration; a running one is measured against the ticking clock.
  const reference = endedAt ? Date.parse(endedAt) : now;
  return secondsSince(startedAt, Number.isNaN(reference) ? now : reference);
}
