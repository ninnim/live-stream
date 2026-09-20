"use client";

import { useMemo, useState } from "react";
import { Badge, Button, Card, Field, inputClasses } from "@/components/ui/primitives";
import type { UseDestinationsResult } from "@/hooks/useDestinations";
import {
  destinationStatusLabel,
  destinationStatusTone,
  formatBytes,
  formatDuration,
  providerLabel,
  secondsUntil,
} from "@/lib/format";
import type { Destination, DestinationProvider, ProviderDescriptor } from "@/lib/types";

/**
 * Where the creator sees and controls the external platforms this session is republished to.
 *
 * The design carries the isolation guarantee visually: every destination reports its own status,
 * and a failed one is rendered as an inset problem inside this panel rather than anything that
 * touches the session badge above it. A broadcaster whose Facebook stream dies should be able to
 * see at a glance that their broadcast is still fine.
 */
export function DestinationPanel({
  destinations,
  sessionIsBroadcasting,
  sessionIsEnded,
  controller,
}: {
  destinations: Destination[];
  sessionIsBroadcasting: boolean;
  sessionIsEnded: boolean;
  controller: UseDestinationsResult;
}) {
  const [adding, setAdding] = useState(false);

  const liveCount = useMemo(
    () => destinations.filter((destination) => destination.status === "Live").length,
    [destinations],
  );

  const failedCount = useMemo(
    () => destinations.filter((destination) => destination.status === "Error").length,
    [destinations],
  );

  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-200">Also streaming to</h2>
          <p className="mt-1 text-xs text-slate-500">
            {destinations.length === 0
              ? "Send this broadcast to YouTube, Facebook, TikTok, Twitch, or any RTMP endpoint."
              : `${destinations.length} destination${destinations.length === 1 ? "" : "s"}` +
                (liveCount > 0 ? ` · ${liveCount} live` : "") +
                (failedCount > 0 ? ` · ${failedCount} failed` : "")}
          </p>
        </div>

        {!sessionIsEnded ? (
          <Button variant="secondary" onClick={() => setAdding((open) => !open)}>
            {adding ? "Cancel" : "Add destination"}
          </Button>
        ) : null}
      </div>

      {controller.error ? (
        <p className="mt-3 rounded-lg bg-red-950/60 px-3 py-2 text-xs text-red-300 ring-1 ring-inset ring-red-900">
          {controller.error}
        </p>
      ) : null}

      {adding ? (
        <AddDestinationForm
          providers={controller.providers}
          busy={controller.busyId === "new"}
          onCancel={() => setAdding(false)}
          onSubmit={async (input) => {
            await controller.add(input);
            setAdding(false);
          }}
        />
      ) : null}

      {destinations.length > 0 ? (
        <ul className="mt-4 flex flex-col gap-2">
          {destinations.map((destination) => (
            <DestinationRow
              key={destination.id}
              destination={destination}
              sessionIsBroadcasting={sessionIsBroadcasting}
              sessionIsEnded={sessionIsEnded}
              busy={controller.busyId === destination.id}
              controller={controller}
            />
          ))}
        </ul>
      ) : null}
    </Card>
  );
}

function DestinationRow({
  destination,
  sessionIsBroadcasting,
  sessionIsEnded,
  busy,
  controller,
}: {
  destination: Destination;
  sessionIsBroadcasting: boolean;
  sessionIsEnded: boolean;
  busy: boolean;
  controller: UseDestinationsResult;
}) {
  const isActive = ["Preparing", "Connecting", "Live", "Retrying"].includes(destination.status);
  const retryIn = secondsUntil(destination.nextRetryAt);

  return (
    <li className="rounded-lg border border-slate-800 bg-slate-950/40 p-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="truncate text-sm font-medium text-slate-100">{destination.displayName}</span>
            <Badge tone={destinationStatusTone(destination.status)} pulse={destination.status === "Live"}>
              {destinationStatusLabel(destination.status)}
            </Badge>
          </div>

          <p className="mt-1 text-xs text-slate-500">
            {providerLabel(destination.provider)}
            {destination.providerAccountName ? ` · ${destination.providerAccountName}` : ""}
            {destination.status === "Live" && destination.uptimeSeconds !== null
              ? ` · ${formatDuration(destination.uptimeSeconds)} · ${formatBytes(destination.bytesSent)} sent`
              : ""}
          </p>
        </div>

        <div className="flex shrink-0 items-center gap-2">
          {destination.watchUrl ? (
            <a
              href={destination.watchUrl}
              target="_blank"
              rel="noreferrer noopener"
              className="text-xs text-sky-400 underline-offset-4 hover:underline"
            >
              View on {providerLabel(destination.provider)}
            </a>
          ) : null}

          {sessionIsBroadcasting && !isActive && destination.enabled ? (
            <Button variant="secondary" className="px-3 py-1.5 text-xs" disabled={busy}
              onClick={() => void controller.start(destination.id).catch(() => undefined)}>
              {busy ? "Starting…" : destination.status === "Error" ? "Retry" : "Start"}
            </Button>
          ) : null}

          {isActive ? (
            <Button variant="ghost" className="px-3 py-1.5 text-xs" disabled={busy}
              onClick={() => void controller.stop(destination.id).catch(() => undefined)}>
              {busy ? "Stopping…" : "Stop"}
            </Button>
          ) : null}

          {!isActive && !sessionIsEnded ? (
            <>
              <Button variant="ghost" className="px-3 py-1.5 text-xs" disabled={busy}
                onClick={() =>
                  void controller.setEnabled(destination.id, !destination.enabled).catch(() => undefined)
                }>
                {destination.enabled ? "Turn off" : "Turn on"}
              </Button>
              <Button variant="ghost" className="px-3 py-1.5 text-xs text-red-400 hover:text-red-300"
                disabled={busy}
                onClick={() => void controller.remove(destination.id).catch(() => undefined)}>
                Remove
              </Button>
            </>
          ) : null}
        </div>
      </div>

      {/*
        A destination problem is stated plainly and kept inside this row. It never becomes a
        page-level banner, because the broadcast itself is not in trouble.
      */}
      {destination.status === "Error" && destination.lastErrorMessage ? (
        <p className="mt-2 rounded bg-red-950/50 px-2.5 py-1.5 text-xs text-red-300">
          {destination.lastErrorMessage} Your broadcast is unaffected.
        </p>
      ) : null}

      {destination.status === "Retrying" ? (
        <p className="mt-2 text-xs text-amber-400">
          Reconnecting{retryIn > 0 ? ` in ${retryIn}s` : "…"} (attempt {destination.attemptCount})
        </p>
      ) : null}
    </li>
  );
}

