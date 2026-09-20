import { describe, expect, it } from "vitest";
import {
  formatBitrate,
  formatBytes,
  formatDuration,
  healthTone,
  secondsSince,
  statusLabel,
  statusTone,
} from "@/lib/format";
import type { LiveSessionStatus } from "@/lib/types";

describe("formatDuration", () => {
  it("formats the session timer as HH:MM:SS", () => {
    expect(formatDuration(0)).toBe("00:00:00");
    expect(formatDuration(754)).toBe("00:12:34");
    expect(formatDuration(3661)).toBe("01:01:01");
    expect(formatDuration(86_399)).toBe("23:59:59");
  });

  it("handles long broadcasts past 24 hours", () => {
    expect(formatDuration(90_061)).toBe("25:01:01");
  });

  it("falls back to zero for missing or invalid values", () => {
    expect(formatDuration(null)).toBe("00:00:00");
    expect(formatDuration(undefined)).toBe("00:00:00");
    expect(formatDuration(Number.NaN)).toBe("00:00:00");
    expect(formatDuration(-5)).toBe("00:00:00");
  });
});

describe("formatBitrate", () => {
  it("switches to Mbps above 1000 kbps", () => {
    expect(formatBitrate(850)).toBe("850 kbps");
    expect(formatBitrate(2400)).toBe("2.4 Mbps");
  });

  it("renders an em dash when unknown", () => {
    expect(formatBitrate(null)).toBe("—");
  });
});

describe("formatBytes", () => {
  it("scales to a readable unit", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(2048)).toBe("2.0 KB");
    expect(formatBytes(8_400_000)).toBe("8.0 MB");
  });

  it("renders an em dash when unknown", () => {
    expect(formatBytes(null)).toBe("—");
  });
});

describe("status presentation", () => {
  it("gives every status a human label", () => {
    const statuses: LiveSessionStatus[] = [
      "DRAFT",
      "PREPARING",
      "READY",
      "STARTING",
      "LIVE",
      "DEGRADED",
      "RECONNECTING",
      "STOPPING",
      "ENDED",
      "FAILED",
    ];

    for (const status of statuses) {
      expect(statusLabel(status)).toBeTruthy();
      // Infrastructure vocabulary must not leak into the UI.
      expect(statusLabel(status)).not.toBe(status);
    }
  });

  it("marks live as live and failures as critical", () => {
    expect(statusTone("LIVE")).toBe("live");
    expect(statusTone("FAILED")).toBe("critical");
    expect(statusTone("RECONNECTING")).toBe("caution");
    expect(statusTone("READY")).toBe("positive");
  });

  it("maps health to a matching tone", () => {
    expect(healthTone("GOOD")).toBe("positive");
    expect(healthTone("FAIR")).toBe("caution");
    expect(healthTone("POOR")).toBe("critical");
    expect(healthTone("UNKNOWN")).toBe("neutral");
  });
});

describe("secondsSince", () => {
  it("measures elapsed time from an ISO timestamp", () => {
    const now = Date.parse("2026-01-01T12:10:00Z");
    expect(secondsSince("2026-01-01T12:00:00Z", now)).toBe(600);
  });

  it("never returns a negative duration for a future timestamp", () => {
    const now = Date.parse("2026-01-01T12:00:00Z");
    expect(secondsSince("2026-01-01T12:10:00Z", now)).toBe(0);
  });

  it("returns zero for missing or unparseable input", () => {
    expect(secondsSince(null)).toBe(0);
    expect(secondsSince("not-a-date")).toBe(0);
  });
});
