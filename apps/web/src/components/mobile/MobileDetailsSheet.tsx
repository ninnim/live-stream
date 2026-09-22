"use client";

import { EncoderPanel } from "@/components/studio/EncoderPanel";
import { Badge } from "@/components/ui/primitives";
import { destinationStatusLabel, destinationStatusTone, formatBitrate, providerLabel } from "@/lib/format";
import type { CaptureQuality, TrackFormat } from "@/lib/media/devices";
import { MOBILE_QUALITIES, mobileAdvice } from "@/lib/media/mobile";
import type { PublishQuality } from "@/lib/media/quality";
import type { UseStreamKeyResult } from "@/hooks/useStreamKey";
import type { Destination } from "@/lib/types";

interface MobileDetailsSheetProps {
  sessionId: string;
  quality: CaptureQuality;
  qualityReason: string;
  videoFormat: TrackFormat | null;
  measured: PublishQuality | null;
  /** What the adaptive ladder is currently holding the encoder to, or null at full quality. */
  reducedLabel: string | null;
  destinations: Destination[];
  busyDestinationId: string | null;
  streamKey: UseStreamKeyResult;
  sessionIsEnded: boolean;
  switching: boolean;
  onSelectQuality: (quality: CaptureQuality) => void;
  onStartDestination: (id: string) => void;
  onStopDestination: (id: string) => void;
  onClose: () => void;
}

/**
 * Everything that is not one of the four things done while filming.
 *
 * A sheet rather than a screen, because opening it must never look like leaving the broadcast: the
 * viewfinder stays visible behind it and the status bar keeps running. It holds the settings
 * somebody changes between shots, the numbers somebody looks at when something feels wrong, and
 * the destinations they want to confirm are actually receiving.
 */
