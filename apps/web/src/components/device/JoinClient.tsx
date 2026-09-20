"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { CameraPreview } from "@/components/studio/CameraPreview";
import { MediaErrorBanner } from "@/components/studio/StatusBanners";
import { Badge, Button, Card, Field, inputClasses } from "@/components/ui/primitives";
import { useDeviceBroadcast } from "@/hooks/useDeviceBroadcast";
import { useMediaDevices } from "@/hooks/useMediaDevices";

/**
 * What a phone sees when it joins a session as a camera.
 *
 * Deliberately spare. This screen is held in one hand, often at arm's length, by someone who is
 * pointing the phone at something rather than reading it: one action at a time, large targets, and
 * no control-room vocabulary.
 */
export function JoinClient({ initialCode }: { initialCode?: string }) {
  const device = useDeviceBroadcast();

  // Same indirection as the studio: capture needs to push tracks into a publisher that is created
  // after it, so the two are joined through a ref rather than by reordering the hooks.
  const replaceTrackRef = useRef<((track: MediaStreamTrack) => Promise<void>) | null>(null);
  const media = useMediaDevices({
    onVideoTrackChanged: (track) => replaceTrackRef.current?.(track),
    onAudioTrackChanged: (track) => replaceTrackRef.current?.(track),
  });

  useEffect(() => {
    replaceTrackRef.current = device.replaceTrack;
  }, [device.replaceTrack]);

  const [code, setCode] = useState(initialCode ?? "");

  const isPaired = device.phase !== "idle" && device.phase !== "pairing" && device.session !== null;
  const isLive = device.phase === "live";
  const contributesMedia = device.session?.permissions.includes("PublishMedia") ?? false;

  // A code arriving in the URL came from a scanned QR, so joining is the obvious next step and the
  // form would only be an extra tap. A typed code still goes through the form.
  useEffect(() => {
    if (initialCode && device.phase === "idle") {
      void device.pair(initialCode, deviceLabel()).catch(() => undefined);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialCode]);

  const handlePair = useCallback(() => {
    void device.pair(code.trim(), deviceLabel()).catch(() => undefined);
  }, [device, code]);

  const handleGoLive = useCallback(() => {
    if (media.stream) {
      void device.publish(media.stream);
    }
  }, [device, media.stream]);

  if (device.phase === "revoked") {
    return (
      <Shell>
        <Card>
          <h1 className="text-lg font-semibold text-slate-100">You have been removed</h1>
          <p className="mt-2 text-sm text-slate-400">
            The producer removed this device from the session. Ask them for a new code if you should
            still be taking part.
          </p>
        </Card>
      </Shell>
    );
  }

  if (!isPaired) {
    return (
      <Shell>
        <Card>
          <h1 className="text-lg font-semibold text-slate-100">Join a live session</h1>
          <p className="mt-2 text-sm text-slate-400">
            Enter the code shown in the control room.
          </p>

          {device.error ? (
            <p className="mt-3 rounded-lg bg-red-950/60 px-3 py-2 text-sm text-red-300 ring-1 ring-inset ring-red-900">
              {device.error}
            </p>
          ) : null}

          <div className="mt-4 flex flex-col gap-3">
            <Field label="Pairing code">
              <input
                className={`${inputClasses} text-center font-mono text-2xl tracking-[0.3em] uppercase`}
                value={code}
                aria-label="Pairing code"
                placeholder="ABCD-EFGH"
                autoComplete="off"
                autoCapitalize="characters"
                spellCheck={false}
                onChange={(event) => setCode(event.target.value)}
              />
            </Field>

            {/*
              There is deliberately no "name this device" field. The producer names the device when
              they create the code, and letting whoever joins rename it would overwrite a choice
              they made on purpose. The device's platform is sent automatically and appears in the
              session's audit trail.
            */}
            <Button onClick={handlePair} disabled={code.trim().length === 0 || device.busy}>
              {device.busy ? "Joining…" : "Join session"}
            </Button>
          </div>
        </Card>
      </Shell>
    );
  }

  return (
    <Shell>
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-xs font-medium uppercase tracking-wide text-slate-500">You are joined as</p>
          <h1 className="truncate text-lg font-semibold text-slate-50">
            {device.session?.source.displayName}
          </h1>
          <p className="mt-1 truncate text-sm text-slate-500">{device.session?.sessionTitle}</p>
        </div>

        <Badge tone={isLive ? "live" : "neutral"} pulse={isLive}>
          {isLive ? "Sending" : "Standing by"}
        </Badge>
      </div>

      {device.error ? (
        <p className="rounded-lg bg-red-950/60 px-3 py-2 text-sm text-red-300 ring-1 ring-inset ring-red-900">
          {device.error}
        </p>
      ) : null}

      {media.error ? (
        <MediaErrorBanner error={media.error} onRetry={() => void media.requestAccess()} />
      ) : null}

      {!contributesMedia ? (
        <Card>
          <p className="text-sm text-slate-400">
            This device joined as {device.session?.source.role.toLowerCase()}, so it does not send
            video. Keep this page open to stay connected to the session.
          </p>
        </Card>
      ) : (
        <>
          <CameraPreview
            stream={media.stream}
            cameraEnabled={media.cameraEnabled}
            isLive={isLive}
            isReconnecting={false}
          />

          {media.permission === "granted" ? (
            <div className="flex flex-wrap gap-3">
              {/*
                Enabled while sending. Turning the phone around to show what you are looking at is
                the whole point of a phone camera, and it must not cost the audience a dropout.
              */}
              {media.cameras.length > 1 ? (
                <Button
                  variant="secondary"
                  onClick={() => void media.selectCamera(nextCameraId(media))}
                  disabled={media.switching}
                >
                  {media.switching ? "Switching…" : "Switch camera"}
                </Button>
              ) : null}

              {isLive ? (
                <Button variant="danger" onClick={() => void device.stop()} disabled={device.busy}>
                  Stop sending
                </Button>
              ) : (
                <Button onClick={handleGoLive} disabled={!media.stream || device.busy}>
                  {device.busy ? "Starting…" : "Start sending video"}
                </Button>
              )}
            </div>
          ) : (
            <Button onClick={() => void media.requestAccess()} disabled={media.permission === "requesting"}>
              {media.permission === "requesting" ? "Waiting for permission…" : "Enable camera and microphone"}
            </Button>
          )}

          {isLive ? (
            <p className="text-sm text-slate-500">
              Keep this page open and your screen awake. Closing it stops your video.
            </p>
          ) : null}
        </>
      )}
    </Shell>
  );
}

function Shell({ children }: { children: React.ReactNode }) {
  return <div className="mx-auto flex w-full max-w-md flex-col gap-4 px-4 py-8">{children}</div>;
}

/** Cycles to the next camera, which on a phone is what "switch camera" means. */
function nextCameraId(media: ReturnType<typeof useMediaDevices>): string {
  const index = media.cameras.findIndex((camera) => camera.deviceId === media.selectedCameraId);
  return media.cameras[(index + 1) % media.cameras.length]?.deviceId ?? media.selectedCameraId ?? "";
}

/**
 * A default name for this device.
 *
 * Deliberately coarse — the platform, not a fingerprint. The producer needs to tell two phones
 * apart, which does not require knowing anything identifying about either.
 */
function deviceLabel(): string {
  if (typeof navigator === "undefined") return "Device";

  const agent = navigator.userAgent;

  if (/iPhone/i.test(agent)) return "iPhone";
  if (/iPad/i.test(agent)) return "iPad";
  if (/Android/i.test(agent)) return "Android phone";
  if (/Macintosh/i.test(agent)) return "Mac";
  if (/Windows/i.test(agent)) return "Windows PC";

  return "Device";
}
