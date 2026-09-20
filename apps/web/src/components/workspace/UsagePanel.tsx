"use client";

import { Card, Metric } from "@/components/ui/primitives";
import type { WorkspaceUsage } from "@/lib/types";

function money(amount: number | null, currency: string): string {
  // Null is not zero. A cost line the deployment has no rate for is unknown, and showing it as
  // free is how a bill becomes a surprise.
  if (amount === null) return "—";

  return new Intl.NumberFormat(undefined, {
    style: "currency",
    currency,
    minimumFractionDigits: 2,
    maximumFractionDigits: amount < 1 ? 4 : 2,
  }).format(amount);
}

function dateRange(from: string, to: string): string {
  const format = (value: string): string =>
    new Date(value).toLocaleDateString(undefined, { month: "short", day: "numeric" });

  return `${format(from)} – ${format(to)}`;
}

/**
 * Capacity and cost for one workspace (implementation/phase-7: capacity and cost visibility).
 *
 * Every figure here comes from a stored row rather than a model of what usage probably was, and
 * what the platform does not measure is named at the bottom instead of being left at zero.
 */
export function UsagePanel({ usage }: { usage: WorkspaceUsage }) {
  const { capacity, cost } = usage;
  const headroom = capacity.maxConcurrentSessions - capacity.openSessions;

  return (
    <Card>
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-lg font-semibold text-slate-100">Usage</h2>
        <p className="text-xs text-slate-500">{dateRange(usage.periodStart, usage.periodEnd)}</p>
      </div>

      <dl className="mt-4 grid grid-cols-2 gap-4 sm:grid-cols-4">
        <Metric
          label="Open sessions"
          value={`${capacity.openSessions} / ${capacity.maxConcurrentSessions}`}
          hint={headroom > 0 ? `${headroom} more allowed` : "At the limit"}
        />
        <Metric label="On air now" value={`${capacity.broadcastingSessions}`} />
        <Metric label="Broadcast hours" value={usage.usage.streamingHours.toFixed(1)} />
        <Metric
          label="Recordings stored"
          value={`${usage.usage.storedRecordingGb.toFixed(2)} GB`}
          hint={`${usage.usage.recordingsStored} kept for ${capacity.recordingRetentionDays} days`}
        />
      </dl>

      <table className="mt-6 w-full text-sm">
        <caption className="sr-only">Estimated cost by line</caption>
        <thead>
          <tr className="text-left text-xs uppercase tracking-wide text-slate-500">
            <th scope="col" className="pb-2 font-medium">Item</th>
            <th scope="col" className="pb-2 text-right font-medium">Quantity</th>
            <th scope="col" className="pb-2 text-right font-medium">Cost</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-800">
          {cost.lines.map((line) => (
            <tr key={line.key}>
              <th scope="row" className="py-2 text-left font-normal text-slate-300">
                {line.label}
              </th>
              <td className="py-2 text-right tabular-nums text-slate-400">
                {line.quantity.toLocaleString(undefined, { maximumFractionDigits: 2 })} {line.unit}
              </td>
              <td className="py-2 text-right tabular-nums text-slate-200">
                {money(line.amount, cost.currency)}
              </td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr className="border-t border-slate-700">
            <th scope="row" className="pt-2 text-left font-medium text-slate-200">
              Estimated total
            </th>
            <td />
            <td className="pt-2 text-right font-medium tabular-nums text-slate-100">
              {money(cost.estimatedTotal, cost.currency)}
            </td>
          </tr>
        </tfoot>
      </table>

      {!cost.ratesConfigured ? (
        <p className="mt-3 text-xs text-amber-300">
          No unit rates are configured for this deployment, so only measured AI spend is priced.
        </p>
      ) : null}

      <div className="mt-4 rounded-lg bg-slate-900/60 p-3">
        <p className="text-xs font-medium text-slate-300">Not included in this estimate</p>
        <ul className="mt-1 list-disc space-y-0.5 pl-4 text-xs text-slate-500">
          {cost.notMetered.map((note) => (
            <li key={note}>{note}</li>
          ))}
        </ul>
      </div>
    </Card>
  );
}
