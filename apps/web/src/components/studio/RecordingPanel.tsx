"use client";

import { Badge, Button, Card } from "@/components/ui/primitives";
import type { UseLocalRecordingResult } from "@/hooks/useLocalRecording";
import { formatBytes, formatDuration } from "@/lib/format";

/**
 * Recording to a file on this computer, without broadcasting.
 *
 * Deliberately its own card rather than a mode the studio is put into. Not every session is a show
 * — a walkthrough, a bug report, a lesson — and those want the same screen share, the same
 * microphone handling and the same preview as going live, with none of the audience. Making it a
 * mode would mean deciding which one this session is before finding out.
 *
 * It sits alongside Start Live rather than replacing it, so recording a broadcast while it goes out
 * is simply allowed. Refusing that would be arbitrary.
 */
export function RecordingPanel({
  recorder,
  hasCapture,
}: {
  recorder: UseLocalRecordingResult;
  /** Whether anything is open to record. Nothing to record is a reason to explain, not to hide. */
  hasCapture: boolean;
}) {
  const { recording, starting } = recorder;

  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <h2 className="text-sm font-semibold text-slate-100">Record to this computer</h2>
            {recording ? <Badge tone="critical">Recording</Badge> : null}
          </div>

          <p className="mt-1 max-w-md text-sm text-slate-400">
            {recording
              ? "Saving to the file you chose. Nothing is being broadcast by this."
              : "Saves a video file to your computer. Nothing is uploaded and nobody can watch it — use this when you just want a recording."}
          </p>
        </div>

        <div className="flex items-center gap-3">
          {recording ? (
            <>
              {/*
                Duration and size together: one says the recording is running, the other says it is
                actually being written. A timer alone would keep counting over a dead recorder.
              */}
              <div className="text-right">
                <p className="font-mono text-lg text-slate-100 tabular-nums">
                  {formatDuration(recorder.elapsedSeconds)}
                </p>
                <p className="text-xs text-slate-500">{formatBytes(recorder.bytesWritten)}</p>
              </div>

              <Button variant="secondary" onClick={() => void recorder.stop()}>
                Stop recording
              </Button>
            </>
          ) : (
            <Button
              variant="secondary"
              onClick={() => void recorder.start()}
              disabled={!hasCapture || !recorder.supported || starting}
            >
              {starting ? "Choose where to save…" : "Start recording"}
            </Button>
          )}
        </div>
      </div>

      {/*
        Why the button is disabled, rather than a button that does nothing when pressed.
      */}
      {!hasCapture ? (
        <p className="mt-3 text-xs text-slate-500">
          Open a camera, a microphone, or share a screen first — there is nothing to record yet.
        </p>
      ) : !recorder.supported ? (
        <p className="mt-3 text-xs text-slate-500">
          This browser cannot record video. Recording works in Chrome, Edge, and Firefox.
        </p>
      ) : null}

      {/*
        Only where the browser could not stream to disk. Said before it becomes a problem: the
        failure mode is a tab running out of memory an hour into something unrepeatable.
      */}
      {recording && recorder.buffersInMemory ? (
        <p className="mt-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-200">
          This browser holds the recording in memory until you stop, so keep it short. Chrome and
          Edge write straight to disk and have no such limit.
        </p>
      ) : null}

      {recorder.error ? (
        <p
          role="alert"
          className="mt-3 rounded-lg border border-rose-500/30 bg-rose-500/10 px-3 py-2 text-xs text-rose-200"
        >
          {recorder.error}
        </p>
      ) : null}

      {recorder.notice ? (
        <div className="mt-3 flex items-start justify-between gap-3 rounded-lg border border-slate-700 bg-slate-900/60 px-3 py-2">
          <p role="status" className="text-xs text-slate-300">
            {recorder.notice}
          </p>
          <button
            type="button"
            onClick={recorder.dismiss}
            className="text-xs text-slate-500 hover:text-slate-300"
          >
            Dismiss
          </button>
        </div>
      ) : null}
    </Card>
  );
}
