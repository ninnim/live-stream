"use client";

import { useEffect, useRef } from "react";

interface WakeLockSentinelLike {
  released: boolean;
  release: () => Promise<void>;
  addEventListener: (type: "release", listener: () => void) => void;
}

/**
 * Keeps the screen awake while something is on air.
 *
 * On a phone this is a broadcast-critical feature rather than a convenience. The screen locking is
 * not a display event: it suspends capture, and the audience is left watching a frozen frame while
 * the broadcaster, who has not touched the phone because they are holding it still and pointing it
 * at something, has no idea anything happened. The default lock timeout is thirty seconds.
 *
 * Every failure is silent. The API is unevenly supported — it has only been in Safari since 16.4 —
 * and a browser that refuses simply leaves the phone locking as it always did. Refusing to go live
 * over it would be absurd.
 *
 * The lock is re-taken when the page becomes visible again, because the browser releases it on
 * every hide and does not give it back on its own.
 */
export function useWakeLock(active: boolean): void {
  const sentinelRef = useRef<WakeLockSentinelLike | null>(null);

  useEffect(() => {
    if (!active || typeof navigator === "undefined") return;

    const wakeLock = (navigator as Navigator & {
      wakeLock?: { request: (type: "screen") => Promise<WakeLockSentinelLike> };
    }).wakeLock;

    if (!wakeLock) return;

    let cancelled = false;

    const acquire = async (): Promise<void> => {
      if (cancelled || sentinelRef.current) return;

      try {
        const sentinel = await wakeLock.request("screen");

        if (cancelled) {
          void sentinel.release().catch(() => undefined);
          return;
        }

        sentinel.addEventListener("release", () => {
          sentinelRef.current = null;
        });

        sentinelRef.current = sentinel;
      } catch {
        // Denied, or the page was hidden at the moment of asking. Nothing to repair.
      }
    };

    const onVisibility = (): void => {
      if (document.visibilityState === "visible") void acquire();
    };

    void acquire();
    document.addEventListener("visibilitychange", onVisibility);

    return () => {
      cancelled = true;
      document.removeEventListener("visibilitychange", onVisibility);

      const sentinel = sentinelRef.current;
      sentinelRef.current = null;
      if (sentinel && !sentinel.released) void sentinel.release().catch(() => undefined);
    };
  }, [active]);
}
