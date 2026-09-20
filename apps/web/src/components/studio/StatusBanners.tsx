"use client";

import { Button } from "@/components/ui/primitives";
import type { MediaAccessError } from "@/lib/media/devices";

/**
 * Error presentation.
 *
 * Every banner states what happened and what to do next — never a raw browser error name
 * (docs/01-product-requirements.md UX principles).
 */
export function ErrorBanner({
  title,
  detail,
  onDismiss,
  onRetry,
}: {
  title: string;
  detail?: string;
  onDismiss?: () => void;
  onRetry?: () => void;
}) {
  return (
    <div role="alert" className="rounded-xl border border-red-900 bg-red-950/60 p-4">
      <p className="text-sm font-semibold text-red-200">{title}</p>
      {detail ? <p className="mt-1 text-sm text-red-300/90">{detail}</p> : null}

      {onRetry || onDismiss ? (
        <div className="mt-3 flex gap-2">
          {onRetry ? (
            <Button variant="secondary" onClick={onRetry}>
              Try again
            </Button>
          ) : null}
          {onDismiss ? (
            <Button variant="ghost" onClick={onDismiss}>
              Dismiss
            </Button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

export function MediaErrorBanner({
  error,
  onRetry,
}: {
  error: MediaAccessError;
  onRetry?: () => void;
}) {
  return <ErrorBanner title={error.userMessage} detail={error.recovery} onRetry={onRetry} />;
}

/**
 * Neutral news about hardware — a camera plugged in, a microphone pulled out.
 *
 * Deliberately not an `alert`: a device appearing is not a problem, and interrupting a screen
 * reader mid-broadcast to say so would be worse than the information is worth.
 */
export function DeviceNoticeBanner({ message, onDismiss }: { message: string; onDismiss: () => void }) {
  return (
    <div role="status" className="flex items-start justify-between gap-4 rounded-xl border border-sky-900 bg-sky-950/50 p-4">
      <p className="text-sm text-sky-100">{message}</p>
      <Button variant="ghost" onClick={onDismiss}>
        Dismiss
      </Button>
    </div>
  );
}

/** Shown while the server is holding the session open through a temporary interruption. */
export function ReconnectingBanner({
  attempt,
  secondsRemaining,
}: {
  attempt: number;
  secondsRemaining: number | null;
}) {
  return (
    <div role="status" className="rounded-xl border border-amber-900 bg-amber-950/60 p-4">
      <p className="text-sm font-semibold text-amber-200">
        Reconnecting{attempt > 0 ? ` — attempt ${attempt}` : ""}…
      </p>
      <p className="mt-1 text-sm text-amber-300/90">
        Your live session is being held open while we restore the connection.
        {secondsRemaining !== null
          ? ` It will end automatically if the stream does not recover within ${secondsRemaining}s.`
          : ""}
      </p>
    </div>
  );
}
/**
 * The way into the studio.
 *
 * Offers both entry paths side by side rather than treating the camera as the way in and screen
 * sharing as a thing you do afterwards. A machine with no webcam is a normal machine, and on one of
 * those the second button is the entire broadcast.
 */
export function PermissionPrompt({
  onRequest,
  onShareScreen,
  busy,
}: {
  onRequest: () => void;
  onShareScreen: () => void;
  busy: boolean;
}) {
  return (
    <div className="rounded-xl border border-slate-800 bg-slate-900/60 p-6 text-center">
      <h2 className="text-base font-semibold text-slate-100">Choose what to broadcast</h2>
      <p className="mx-auto mt-2 max-w-md text-sm text-slate-400">
        Your browser will ask for permission. Nothing is streamed until you choose Start Live — you
        will see a preview first. A camera is optional: you can broadcast a screen instead.
      </p>
      <div className="mt-4 flex flex-wrap justify-center gap-3">
        <Button onClick={onRequest} disabled={busy}>
          {busy ? "Requesting access…" : "Use camera and microphone"}
        </Button>
        <Button variant="secondary" onClick={onShareScreen} disabled={busy}>
          Share a screen instead
        </Button>
      </div>
    </div>
  );
}

/**
 * Offers the way out of having no picture.
 *
 * Shown when the studio is running on sound alone — no webcam on this machine, or camera access
 * refused. It is a prompt rather than an error: nothing is broken, and there is something useful
 * to do about it.
 */
export function NoCameraPrompt({ onShareScreen, busy }: { onShareScreen: () => void; busy: boolean }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-900/60 p-4">
      <div>
        <p className="text-sm font-semibold text-slate-100">No camera on this machine</p>
        <p className="mt-1 text-sm text-slate-400">
          Share a screen or a window to give your broadcast a picture, or bring in a phone as a
          camera.
        </p>
      </div>
      <Button variant="secondary" onClick={onShareScreen} disabled={busy}>
        Share a screen
      </Button>
    </div>
  );
}
