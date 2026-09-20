"use client";

import { useState } from "react";
import { Badge, Button, Card, Field, inputClasses } from "@/components/ui/primitives";
import type { UpdateWorkspaceLimits, WorkspaceLimits } from "@/lib/types";

interface LimitRow {
  key: keyof UpdateWorkspaceLimits;
  label: string;
  planMax: number;
  override: number | null;
  effective: number;
  unit: string;
}

function rows(limits: WorkspaceLimits): LimitRow[] {
  return [
    {
      key: "maxConcurrentSessions",
      label: "Open sessions at once",
      planMax: limits.planMaxConcurrentSessions,
      override: limits.maxConcurrentSessionsOverride,
      effective: limits.effectiveMaxConcurrentSessions,
      unit: "sessions",
    },
    {
      key: "maxDestinationsPerSession",
      label: "Destinations per session",
      planMax: limits.planMaxDestinationsPerSession,
      override: limits.maxDestinationsPerSessionOverride,
      effective: limits.effectiveMaxDestinationsPerSession,
      unit: "destinations",
    },
    {
      key: "maxSourcesPerSession",
      label: "Cameras per session",
      planMax: limits.planMaxSourcesPerSession,
      override: limits.maxSourcesPerSessionOverride,
      effective: limits.effectiveMaxSourcesPerSession,
      unit: "sources",
    },
    {
      key: "recordingRetentionDays",
      label: "Keep recordings for",
      planMax: limits.planRecordingRetentionDays,
      override: limits.recordingRetentionDaysOverride,
      effective: limits.effectiveRecordingRetentionDays,
      unit: "days",
    },
  ];
}

/**
 * Plan and tenant limits.
 *
 * The rule the form has to make obvious: a workspace can set itself a smaller number, never a
 * bigger one. Leaving a field empty means "whatever the plan allows", which is why the inputs are
 * optional rather than pre-filled with the effective value — pre-filling would turn every save into
 * a permanent override of a number the customer never chose.
 */
export function LimitsPanel({
  limits,
  canEdit,
  onSave,
}: {
  limits: WorkspaceLimits;
  canEdit: boolean;
  onSave: (update: UpdateWorkspaceLimits) => Promise<void>;
}) {
  const [draft, setDraft] = useState<Record<string, string>>(() =>
    Object.fromEntries(rows(limits).map((row) => [row.key, row.override?.toString() ?? ""])),
  );
  const [region, setRegion] = useState(limits.residencyRegion ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const submit = async (event: React.FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setSaved(false);

    const numberOrNull = (value: string): number | null => {
      const trimmed = value.trim();
      return trimmed === "" ? null : Number(trimmed);
    };

    try {
      await onSave({
        maxConcurrentSessions: numberOrNull(draft.maxConcurrentSessions ?? ""),
        maxDestinationsPerSession: numberOrNull(draft.maxDestinationsPerSession ?? ""),
        maxSourcesPerSession: numberOrNull(draft.maxSourcesPerSession ?? ""),
        recordingRetentionDays: numberOrNull(draft.recordingRetentionDays ?? ""),
        residencyRegion: region.trim() === "" ? null : region.trim(),
      });
      setSaved(true);
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "We could not save those limits.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-lg font-semibold text-slate-100">Plan and limits</h2>
        <Badge tone="neutral">{limits.plan}</Badge>
      </div>

      <p className="mt-1 text-sm text-slate-400">
        A workspace setting can lower a limit below the plan, not raise it above.{" "}
        {limits.deploymentRegion ? `This deployment serves ${limits.deploymentRegion}.` : null}
      </p>

      <form onSubmit={submit} className="mt-4 flex flex-col gap-4">
        {rows(limits).map((row) => (
          <Field
            key={row.key}
            label={row.label}
            hint={`Plan allows ${row.planMax} ${row.unit}. Currently enforced: ${row.effective}.`}
          >
            <input
              type="number"
              min={1}
              max={row.planMax}
              disabled={!canEdit || busy}
              placeholder={`Plan default (${row.planMax})`}
              className={inputClasses}
              value={draft[row.key] ?? ""}
              onChange={(event) =>
                setDraft((current) => ({ ...current, [row.key]: event.target.value }))
              }
            />
          </Field>
        ))}

        <Field
          label="Data residency"
          hint={
            limits.dataResidencyAllowed
              ? "Sessions are refused in any other region. Leave empty for no requirement."
              : "Available on Business and Enterprise plans."
          }
        >
          <input
            type="text"
            disabled={!canEdit || busy || !limits.dataResidencyAllowed}
            placeholder="No requirement"
            className={inputClasses}
            value={region}
            onChange={(event) => setRegion(event.target.value)}
          />
        </Field>

        {error ? (
          <p role="alert" className="text-sm text-red-400">
            {error}
          </p>
        ) : null}

        {saved ? <p className="text-sm text-emerald-400">Limits saved.</p> : null}

        {canEdit ? (
          <div>
            <Button type="submit" disabled={busy}>
              {busy ? "Saving…" : "Save limits"}
            </Button>
          </div>
        ) : (
          <p className="text-sm text-slate-500">Only workspace administrators can change these.</p>
        )}
      </form>
    </Card>
  );
}
