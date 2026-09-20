"use client";

import { Card, Metric } from "@/components/ui/primitives";
import {
  formatBitrate,
  formatDuration,
  formatViewers,
  healthLabel,
  healthTone,
} from "@/lib/format";
import type { PublishQuality, QualityVerdict } from "@/lib/media/quality";
import type { BroadcastConnectionState } from "@/lib/media/whip";
import type { LiveSessionHealth } from "@/lib/types";

interface HealthPanelProps {
  health: LiveSessionHealth | null;
  elapsedSeconds: number;
  connectionState: BroadcastConnectionState;
  realtimeConnected: boolean;
  cameraEnabled: boolean;
  microphoneEnabled: boolean;
  /** Measured in this browser. Distinct from `health`, which is what the server received. */
  quality: PublishQuality | null;
}

const VERDICT_LABELS: Record<QualityVerdict, string> = {
  good: "Smooth",
  fair: "Strained",
  poor: "Poor",
};

function verdictTone(verdict: QualityVerdict) {
  switch (verdict) {
    case "good":
      return "positive" as const;
    case "fair":
      return "caution" as const;
    default:
      return "critical" as const;
  }
}

const CONNECTION_LABELS: Record<BroadcastConnectionState, string> = {
  idle: "Not connected",
  connecting: "Connecting",
  connected: "Stable",
  reconnecting: "Reconnecting",
  failed: "Failed",
  closed: "Closed",
};

function connectionTone(state: BroadcastConnectionState) {
  switch (state) {
    case "connected":
      return "positive" as const;
    case "connecting":
    case "reconnecting":
      return "caution" as const;
    case "failed":
      return "critical" as const;
    default:
      return "neutral" as const;
  }
}

/**
 * The operational read-out during a broadcast: duration, audience, stream health, and the state of
 * each input. Everything shown here comes from the server except the local device toggles.
 */
export function HealthPanel({
  health,
  elapsedSeconds,
  connectionState,
  realtimeConnected,
  cameraEnabled,
  microphoneEnabled,
  quality,
}: HealthPanelProps) {
  return (
    <Card className="flex flex-col gap-5">
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <Metric label="Duration" value={formatDuration(elapsedSeconds)} />
        <Metric label="Viewers" value={formatViewers(health?.viewerCount ?? 0)} />
        <Metric
          label="Health"
          value={healthLabel(health?.status ?? "UNKNOWN")}
          tone={healthTone(health?.status ?? "UNKNOWN")}
        />
        <Metric label="Received" value={formatBitrate(health?.bitrateKbps)} />
      </div>

      {/*
        What this browser is actually managing to send, which the server cannot see. When it
        disagrees with "Received" above, the gap is the network between them — and that comparison
        is the fastest way to tell a local encoding problem from a connection problem.
      */}
      {quality ? (
        <div className="grid grid-cols-2 gap-4 border-t border-slate-800 pt-4 sm:grid-cols-4">
          <Metric
            label="Outgoing video"
            value={VERDICT_LABELS[quality.verdict]}
            tone={verdictTone(quality.verdict)}
          />
          <Metric label="Upload" value={formatBitrate(quality.bitrateKbps || null)} />
          <Metric label="Frame rate" value={quality.framesPerSecond > 0 ? `${quality.framesPerSecond} fps` : "—"} />
          <Metric
            label="Packet loss"
            value={`${quality.packetLossPercent.toFixed(1)}%`}
            tone={quality.packetLossPercent >= 2 ? "caution" : "positive"}
          />

          {/*
            Whether this machine's GPU is doing the encoding — the difference between holding
            1080p60 and watching the frame rate collapse as the fans spin up.

            Taken from `powerEfficientEncoder`, the standard boolean, rather than from the encoder's
            name. "Not reported" means this browser declined to answer, which is a different thing
            from "no GPU" and has to read that way: a machine with a perfectly good GPU used to be
            told its encoder was Unknown, because the name it reported was not one this code had
            been taught.
          */}
          {quality.encoder ? (
            <Metric
              label="Encoder"
              value={
                quality.encoder.hardware === true
                  ? "GPU"
                  : quality.encoder.hardware === false
                    ? "CPU"
                    : "Not reported"
              }
              // The browser's own name for it, always. It is the useful half when the answer is
              // "not reported", and the confirming half when it is not.
              hint={
                quality.encoder.hardware === null
                  ? `${quality.encoder.name} — this browser does not say whether a GPU is involved`
                  : quality.encoder.name
              }
              tone={quality.encoder.hardware === false ? "caution" : "neutral"}
            />
          ) : null}
        </div>
      ) : null}

      {quality?.advice ? (
        <p className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-200">
          <span className="font-medium">{quality.headline}</span> {quality.advice}
        </p>
      ) : null}

      <div className="grid grid-cols-2 gap-4 border-t border-slate-800 pt-4 sm:grid-cols-4">
        <Metric
          label="Camera"
          value={cameraEnabled ? "On" : "Off"}
          tone={cameraEnabled ? "positive" : "critical"}
        />
        <Metric
          label="Microphone"
          value={microphoneEnabled ? "On" : "Off"}
          tone={microphoneEnabled ? "positive" : "critical"}
        />
        <Metric
          label="Connection"
          value={CONNECTION_LABELS[connectionState]}
          tone={connectionTone(connectionState)}
        />
        <Metric
          label="Live updates"
          value={realtimeConnected ? "On" : "Polling"}
          tone={realtimeConnected ? "positive" : "caution"}
        />
      </div>

      {health && health.reconnectCount > 0 ? (
        <p className="text-xs text-slate-500">
          Recovered from {health.reconnectCount} connection {health.reconnectCount === 1 ? "drop" : "drops"}{" "}
          during this session.
        </p>
      ) : null}
    </Card>
  );
}
