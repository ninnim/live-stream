"use client";

import { useState } from "react";
import { Button, Card } from "@/components/ui/primitives";
import type { UseStreamKeyResult } from "@/hooks/useStreamKey";

interface EncoderPanelProps {
  streamKey: UseStreamKeyResult;
  sessionIsEnded: boolean;
  /** Compact enough for a phone sheet: no card chrome, tighter copy. */
  compact?: boolean;
}

/**
 * Stream from something that is not this browser.
 *
 * The case this exists for is the one native broadcasting cannot reach: a mobile game. A phone can
 * publish its camera from the browser, but no browser on any phone can record another app, because
 * capturing one app from another needs an OS-level API that only a native app can hold. The way
 * through is an encoder — a screen-capture app on the phone, OBS on a desktop, a capture card —
 * and every one of them speaks RTMP (ADR 0022).
 *
 * The key is shown once and only here. There is no way to read it back, because the server keeps
 * only a hash, so this panel offers rotation rather than retrieval.
 */
export function EncoderPanel({ streamKey, sessionIsEnded, compact = false }: EncoderPanelProps) {
  const { key, busy, error, unavailable } = streamKey;

  const body = (
    <div className={compact ? "" : "flex flex-col gap-4"}>
      {!compact ? (
        <div>
          <h2 className="text-sm font-semibold text-slate-100">Stream from an app or encoder</h2>
          <p className="mt-1 text-xs text-slate-500">
            For a phone game, a console through a capture card, or OBS — anything this browser
            cannot see for itself.
          </p>
        </div>
      ) : null}

      {unavailable ? (
        <p className="text-xs text-slate-500">
          This deployment does not accept external encoders. An administrator enables it by setting
          an RTMP address on the media gateway.
        </p>
      ) : sessionIsEnded ? (
        <p className="text-xs text-slate-500">This session has ended, so it accepts no new video.</p>
      ) : key ? (
        <div className="flex flex-col gap-3">
          <Field label="Server" value={key.serverUrl} />
          <Field label="Stream key" value={key.streamKey} secret />
          {key.srtUrl ? <Field label="SRT (better on a weak signal)" value={key.srtUrl} secret /> : null}

          {/*
            The button is named differently on the two screens, and instructions that name the
            wrong one send people looking for a control that is not there.
          */}
          <p className="text-xs text-slate-500">
            Paste these into the encoder, then press{" "}
            <strong className="text-slate-400">{compact ? "Go live" : "Start Live"}</strong> here. The
            session goes live when your video arrives.
          </p>

          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => void streamKey.reveal()} disabled={busy}>
              {busy ? "Working…" : "Rotate key"}
            </Button>
            <Button variant="ghost" onClick={streamKey.forget} disabled={busy}>
              Hide
            </Button>
            <Button variant="danger" onClick={() => void streamKey.revoke()} disabled={busy}>
              Revoke
            </Button>
          </div>

          <p className="text-xs text-slate-600">
            Shown once. Rotating replaces it immediately, which is also how you make a key you have
            pasted somewhere it should not be harmless.
          </p>
        </div>
      ) : (
        <div className="flex flex-col gap-2">
          <Button variant="secondary" onClick={() => void streamKey.reveal()} disabled={busy}>
            {busy ? "Creating…" : "Create a stream key"}
          </Button>
          <p className="text-xs text-slate-600">
            Creating a new key stops the previous one working.
          </p>
        </div>
      )}

      {error ? (
        <p className="text-xs text-red-400" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );

  return compact ? body : <Card>{body}</Card>;
}

/**
 * One copyable value.
 *
 * A secret is masked until asked for, rather than simply shown. This panel gets opened in front of
 * an audience — the person setting up an encoder is often already streaming something — and a key
 * on screen for a second is a key that has been given away.
 *
 * Copying is the primary action and does not require revealing: almost nobody wants to read a key,
 * they want it in the encoder.
 */
function Field({ label, value, secret = false }: { label: string; value: string; secret?: boolean }) {
  const [shown, setShown] = useState(false);
  const [copied, setCopied] = useState(false);

  const copy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard access can be refused, and a copy button that silently fails is worse than one
      // that reveals the value for the person to select by hand.
      setShown(true);
    }
  };

  const masked = secret && !shown;

  return (
    <div>
      <div className="mb-1 flex items-center justify-between gap-2">
        <span className="text-xs font-medium uppercase tracking-wide text-slate-500">{label}</span>

        <div className="flex gap-2">
          {secret ? (
            <button
              type="button"
              onClick={() => setShown(!shown)}
              className="min-h-11 text-xs font-medium text-slate-400 sm:min-h-0"
            >
              {shown ? "Hide" : "Show"}
            </button>
          ) : null}

          <button
            type="button"
            onClick={() => void copy()}
            data-testid={`copy-${label.toLowerCase().replace(/[^a-z]+/g, "-")}`}
            className="min-h-11 text-xs font-semibold text-sky-400 sm:min-h-0"
          >
            {copied ? "Copied" : "Copy"}
          </button>
        </div>
      </div>

      <p
        className="overflow-x-auto whitespace-nowrap rounded-lg bg-slate-950 px-3 py-2 font-mono text-xs text-slate-300 ring-1 ring-slate-800"
        data-testid={`value-${label.toLowerCase().replace(/[^a-z]+/g, "-")}`}
      >
        {masked ? "•".repeat(Math.min(48, value.length)) : value}
      </p>
    </div>
  );
}
