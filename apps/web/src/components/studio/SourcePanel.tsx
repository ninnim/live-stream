"use client";

import { useEffect, useState } from "react";
import QRCode from "qrcode";
import { Badge, Button, Card, Field, inputClasses } from "@/components/ui/primitives";
import type { UseSourcesResult } from "@/hooks/useSources";
import { formatBitrate, secondsSince } from "@/lib/format";
import type { SessionSource, SourceInvitation, SourceRole, SourceStatus } from "@/lib/types";

/**
 * The devices contributing to this session.
 *
 * Presence here is always the server's observation, never the browser's guess: a tile says
 * "Connected" because the media plane reported media on that source's path, which is what makes it
 * trustworthy when a phone dies silently rather than disconnecting politely.
 */
export function SourcePanel({
  sources,
  sessionIsEnded,
  controller,
}: {
  sources: SessionSource[];
  sessionIsEnded: boolean;
  controller: UseSourcesResult;
}) {
  const [inviting, setInviting] = useState(false);

  const contributors = sources.filter((source) => source.role !== "Host");
  const connected = contributors.filter((source) => source.status === "Connected").length;

  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-200">Devices</h2>
          <p className="mt-1 text-xs text-slate-500">
            {contributors.length === 0
              ? "Add a phone as a second camera, or bring in a screen share."
              : `${contributors.length} device${contributors.length === 1 ? "" : "s"}` +
                (connected > 0 ? ` · ${connected} sending` : "")}
          </p>
        </div>

        {!sessionIsEnded ? (
          <Button variant="secondary" onClick={() => setInviting((open) => !open)}>
            {inviting ? "Cancel" : "Add device"}
          </Button>
        ) : null}
      </div>

      {controller.error ? (
        <p className="mt-3 rounded-lg bg-red-950/60 px-3 py-2 text-xs text-red-300 ring-1 ring-inset ring-red-900">
          {controller.error}
        </p>
      ) : null}

      {controller.invitation ? (
        <InvitationCard invitation={controller.invitation} onDismiss={controller.dismissInvitation} />
      ) : null}

      {inviting && !controller.invitation ? (
        <InviteForm
          busy={controller.busyId === "new"}
          onCancel={() => setInviting(false)}
          onSubmit={async (role, displayName) => {
            await controller.invite(role, displayName);
            setInviting(false);
          }}
        />
      ) : null}

      <ul className="mt-4 flex flex-col gap-2">
        {sources.map((source) => (
          <SourceRow
            key={source.id}
            source={source}
            sessionIsEnded={sessionIsEnded}
            busy={controller.busyId === source.id}
            onRevoke={() => void controller.revoke(source.id).catch(() => undefined)}
          />
        ))}
      </ul>
    </Card>
  );
}

const STATUS_LABELS: Record<SourceStatus, string> = {
  Invited: "Waiting to join",
  Paired: "Joined",
  Connected: "Sending",
  Disconnected: "No signal",
  Revoked: "Removed",
};

const ROLE_LABELS: Record<SourceRole, string> = {
  Host: "Studio",
  Camera: "Camera",
  Screen: "Screen share",
  Audio: "Audio",
  Moderator: "Moderator",
  Operator: "Operator",
  ViewerMonitor: "Monitor",
};

function statusTone(status: SourceStatus) {
  switch (status) {
    case "Connected":
      return "positive" as const;
    case "Paired":
    case "Invited":
      return "caution" as const;
    case "Disconnected":
      return "critical" as const;
    default:
      return "neutral" as const;
  }
}

function SourceRow({
  source,
  sessionIsEnded,
  busy,
  onRevoke,
}: {
  source: SessionSource;
  sessionIsEnded: boolean;
  busy: boolean;
  onRevoke: () => void;
}) {
  const isHost = source.role === "Host";
  const idleSeconds = source.status === "Disconnected" ? secondsSince(source.lastSeenAt) : 0;

  return (
    <li className="rounded-lg border border-slate-800 bg-slate-950/40 p-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="truncate text-sm font-medium text-slate-100">{source.displayName}</span>
            <Badge tone={statusTone(source.status)} pulse={source.status === "Connected"}>
              {STATUS_LABELS[source.status]}
            </Badge>
            {source.isProgram ? <Badge tone="live">On air</Badge> : null}
          </div>

          <p className="mt-1 text-xs text-slate-500">
            {/*
              The studio source is named after its role, so repeating it would render
              "Studio · Studio". Devices keep the role line, which is how an operator tells a
              second camera from a screen share at a glance.
            */}
            {source.displayName === ROLE_LABELS[source.role] ? "" : ROLE_LABELS[source.role]}
            {source.status === "Connected" && source.bitrateKbps !== null
              ? `${source.displayName === ROLE_LABELS[source.role] ? "" : " · "}${formatBitrate(source.bitrateKbps)}`
              : ""}
            {source.status === "Disconnected" && idleSeconds > 0
              ? ` · last seen ${idleSeconds}s ago`
              : ""}
          </p>
        </div>

        {/*
          The studio source has no Remove button on purpose: it is the broadcast itself, and the
          way to end it is to stop the session.
        */}
        {!isHost && !sessionIsEnded && source.status !== "Revoked" ? (
          <Button
            variant="ghost"
            className="px-3 py-1.5 text-xs text-red-400 hover:text-red-300"
            disabled={busy}
            onClick={onRevoke}
          >
            {busy ? "Removing…" : "Remove"}
          </Button>
        ) : null}
      </div>
    </li>
  );
}