/**
 * The add form.
 *
 * The stream key is a password field and is never read back from the server, so it exists only
 * between this input and the request that stores it.
 */
function AddDestinationForm({
  providers,
  busy,
  onCancel,
  onSubmit,
}: {
  providers: ProviderDescriptor[];
  busy: boolean;
  onCancel: () => void;
  onSubmit: (input: {
    provider: DestinationProvider;
    displayName: string;
    ingestUrl?: string | null;
    streamKey?: string | null;
  }) => Promise<void>;
}) {
  const [provider, setProvider] = useState<DestinationProvider>("YouTube");
  const [displayName, setDisplayName] = useState("");
  const [ingestUrl, setIngestUrl] = useState("");
  const [streamKey, setStreamKey] = useState("");

  const descriptor = providers.find((candidate) => candidate.provider === provider);

  // Most platforms publish a fixed endpoint, so only the ones that do not need the field.
  const needsIngestUrl = !descriptor?.defaultIngestUrl;
  const effectiveIngestUrl = needsIngestUrl ? ingestUrl.trim() : (descriptor?.defaultIngestUrl ?? "");

  const canSubmit =
    displayName.trim().length > 0 && streamKey.trim().length > 0 && effectiveIngestUrl.length > 0 && !busy;

  return (
    <form
      className="mt-4 flex flex-col gap-3 rounded-lg border border-slate-800 bg-slate-950/40 p-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (!canSubmit) return;

        void onSubmit({
          provider,
          displayName: displayName.trim(),
          ingestUrl: effectiveIngestUrl,
          streamKey: streamKey.trim(),
        }).catch(() => undefined);
      }}
    >
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Platform">
          <select
            className={inputClasses}
            value={provider}
            aria-label="Platform"
            onChange={(event) => {
              const next = event.target.value as DestinationProvider;
              setProvider(next);
              setIngestUrl("");
              if (displayName.trim().length === 0) {
                setDisplayName(providerLabel(next));
              }
            }}
          >
            {(providers.length > 0
              ? providers
              : ([{ provider: "CustomRtmp", displayName: "Custom RTMP" }] as ProviderDescriptor[])
            ).map((candidate) => (
              <option key={candidate.provider} value={candidate.provider}>
                {candidate.displayName}
              </option>
            ))}
          </select>
        </Field>

        <Field label="Name" hint="How this destination appears in your studio.">
          <input
            className={inputClasses}
            value={displayName}
            aria-label="Name"
            placeholder="My channel"
            onChange={(event) => setDisplayName(event.target.value)}
          />
        </Field>
      </div>

      {needsIngestUrl ? (
        <Field label="Server URL" hint="The rtmp:// or rtmps:// address the platform gave you.">
          <input
            className={inputClasses}
            value={ingestUrl}
            aria-label="Server URL"
            placeholder="rtmp://live.example.com/app"
            onChange={(event) => setIngestUrl(event.target.value)}
          />
        </Field>
      ) : null}

      <Field label="Stream key" hint={descriptor?.streamKeyHelp}>
        <input
          className={inputClasses}
          type="password"
          value={streamKey}
          aria-label="Stream key"
          autoComplete="off"
          placeholder="Paste your stream key"
          onChange={(event) => setStreamKey(event.target.value)}
        />
      </Field>

      {descriptor?.helpUrl ? (
        <a
          href={descriptor.helpUrl}
          target="_blank"
          rel="noreferrer noopener"
          className="text-xs text-sky-400 underline-offset-4 hover:underline"
        >
          Where do I find my {descriptor.displayName} stream key?
        </a>
      ) : null}

      <div className="flex justify-end gap-2">
        <Button variant="ghost" onClick={onCancel} type="button">
          Cancel
        </Button>
        <button
          type="submit"
          disabled={!canSubmit}
          className="inline-flex items-center justify-center rounded-lg bg-sky-500 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-sky-400 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {busy ? "Adding…" : "Add destination"}
        </button>
      </div>
    </form>
  );
}
