"use client";

import { useEffect, useRef } from "react";
import { Badge, Button, Card } from "@/components/ui/primitives";
import type { ProgramFeed, UseProgramResult } from "@/hooks/useProgram";
import { LAYOUTS, type LayoutId } from "@/lib/media/layout";

interface ProgramPanelProps {
  program: UseProgramResult;
  /** Cutting is an operator action against the server, so it lives with the source controller. */
  onCut: (sourceId: string) => void;
  busySourceId: string | null;
  sessionIsEnded: boolean;
}

/**
 * The switcher.
 *
 * Every contributing source gets a monitor, and the monitor element *is* what the compositor draws
 * from — there is no second, hidden copy of each video. That keeps one decoder per source rather
 * than two, and makes it impossible for what the operator is watching to drift from what the
 * audience is getting.
 */
export function ProgramPanel({ program, onCut, busySourceId, sessionIsEnded }: ProgramPanelProps) {
  if (program.feeds.length <= 1) {
    // One source is not a show to switch between. The panel appears when a device joins.
    return null;
  }

  return (
    <Card className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-100">Program</h2>
          <p className="mt-0.5 text-xs text-slate-500">
            {program.composing
              ? "Composing in the studio. Cuts are instant."
              : "Sending your camera directly."}
          </p>
        </div>

        <div className="flex flex-wrap gap-2">
          {LAYOUTS.map((option) => (
            <LayoutButton
              key={option.id}
              option={option.id}
              label={option.label}
              active={program.layout === option.id}
              disabled={sessionIsEnded}
              onSelect={program.setLayout}
            />
          ))}
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {program.feeds.map((feed) => (
          <SourceMonitor
            key={feed.source.id}
            feed={feed}
            registerElement={program.registerElement}
            onCut={onCut}
            busy={busySourceId === feed.source.id}
            disabled={sessionIsEnded}
          />
        ))}
      </div>
    </Card>
  );
}

function LayoutButton({
  option,
  label,
  active,
  disabled,
  onSelect,
}: {
  option: LayoutId;
  label: string;
  active: boolean;
  disabled: boolean;
  onSelect: (layout: LayoutId) => void;
}) {
  return (
    <Button
      variant={active ? "primary" : "secondary"}
      aria-pressed={active}
      disabled={disabled}
      onClick={() => onSelect(option)}
    >
      {label}
    </Button>
  );
}

const STATE_LABELS: Record<string, string> = {
  local: "This computer",
  idle: "Waiting",
  connecting: "Connecting",
  connected: "Receiving",
  reconnecting: "Reconnecting",
  failed: "No picture",
  closed: "Stopped",
};

function SourceMonitor({
  feed,
  registerElement,
  onCut,
  busy,
  disabled,
}: {
  feed: ProgramFeed;
  registerElement: (sourceId: string, element: HTMLVideoElement | null) => void;
  onCut: (sourceId: string) => void;
  busy: boolean;
  disabled: boolean;
}) {
  const videoRef = useRef<HTMLVideoElement | null>(null);

  // Assigning `srcObject` is not something React can express as a prop, and re-assigning the same
  // stream restarts playback — so it is guarded on identity rather than set on every render.
  useEffect(() => {
    const element = videoRef.current;
    if (!element || element.srcObject === feed.stream) return;
    element.srcObject = feed.stream;
  }, [feed.stream]);

  const sourceId = feed.source.id;

  useEffect(() => {
    return () => registerElement(sourceId, null);
  }, [registerElement, sourceId]);

  const live = feed.source.isProgram;

  return (
    <div
      className={`flex flex-col gap-2 rounded-xl border p-2 ${
        feed.onAir ? "border-red-500/70 bg-red-950/20" : "border-slate-800 bg-slate-900/40"
      }`}
    >
      <div className="relative aspect-video overflow-hidden rounded-lg bg-black">
        {/*
          Muted is not optional: this is the same audio the mixer is already sending, and playing it
          through the operator's speakers next to their microphone is a feedback loop.
        */}
        <video
          ref={(element) => {
            videoRef.current = element;
            registerElement(sourceId, element);
          }}
          autoPlay
          muted
          playsInline
          aria-label={`${feed.source.displayName} monitor`}
          className="size-full object-contain"
        />

        {!feed.stream ? (
          <p className="absolute inset-0 flex items-center justify-center text-xs text-slate-500">
            {STATE_LABELS[feed.state] ?? "No picture"}
          </p>
        ) : null}

        {live ? (
          <span className="absolute left-2 top-2">
            <Badge tone="live" pulse>
              On air
            </Badge>
          </span>
        ) : null}
      </div>

      <div className="flex items-center justify-between gap-2">
        <div className="min-w-0">
          <p className="truncate text-sm font-medium text-slate-200">{feed.source.displayName}</p>
          <p className="truncate text-xs text-slate-500">{STATE_LABELS[feed.state] ?? feed.state}</p>
        </div>

        {/*
          No button on the source that is already on air. The tally badge over the picture says so,
          and offering to cut to what is already live is an affordance with nothing behind it.
        */}
        {live ? null : (
          <Button
            variant="secondary"
            disabled={disabled || busy || feed.source.status !== "Connected"}
            onClick={() => onCut(sourceId)}
          >
            {busy ? "Cutting…" : "Cut"}
          </Button>
        )}
      </div>
    </div>
  );
}
