"use client";

import Link from "next/link";
import { Badge } from "@/components/ui/primitives";
import { formatDuration, formatViewers, statusLabel, statusTone } from "@/lib/format";
import type { QualityVerdict } from "@/lib/media/quality";
import type { LiveSessionStatus } from "@/lib/types";

interface MobileStatusBarProps {
  title: string;
  status: LiveSessionStatus;
  isLive: boolean;
  elapsedSeconds: number;
  viewerCount: number | null;
  /** Null before the first measurement, which is most of the time before going live. */
  verdict: QualityVerdict | null;
  /** True while the ladder is holding the broadcast below full quality. */
  reduced: boolean;
  onOpenDetails: () => void;
}

const VERDICT_BARS: Record<QualityVerdict, number> = { good: 3, fair: 2, poor: 1 };
const VERDICT_COLOR: Record<QualityVerdict, string> = {
  good: "bg-emerald-400",
  fair: "bg-amber-400",
  poor: "bg-red-400",
};

/**
 * The top strip: what the session is doing, for how long, and how well it is getting out.
 *
 * Connection quality is three bars rather than a number. The numbers are all available one tap
 * away, and they are the wrong thing to put in front of somebody filming: nobody holding a phone
 * is going to act on 1,850 kbps, and everybody understands one bar.
 */
export function MobileStatusBar({
  title,
  status,
  isLive,
  elapsedSeconds,
  viewerCount,
  verdict,
  reduced,
  onOpenDetails,
}: MobileStatusBarProps) {
  return (
    <div className="flex items-start justify-between gap-3 bg-gradient-to-b from-black/70 to-transparent px-4 pb-8 pt-[max(0.75rem,env(safe-area-inset-top))]">
      <Link
        href="/dashboard"
        aria-label="Back to sessions"
        className="flex size-10 shrink-0 items-center justify-center rounded-full bg-black/50 text-lg text-slate-200 ring-1 ring-white/15"
      >
        ‹
      </Link>

      <div className="min-w-0 flex-1 text-center">
        <div className="flex items-center justify-center gap-2">
          <Badge tone={statusTone(status)} pulse={isLive}>
            {statusLabel(status)}
          </Badge>
          {isLive ? (
            <span className="font-mono text-sm tabular-nums text-slate-100" data-testid="mobile-timer">
              {formatDuration(elapsedSeconds)}
            </span>
          ) : null}
        </div>

        {/*
          The count needs its noun. On the desktop it sits under a "Viewers" label; here it has no
          label to borrow, and a bare "0" under a running timer reads as a fault rather than as an
          audience that has not arrived yet.
        */}
        <p className="mt-1 truncate text-xs text-slate-400">
          {isLive && viewerCount !== null ? `${formatViewers(viewerCount)} watching` : title}
        </p>
      </div>

      <button
        type="button"
        onClick={onOpenDetails}
        aria-label="Broadcast settings and connection details"
        className="relative flex size-10 shrink-0 items-center justify-center gap-0.5 rounded-full bg-black/50 ring-1 ring-white/15"
        data-testid="mobile-details"
      >
        {[1, 2, 3].map((bar) => (
          <span
            key={bar}
            aria-hidden="true"
            className={`w-1 rounded-full ${
              verdict && VERDICT_BARS[verdict] >= bar ? VERDICT_COLOR[verdict] : "bg-slate-600"
            }`}
            style={{ height: `${4 + bar * 3}px` }}
          />
        ))}
        {/* A dot rather than a word: the sentence explaining it is inside, and there is no room here. */}
        {reduced ? (
          <span className="absolute -bottom-0.5 size-1.5 rounded-full bg-amber-400" aria-hidden="true" />
        ) : null}
      </button>
    </div>
  );
}
