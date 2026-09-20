"use client";

import { useState } from "react";
import { Button, Card, Field, inputClasses } from "@/components/ui/primitives";
import type { WorkspaceErasure } from "@/lib/types";

/**
 * Export and erasure (implementation/phase-7: "Compliance/retention controls").
 *
 * Erasure asks for the workspace name rather than a yes/no dialog. Nothing it removes comes back,
 * and a confirmation somebody can click through without reading is not a confirmation.
 */
export function DataPanel({
  workspaceName,
  onExport,
  onErase,
}: {
  workspaceName: string;
  onExport: () => Promise<unknown>;
  onErase: (confirmation: string) => Promise<WorkspaceErasure>;
}) {
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState<"export" | "erase" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [erased, setErased] = useState<WorkspaceErasure | null>(null);

  const exportData = async (): Promise<void> => {
    setBusy("export");
    setError(null);

    try {
      const data = await onExport();
      download(`${slug(workspaceName)}-export.json`, JSON.stringify(data, null, 2));
    } catch (exportError) {
      setError(exportError instanceof Error ? exportError.message : "We could not build that export.");
    } finally {
      setBusy(null);
    }
  };

  const erase = async (event: React.FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy("erase");
    setError(null);

    try {
      setErased(await onErase(confirmation));
    } catch (eraseError) {
      setError(eraseError instanceof Error ? eraseError.message : "We could not erase this workspace.");
    } finally {
      setBusy(null);
    }
  };

  return (
    <Card>
      <h2 className="text-lg font-semibold text-slate-100">Your data</h2>

      <p className="mt-1 text-sm text-slate-400">
        The export contains sessions, events, recordings metadata and members. It carries no stream
        keys or other secrets.
      </p>

      <div className="mt-4">
        <Button variant="secondary" disabled={busy !== null} onClick={() => void exportData()}>
          {busy === "export" ? "Preparing…" : "Download export"}
        </Button>
      </div>

      <hr className="my-6 border-slate-800" />

      <h3 className="text-sm font-semibold text-red-300">Erase this workspace</h3>
      <p className="mt-1 text-sm text-slate-400">
        Deletes every session, recording and setting, and the recorded media itself. This cannot be
        undone.
      </p>

      {erased ? (
        <p role="status" className="mt-3 text-sm text-emerald-400">
          Erased {erased.sessionsDeleted} sessions and {erased.recordingsDeleted} recordings.
        </p>
      ) : (
        <form onSubmit={erase} className="mt-3 flex flex-col gap-3">
          <Field label={`Type "${workspaceName}" to confirm`}>
            <input
              type="text"
              className={inputClasses}
              value={confirmation}
              onChange={(event) => setConfirmation(event.target.value)}
            />
          </Field>

          <div>
            <Button
              type="submit"
              variant="danger"
              disabled={busy !== null || confirmation !== workspaceName}
            >
              {busy === "erase" ? "Erasing…" : "Erase workspace"}
            </Button>
          </div>
        </form>
      )}

      {error ? (
        <p role="alert" className="mt-3 text-sm text-red-400">
          {error}
        </p>
      ) : null}
    </Card>
  );
}

function slug(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "workspace";
}

/** Hands the file to the browser without a round trip: the export is already in memory. */
function download(filename: string, contents: string): void {
  const url = URL.createObjectURL(new Blob([contents], { type: "application/json" }));
  const anchor = document.createElement("a");

  anchor.href = url;
  anchor.download = filename;
  anchor.click();

  URL.revokeObjectURL(url);
}
