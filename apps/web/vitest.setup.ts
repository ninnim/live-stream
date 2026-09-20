import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach, vi } from "vitest";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

// jsdom implements neither media playback nor WebRTC. Stub the surface the studio touches so
// component tests exercise real component logic instead of failing on missing browser APIs.
Object.defineProperty(window.HTMLMediaElement.prototype, "play", {
  configurable: true,
  value: vi.fn().mockResolvedValue(undefined),
});

Object.defineProperty(window.HTMLMediaElement.prototype, "pause", {
  configurable: true,
  value: vi.fn(),
});

Object.defineProperty(window.HTMLMediaElement.prototype, "srcObject", {
  configurable: true,
  writable: true,
  value: null,
});

/**
 * jsdom has no `MediaStream`. The studio keeps one stream for the life of the session and swaps
 * tracks inside it — that is what stops the preview flashing black on a device change — so the
 * container has to actually behave like a set of tracks rather than be a stub that records calls.
 */
if (typeof globalThis.MediaStream === "undefined") {
  class MediaStreamStub {
    private tracks: MediaStreamTrack[];

    constructor(tracks: MediaStreamTrack[] = []) {
      this.tracks = [...tracks];
    }

    getTracks(): MediaStreamTrack[] {
      return [...this.tracks];
    }

    getVideoTracks(): MediaStreamTrack[] {
      return this.tracks.filter((track) => track.kind === "video");
    }

    getAudioTracks(): MediaStreamTrack[] {
      return this.tracks.filter((track) => track.kind === "audio");
    }

    addTrack(track: MediaStreamTrack): void {
      if (!this.tracks.includes(track)) this.tracks.push(track);
    }

    removeTrack(track: MediaStreamTrack): void {
      this.tracks = this.tracks.filter((candidate) => candidate !== track);
    }
  }

  globalThis.MediaStream = MediaStreamStub as unknown as typeof MediaStream;
}
