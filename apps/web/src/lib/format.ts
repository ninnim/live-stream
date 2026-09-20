import type {
  DestinationProvider,
  DestinationStatus,
  LiveSessionStatus,
  StreamHealthStatus,
} from "@/lib/types";

/** Formats a duration as HH:MM:SS, matching the studio's session timer. */
export function formatDuration(totalSeconds: number | null | undefined): string {
  if (totalSeconds === null || totalSeconds === undefined || Number.isNaN(totalSeconds)) {
    return "00:00:00";
  }

  const seconds = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const remainder = seconds % 60;

  return [hours, minutes, remainder].map((part) => String(part).padStart(2, "0")).join(":");
}

export function formatBitrate(kbps: number | null | undefined): string {
  if (kbps === null || kbps === undefined) return "—";
  if (kbps >= 1000) return `${(kbps / 1000).toFixed(1)} Mbps`;
  return `${Math.round(kbps)} kbps`;
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) return "—";

  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unitIndex = 0;

  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }

  return `${value.toFixed(unitIndex === 0 ? 0 : 1)} ${units[unitIndex]}`;
}

export function formatViewers(count: number): string {
  return new Intl.NumberFormat("en-US").format(count);
}

/** Plain-language label for a session state. Infrastructure vocabulary stays out of the UI. */
export function statusLabel(status: LiveSessionStatus): string {
  const labels: Record<LiveSessionStatus, string> = {
    DRAFT: "Draft",
    PREPARING: "Preparing",
    READY: "Ready to go live",
    STARTING: "Starting",
    LIVE: "Live",
    DEGRADED: "Live — reduced quality",
    RECONNECTING: "Reconnecting",
    STOPPING: "Stopping",
    ENDED: "Ended",
    FAILED: "Failed",
  };

  return labels[status];
}

export function healthLabel(status: StreamHealthStatus): string {
  const labels: Record<StreamHealthStatus, string> = {
    UNKNOWN: "Unknown",
    GOOD: "Good",
    FAIR: "Fair",
    POOR: "Poor",
  };

  return labels[status];
}

/** Semantic tone used for status pills and health readouts. */
export type Tone = "neutral" | "positive" | "caution" | "critical" | "live";

export function statusTone(status: LiveSessionStatus): Tone {
  switch (status) {
    case "LIVE":
      return "live";
    case "DEGRADED":
    case "RECONNECTING":
    case "STARTING":
    case "STOPPING":
    case "PREPARING":
      return "caution";
    case "READY":
      return "positive";
    case "FAILED":
      return "critical";
    default:
      return "neutral";
  }
}

export function healthTone(status: StreamHealthStatus): Tone {
  switch (status) {
    case "GOOD":
      return "positive";
    case "FAIR":
      return "caution";
    case "POOR":
      return "critical";
    default:
      return "neutral";
  }
}

/**
 * Plain-language label for a destination state.
 *
 * "Reconnecting" rather than "Retrying", and "Not connected" rather than "Idle": the person reading
 * this is a broadcaster watching their stream, not an operator reading a state machine.
 */
export function destinationStatusLabel(status: DestinationStatus): string {
  const labels: Record<DestinationStatus, string> = {
    Idle: "Not started",
    Preparing: "Preparing",
    Connecting: "Connecting",
    Live: "Live",
    Retrying: "Reconnecting",
    Stopping: "Stopping",
    Stopped: "Stopped",
    Error: "Failed",
    Disabled: "Off",
  };

  return labels[status];
}

export function destinationStatusTone(status: DestinationStatus): Tone {
  switch (status) {
    case "Live":
      return "live";
    case "Preparing":
    case "Connecting":
    case "Retrying":
    case "Stopping":
      return "caution";
    case "Error":
      return "critical";
    default:
      return "neutral";
  }
}

export function providerLabel(provider: DestinationProvider): string {
  const labels: Record<DestinationProvider, string> = {
    CustomRtmp: "Custom RTMP",
    YouTube: "YouTube",
    Facebook: "Facebook",
    TikTok: "TikTok",
    Twitch: "Twitch",
  };

  return labels[provider];
}

/** Seconds until an ISO timestamp, floored at zero. Used for the retry countdown. */
export function secondsUntil(iso: string | null | undefined, now: number = Date.now()): number {
  if (!iso) return 0;
  const target = Date.parse(iso);
  if (Number.isNaN(target)) return 0;
  return Math.max(0, Math.ceil((target - now) / 1000));
}

/** Seconds elapsed since an ISO timestamp, floored at zero. */
export function secondsSince(iso: string | null | undefined, now: number = Date.now()): number {
  if (!iso) return 0;
  const started = Date.parse(iso);
  if (Number.isNaN(started)) return 0;
  return Math.max(0, Math.floor((now - started) / 1000));
}
