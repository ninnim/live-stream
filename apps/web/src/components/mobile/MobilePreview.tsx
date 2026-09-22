"use client";

import { useEffect, useRef } from "react";
import type { CameraFacing } from "@/lib/media/mobile";

interface MobilePreviewProps {
  stream: MediaStream | null;
  /** False when capture opened without a camera — a refused permission, or sound only. */
  hasVideo: boolean;
  facing: CameraFacing;
  cameraEnabled: boolean;
  interrupted: boolean;
}

/**
 * The viewfinder: the camera, full bleed, behind everything else.
 *
 * Two decisions worth stating.
 *
 * **The front camera is mirrored, and only in the preview.** Everybody expects their own face to
 * behave like a mirror, and nobody expects the audience to read their T-shirt backwards. The flip
 * is a CSS transform on the element, so it touches what the broadcaster sees and nothing that is
 * sent. The rear camera is never mirrored — it is pointing at the world, and the world is not
 * reversed.
 *
 * **`object-cover`, not `object-contain`.** A phone viewfinder that letterboxes itself looks
 * broken. The outgoing video is unaffected either way; this is only how the local picture is
 * fitted to a screen whose shape it does not share.
 */
export function MobilePreview({
  stream,
  hasVideo,
  facing,
  cameraEnabled,
  interrupted,
}: MobilePreviewProps) {
  const videoRef = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    video.srcObject = stream;

    if (stream) {
      // Autoplay can still be refused; the element stays visible and the controls still work.
      void video.play().catch(() => undefined);
    }

    return () => {
      video.srcObject = null;
    };
  }, [stream]);

  return (
    <div className="absolute inset-0 bg-black">
      <video
        ref={videoRef}
        className={`size-full object-cover ${facing === "user" ? "-scale-x-100" : ""}`}
        autoPlay
        // Without this, iOS takes the video full screen in its own player the moment it plays,
        // and every control on this screen disappears behind it.
        playsInline
        // Required for autoplay, and it stops the broadcaster hearing themselves.
        muted
        aria-label="Camera preview"
        data-testid="mobile-preview"
      />

      {!stream ? (
        <div className="absolute inset-0 flex items-center justify-center px-8 text-center">
          <p className="text-sm text-slate-500">Your camera will appear here.</p>
        </div>
      ) : null}

      {/*
        Capture opened, but with no picture in it — a refused camera on a phone whose microphone
        was allowed. A broadcast of sound alone is a real broadcast and is allowed to continue, but
        an unexplained black viewfinder reads as a fault, so it says which one this is.
      */}
      {stream && !hasVideo ? (
        <div className="absolute inset-0 flex items-center justify-center px-8 text-center">
          <p className="text-sm text-slate-300">
            No camera. Your broadcast will carry sound only.
          </p>
        </div>
      ) : null}

      {stream && hasVideo && !cameraEnabled ? (
        <div className="absolute inset-0 flex items-center justify-center bg-slate-950/90">
          <p className="text-sm font-medium text-slate-300">Camera off</p>
        </div>
      ) : null}

      {/*
        The interruption overlay is not decoration. While the operating system holds the camera,
        the preview keeps showing the last frame it had — so without something on top of it, the
        broadcaster is looking at a picture that stopped being live some time ago.
      */}
      {interrupted ? (
        <div className="absolute inset-0 flex items-center justify-center bg-slate-950/80 px-8 text-center">
          <p className="text-sm font-medium text-amber-300">
            Camera paused by your phone. It restarts when you come back.
          </p>
        </div>
      ) : null}
    </div>
  );
}
