"use client";

import { useEffect, useRef } from "react";
import { Badge } from "@/components/ui/primitives";

interface CameraPreviewProps {
  stream: MediaStream | null;
  cameraEnabled: boolean;
  isLive: boolean;
  isReconnecting: boolean;
}

/**
 * Local camera preview. Always shows the local capture — never the encoded return feed — so what
 * the broadcaster sees stays instant even while the connection is degraded.
 */
export function CameraPreview({ stream, cameraEnabled, isLive, isReconnecting }: CameraPreviewProps) {
  const videoRef = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    video.srcObject = stream;

    if (stream) {
      // Autoplay can be refused; the element stays visible and the user can start it manually.
      void video.play().catch(() => undefined);
    }

    return () => {
      video.srcObject = null;
    };
  }, [stream]);

  return (
    <div className="relative aspect-video w-full overflow-hidden rounded-xl border border-slate-800 bg-black">
      <video
        ref={videoRef}
        className="size-full object-cover"
        autoPlay
        playsInline
        // Muted is required for autoplay and prevents the broadcaster hearing their own echo.
        muted
        aria-label="Camera preview"
        data-testid="camera-preview"
      />

      {!stream ? (
        <div className="absolute inset-0 flex items-center justify-center">
          <p className="text-sm text-slate-500">Camera preview will appear here</p>
        </div>
      ) : null}

      {stream && !cameraEnabled ? (
        <div className="absolute inset-0 flex items-center justify-center bg-slate-950/90">
          <p className="text-sm font-medium text-slate-300">Camera is off</p>
        </div>
      ) : null}

      <div className="absolute left-4 top-4 flex items-center gap-2">
        {isLive ? (
          <Badge tone="live" pulse>
            LIVE
          </Badge>
        ) : null}
        {isReconnecting ? <Badge tone="caution">Reconnecting…</Badge> : null}
      </div>
    </div>
  );
}
