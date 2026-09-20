"use client";

import { useCallback, useEffect, useState } from "react";
import { LivePlayer } from "@/components/player/LivePlayer";
import { Badge } from "@/components/ui/primitives";
import { ApiError } from "@/lib/api/client";
import { liveSessionApi } from "@/lib/api/live-sessions";
import type { Playback } from "@/lib/types";

/** How often a viewer re-checks whether the stream has started or ended. */
const POLL_MS = 8000;

/**
 * Viewer page. Deliberately anonymous-friendly: public and unlisted sessions are watchable from a
 * link, and the API refuses playback for private sessions without workspace membership.
 */
export function WatchClient({ sessionId }: { sessionId: string }) {
  const [playback, setPlayback] = useState<Playback | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    try {
      setPlayback(await liveSessionApi.playback(sessionId));
      setError(null);
    } catch (loadError) {
      if (loadError instanceof ApiError) {
        setError(
          loadError.status === 403
            ? "This stream is private."
            : loadError.isSessionGone
              ? "This stream does not exist."
              : loadError.message,
        );
      } else {
        setError("We could not load this stream.");
      }
    } finally {
      setLoading(false);
    }
  }, [sessionId]);

  // The first load runs on a zero-delay timer rather than directly in the effect body, so its
  // state updates land in their own commit instead of cascading inside this one.
  useEffect(() => {
    const initial = setTimeout(() => void load(), 0);
    const poll = setInterval(() => void load(), POLL_MS);

    return () => {
      clearTimeout(initial);
      clearInterval(poll);
    };
  }, [load]);

  return (
    <main className="mx-auto flex w-full max-w-4xl flex-col gap-4 px-4 py-8">
      <header className="flex items-center justify-between gap-4">
        <h1 className="text-xl font-semibold text-slate-50">Live stream</h1>
        {playback?.isLive ? (
          <Badge tone="live" pulse>
            LIVE
          </Badge>
        ) : (
          <Badge tone="neutral">Offline</Badge>
        )}
      </header>

      {loading ? <p className="text-sm text-slate-500">Loading…</p> : null}

      {error ? (
        <p role="alert" className="text-sm text-red-400">
          {error}
        </p>
      ) : null}

      {playback ? <LivePlayer hlsUrl={playback.hlsUrl} isLive={playback.isLive} /> : null}
    </main>
  );
}
