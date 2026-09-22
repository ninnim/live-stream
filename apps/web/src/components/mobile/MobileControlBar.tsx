"use client";

interface MobileControlBarProps {
  isBroadcasting: boolean;
  isStarting: boolean;
  busy: boolean;
  canGoLive: boolean;
  cameraEnabled: boolean;
  microphoneEnabled: boolean;
  hasCamera: boolean;
  hasMicrophone: boolean;
  canFlip: boolean;
  switching: boolean;
  onGoLive: () => void;
  onStop: () => void;
  onFlip: () => void;
  onToggleCamera: () => void;
  onToggleMicrophone: () => void;
}

/**
 * The bottom bar: the four things somebody does while holding a phone.
 *
 * Sizing is a functional decision, not a stylistic one. Every control is at least 56px — comfortably
 * past the 44px both platforms recommend — because this is worked one-handed, often one-thumbed,
 * frequently while moving. The go-live control is deliberately much larger than the rest and sits
 * in the middle, where a thumb reaches without the hand shifting its grip.
 *
 * There is no confirmation step on **Stop**, and one on nothing else. A broadcaster who wants to
 * stop usually wants to stop *now*.
 */
export function MobileControlBar({
  isBroadcasting,
  isStarting,
  busy,
  canGoLive,
  cameraEnabled,
  microphoneEnabled,
  hasCamera,
  hasMicrophone,
  canFlip,
  switching,
  onGoLive,
  onStop,
  onFlip,
  onToggleCamera,
  onToggleMicrophone,
}: MobileControlBarProps) {
  return (
    <div className="bg-gradient-to-t from-black/80 to-transparent px-6 pb-[max(1.5rem,env(safe-area-inset-bottom))] pt-10">
      <div className="flex items-center justify-between gap-4">
        <SecondaryControl
          label={microphoneEnabled ? "Mute microphone" : "Unmute microphone"}
          active={microphoneEnabled}
          disabled={!hasMicrophone}
          onClick={onToggleMicrophone}
          testId="mobile-mic"
        >
          {microphoneEnabled ? "Mic" : "Muted"}
        </SecondaryControl>

        {isBroadcasting || isStarting ? (
          <button
            type="button"
            onClick={onStop}
            disabled={busy}
            aria-label="Stop the broadcast"
            data-testid="mobile-stop"
            className="flex size-20 items-center justify-center rounded-full bg-white ring-4 ring-white/30 transition active:scale-95 disabled:opacity-50"
          >
            <span className="size-7 rounded bg-red-600" aria-hidden="true" />
          </button>
        ) : (
          <button
            type="button"
            onClick={onGoLive}
            disabled={!canGoLive || busy}
            aria-label="Go live"
            data-testid="mobile-go-live"
            className="flex size-20 items-center justify-center rounded-full bg-red-600 text-xs font-bold uppercase tracking-wide text-white ring-4 ring-white/30 transition active:scale-95 disabled:bg-slate-700 disabled:ring-white/10"
          >
            {busy ? "…" : "Go live"}
          </button>
        )}

        {canFlip ? (
          <SecondaryControl
            label="Switch between the front and back camera"
            // Deliberately no pressed state: flipping is an action, not a toggle, and announcing
            // it as one tells a screen-reader user something that is not true.
            disabled={switching || !hasCamera}
            onClick={onFlip}
            testId="mobile-flip"
          >
            Flip
          </SecondaryControl>
        ) : (
          <SecondaryControl
            label={cameraEnabled ? "Turn the camera off" : "Turn the camera on"}
            active={cameraEnabled}
            disabled={!hasCamera}
            onClick={onToggleCamera}
            testId="mobile-camera"
          >
            {cameraEnabled ? "Cam" : "Off"}
          </SecondaryControl>
        )}
      </div>

      {/*
        The camera toggle keeps its own place whenever flipping took the slot above, rather than
        being buried in the settings sheet: turning the picture off mid-broadcast is something
        people do in a hurry, for reasons they do not want to explain to an audience.
      */}
      {canFlip ? (
        <div className="mt-4 flex justify-center">
          <SecondaryControl
            label={cameraEnabled ? "Turn the camera off" : "Turn the camera on"}
            active={cameraEnabled}
            disabled={!hasCamera}
            onClick={onToggleCamera}
            testId="mobile-camera"
          >
            {cameraEnabled ? "Camera on" : "Camera off"}
          </SecondaryControl>
        </div>
      ) : null}
    </div>
  );
}

function SecondaryControl({
  label,
  active,
  disabled,
  onClick,
  testId,
  children,
}: {
  label: string;
  /** Omitted for controls that act rather than toggle, so nothing claims a state they do not have. */
  active?: boolean;
  disabled: boolean;
  onClick: () => void;
  testId: string;
  children: React.ReactNode;
}) {
  // A toggle that is off is drawn as a warning rather than as "inactive": a muted microphone is
  // something the broadcaster needs to notice from across a room, not a dimmed button.
  const off = active === false;

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      {...(active === undefined ? {} : { "aria-pressed": active })}
      data-testid={testId}
      className={`flex min-h-14 min-w-14 items-center justify-center rounded-full px-4 text-xs font-semibold ring-1 transition active:scale-95 disabled:opacity-40 ${
        off ? "bg-red-950/70 text-red-200 ring-red-500/40" : "bg-white/15 text-white ring-white/25"
      }`}
    >
      {children}
    </button>
  );
}