export function MobileDetailsSheet({
  sessionId,
  quality,
  qualityReason,
  videoFormat,
  measured,
  reducedLabel,
  destinations,
  busyDestinationId,
  streamKey,
  sessionIsEnded,
  switching,
  onSelectQuality,
  onStartDestination,
  onStopDestination,
  onClose,
}: MobileDetailsSheetProps) {
  const advice = measured ? mobileAdvice(measured) : null;

  return (
    <div className="absolute inset-0 z-20 flex flex-col justify-end" data-testid="mobile-sheet">
      {/* A real button rather than a div with a click handler: tapping outside to dismiss is the
          gesture everybody uses, and it should be reachable by keyboard and screen reader too. */}
      <button
        type="button"
        aria-label="Close settings"
        className="absolute inset-0 bg-black/60"
        onClick={onClose}
      />

      <section
        role="dialog"
        aria-label="Broadcast settings"
        className="relative max-h-[85vh] overflow-y-auto rounded-t-2xl bg-slate-950/95 px-5 pb-[max(1.5rem,env(safe-area-inset-bottom))] pt-4 ring-1 ring-slate-800 backdrop-blur"
      >
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-sm font-semibold text-slate-100">Broadcast settings</h2>
          <button
            type="button"
            onClick={onClose}
            className="min-h-11 rounded-full px-4 text-sm font-medium text-slate-300"
          >
            Done
          </button>
        </div>

        <fieldset className="mb-5">
          <legend className="mb-2 text-xs font-medium uppercase tracking-wide text-slate-500">
            Camera quality
          </legend>

          <div className="flex gap-2">
            {MOBILE_QUALITIES.map((option) => (
              <button
                key={option}
                type="button"
                disabled={switching}
                aria-pressed={quality === option}
                onClick={() => onSelectQuality(option)}
                data-testid={`mobile-quality-${option}`}
                className={`min-h-11 flex-1 rounded-lg text-sm font-semibold ring-1 transition disabled:opacity-50 ${
                  quality === option
                    ? "bg-sky-500 text-white ring-sky-400"
                    : "bg-slate-900 text-slate-300 ring-slate-700"
                }`}
              >
                {option}
              </button>
            ))}
          </div>

          <p className="mt-2 text-xs text-slate-500">{qualityReason}</p>

          {reducedLabel ? (
            <p className="mt-2 text-xs text-amber-400" data-testid="mobile-reduced">
              Quality is being held at {reducedLabel.toLowerCase()} to keep the broadcast smooth. It
              goes back up on its own when your connection settles.
            </p>
          ) : null}
        </fieldset>

        <dl className="mb-5 grid grid-cols-2 gap-3 text-sm">
          <Readout label="Sending">
            {measured ? formatBitrate(measured.bitrateKbps) : "Not publishing"}
          </Readout>
          <Readout label="Frame rate">{measured ? `${measured.framesPerSecond} fps` : "—"}</Readout>
          <Readout label="Resolution">
            {measured?.width && measured.height
              ? `${measured.width}×${measured.height}`
              : videoFormat
                ? `${videoFormat.width}×${videoFormat.height}`
                : "—"}
          </Readout>
          <Readout label="Packet loss">
            {measured ? `${measured.packetLossPercent.toFixed(1)}%` : "—"}
          </Readout>
        </dl>

        {/* Deliberately not `measured.advice`: that is the studio's wording, written for a desk. */}
        {advice ? (
          <p className="mb-5 text-xs text-amber-400" data-testid="mobile-advice">
            {advice}
          </p>
        ) : null}

        {/*
          Streaming a game from this phone.

          The one thing this broadcaster cannot do for itself: no browser on any phone can record
          another app. A screen-capture encoder app can, and it publishes here (ADR 0022). The key
          lives on the phone that will use it, which is the case this placement exists for.
        */}
        <div className="mb-5">
          <h3 className="mb-2 text-xs font-medium uppercase tracking-wide text-slate-500">
            Stream a game or your screen
          </h3>
          <EncoderPanel streamKey={streamKey} sessionIsEnded={sessionIsEnded} compact />
        </div>

        {destinations.length > 0 ? (
          <div className="mb-5">
            <h3 className="mb-2 text-xs font-medium uppercase tracking-wide text-slate-500">
              Also streaming to
            </h3>

            <ul className="flex flex-col gap-2">
              {destinations.map((destination) => {
                // "Running" is anything the relay is still working on, retries included — stopping
                // a destination that is midway through reconnecting is a thing people want to do.
                const running = !["Idle", "Stopped", "Error", "Disabled"].includes(destination.status);

                return (
                  <li
                    key={destination.id}
                    className="flex items-center justify-between gap-3 rounded-lg bg-slate-900 px-3 py-2"
                  >
                    <div className="min-w-0">
                      <p className="truncate text-sm text-slate-200">{destination.displayName}</p>
                      <p className="text-xs text-slate-500">{providerLabel(destination.provider)}</p>
                    </div>

                    <div className="flex shrink-0 items-center gap-2">
                      <Badge tone={destinationStatusTone(destination.status)}>
                        {destinationStatusLabel(destination.status)}
                      </Badge>

                      <button
                        type="button"
                        disabled={busyDestinationId === destination.id || !destination.enabled}
                        onClick={() =>
                          running ? onStopDestination(destination.id) : onStartDestination(destination.id)
                        }
                        className="min-h-11 rounded-lg bg-slate-800 px-3 text-xs font-semibold text-slate-100 disabled:opacity-50"
                      >
                        {running ? "Stop" : "Start"}
                      </button>
                    </div>
                  </li>
                );
              })}
            </ul>
          </div>
        ) : null}

        {/*
          Adding a destination, composing scenes, inviting other devices: all of it lives in the
          full studio, and none of it belongs on a screen worked one-handed while filming. The link
          is honest about where it goes rather than hiding a half-built version here.

          A plain anchor, deliberately, where the rest of the application uses `Link`. This route
          does not read the query string on the server, so a client-side navigation to it is served
          from the router cache and the subtree is never re-rendered — the address bar changes and
          the screen does not. A document load also rebuilds the capture and transport stack from
          nothing, which is the honest way to hand a live camera from one broadcaster to the other.
        */}
        <a
          href={`/studio/${sessionId}?view=desktop`}
          className="block min-h-11 rounded-lg bg-slate-900 px-4 py-3 text-center text-sm font-medium text-slate-300 ring-1 ring-slate-800"
        >
          Open the full studio
        </a>
      </section>
    </div>
  );
}

function Readout({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="rounded-lg bg-slate-900 px-3 py-2">
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className="mt-0.5 font-mono text-sm tabular-nums text-slate-200">{children}</dd>
    </div>
  );
}
