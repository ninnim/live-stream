"use client";

import { useEffect, useRef, useState } from "react";
import type Hls from "hls.js";

/**
 * Viewer playback over HLS.
 *
 * Safari plays HLS natively, so hls.js is only loaded when the browser needs it — and it is
 * imported dynamically so the library never lands in the studio's bundle.
 */
export function LivePlayer({ hlsUrl, isLive }: { hlsUrl: string; isLive: boolean }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !hlsUrl) return;

    let hls: Hls | null = null;
    let cancelled = false;

    const attach = async (): Promise<void> => {
      if (video.canPlayType("application/vnd.apple.mpegurl")) {
        video.src = hlsUrl;
        return;
      }

      const { default: HlsClass } = await import("hls.js");
      if (cancelled) return;

      if (!HlsClass.isSupported()) {
        setError("This browser cannot play the live stream.");
        return;
      }

      hls = new HlsClass({
        // Tuned for live: stay close to the edge rather than buffering a long history.
        lowLatencyMode: true,
        backBufferLength: 30,
      });

      hls.on(HlsClass.Events.ERROR, (_event, data) => {
        if (!data.fatal) return;

        switch (data.type) {
          case HlsClass.ErrorTypes.NETWORK_ERROR:
            // The broadcaster may simply be reconnecting; retry rather than giving up.
            hls?.startLoad();
            break;
          case HlsClass.ErrorTypes.MEDIA_ERROR:
            hls?.recoverMediaError();
            break;
          default:
            setError("The live stream is unavailable right now.");
            break;
        }
      });

      hls.loadSource(hlsUrl);
      hls.attachMedia(video);
    };

    void attach();

    return () => {
      cancelled = true;
      hls?.destroy();
    };
  }, [hlsUrl]);

  return (
    <div className="flex flex-col gap-3">
      <div className="relative aspect-video w-full overflow-hidden rounded-xl border border-slate-800 bg-black">
        <video
          ref={videoRef}
          className="size-full"
          controls
          autoPlay
          playsInline
          aria-label="Live stream"
          data-testid="live-player"
        />

        {!isLive ? (
          <div className="absolute inset-0 flex items-center justify-center bg-slate-950/80">
            <p className="text-sm text-slate-400">This stream is not live right now.</p>
          </div>
        ) : null}
      </div>

      {error ? (
        <p role="alert" className="text-sm text-red-400">
          {error}
        </p>
      ) : null}
    </div>
  );
}