/**
 * Shows a freshly minted pairing code.
 *
 * Deliberately persistent rather than a toast: the code cannot be retrieved again, and someone has
 * to read it onto a phone or scan it.
 */
function InvitationCard({
  invitation,
  onDismiss,
}: {
  invitation: SourceInvitation;
  onDismiss: () => void;
}) {
  const [qrDataUrl, setQrDataUrl] = useState<string | null>(null);
  const [secondsLeft, setSecondsLeft] = useState(invitation.expiresInSeconds);

  useEffect(() => {
    let cancelled = false;

    void QRCode.toDataURL(invitation.joinUrl, { width: 240, margin: 1 })
      .then((url) => {
        if (!cancelled) setQrDataUrl(url);
      })
      .catch(() => {
        // The code and link below still work without the QR image.
      });

    return () => {
      cancelled = true;
    };
  }, [invitation.joinUrl]);

  // The countdown is the point: a code that has quietly expired should say so rather than sending
  // someone to type it in vain.
  useEffect(() => {
    const expiresAt = Date.parse(invitation.expiresAt);
    const tick = () => setSecondsLeft(Math.max(0, Math.ceil((expiresAt - Date.now()) / 1000)));

    tick();
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  }, [invitation.expiresAt]);

  const expired = secondsLeft <= 0;

  return (
    <div className="mt-4 rounded-lg border border-sky-900 bg-sky-950/30 p-4">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h3 className="text-sm font-semibold text-slate-100">Join this session</h3>
          <p className="mt-1 text-xs text-slate-400">
            Scan the code, or open the link and enter the code below.
          </p>

          <p className="mt-3 font-mono text-2xl font-semibold tracking-[0.2em] text-sky-300">
            {invitation.pairingCode}
          </p>

          <p className={`mt-2 text-xs ${expired ? "text-red-400" : "text-slate-500"}`}>
            {expired
              ? "This code has expired. Add the device again for a new one."
              : `Expires in ${Math.floor(secondsLeft / 60)}m ${secondsLeft % 60}s · single use`}
          </p>

          <p className="mt-2 break-all text-xs text-slate-500">{invitation.joinUrl}</p>
        </div>

        {qrDataUrl && !expired ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img
            src={qrDataUrl}
            alt={`QR code to join as ${invitation.source.displayName}`}
            className="size-32 rounded bg-white p-1"
          />
        ) : null}
      </div>

      <div className="mt-3 flex justify-end">
        <Button variant="ghost" className="px-3 py-1.5 text-xs" onClick={onDismiss}>
          Done
        </Button>
      </div>
    </div>
  );
}

function InviteForm({
  busy,
  onCancel,
  onSubmit,
}: {
  busy: boolean;
  onCancel: () => void;
  onSubmit: (role: SourceRole, displayName: string) => Promise<void>;
}) {
  const [role, setRole] = useState<SourceRole>("Camera");
  const [displayName, setDisplayName] = useState("Second camera");

  const canSubmit = displayName.trim().length > 0 && !busy;

  return (
    <form
      className="mt-4 flex flex-col gap-3 rounded-lg border border-slate-800 bg-slate-950/40 p-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (!canSubmit) return;
        void onSubmit(role, displayName.trim()).catch(() => undefined);
      }}
    >
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Role" hint="What this device is for. It cannot be changed by whoever joins.">
          <select
            className={inputClasses}
            value={role}
            aria-label="Role"
            onChange={(event) => {
              const next = event.target.value as SourceRole;
              setRole(next);
              setDisplayName(ROLE_LABELS[next]);
            }}
          >
            <option value="Camera">Camera</option>
            <option value="Screen">Screen share</option>
            <option value="Audio">Audio</option>
            <option value="Moderator">Moderator</option>
            <option value="Operator">Operator</option>
          </select>
        </Field>

        <Field label="Name" hint="How this device appears in your control room.">
          <input
            className={inputClasses}
            value={displayName}
            aria-label="Device name"
            onChange={(event) => setDisplayName(event.target.value)}
          />
        </Field>
      </div>

      <div className="flex justify-end gap-2">
        <Button variant="ghost" type="button" onClick={onCancel}>
          Cancel
        </Button>
        <button
          type="submit"
          disabled={!canSubmit}
          className="inline-flex items-center justify-center rounded-lg bg-sky-500 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-sky-400 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {busy ? "Creating…" : "Create pairing code"}
        </button>
      </div>
    </form>
  );
}
