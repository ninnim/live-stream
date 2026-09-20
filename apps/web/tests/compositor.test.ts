import { describe, expect, it, vi } from "vitest";
import {
  type CompositorCanvas,
  type CompositorOptions,
  type Watermark,
  ProgramCompositor,
} from "@/lib/media/compositor";

/**
 * jsdom implements neither a 2D context nor `captureStream`. This records the draw calls, which is
 * what the compositor's job actually reduces to: which sources end up where in the frame.
 */
function fakeCanvas() {
  const drawn: { source: unknown; x: number; y: number; width: number; height: number }[] = [];
  const fills: { x: number; y: number; width: number; height: number }[] = [];
  const texts: string[] = [];

  const videoTrack = { kind: "video", id: "composed", stop: vi.fn() } as unknown as MediaStreamTrack;
  const captureStream = vi.fn(
    () => ({ getVideoTracks: () => [videoTrack], getTracks: () => [videoTrack] }) as unknown as MediaStream,
  );

  const context = {
    fillStyle: "",
    font: "",
    // Starts at 1, like a real context: the watermark path sets and restores it, and a leak here
    // would fade the entire next frame.
    globalAlpha: 1,
    textBaseline: "alphabetic" as CanvasTextBaseline,
    fillRect: (x: number, y: number, width: number, height: number) => fills.push({ x, y, width, height }),
    drawImage: (source: unknown, x: number, y: number, width: number, height: number) =>
      drawn.push({ source, x, y, width, height }),
    measureText: (text: string) => ({ width: text.length * 10 }) as TextMetrics,
    fillText: (text: string) => texts.push(text),
  } as unknown as CanvasRenderingContext2D;

  const canvas: CompositorCanvas = {
    width: 0,
    height: 0,
    getContext: () => context,
    captureStream,
  };

  return { canvas, drawn, fills, texts, captureStream, videoTrack };
}

/** A video element that reports a size, which is all the compositor reads from one. */
function fakeVideo(width: number, height: number): HTMLVideoElement {
  return { videoWidth: width, videoHeight: height } as unknown as HTMLVideoElement;
}

function build(overrides: CompositorOptions = {}) {
  const harness = fakeCanvas();
  const compositor = new ProgramCompositor({
    createCanvas: () => harness.canvas,
    scheduleFrame: () => 1,
    cancelFrame: () => undefined,
    ...overrides,
  });

  return { compositor, ...harness };
}

describe("ProgramCompositor", () => {
  it("composes at 720p30 by default", () => {
    const { compositor } = build();

    expect(compositor.width).toBe(1280);
    expect(compositor.height).toBe(720);
  });

  it("hands out one stable video track", () => {
    // The whole point: the studio publishes this track, and every cut afterwards changes the
    // picture inside it rather than replacing what is being sent.
    const { compositor, captureStream } = build();

    const first = compositor.videoTrack;

    expect(first).not.toBeNull();
    expect(compositor.videoTrack).toBe(first);
    expect(captureStream).toHaveBeenCalledTimes(1);
  });

  it("draws a solo source across the whole frame", () => {
    const { compositor, drawn } = build();
    const element = fakeVideo(1280, 720);

    compositor.setLayers([{ id: "a", label: "Studio", element }]);
    compositor.drawFrame();

    expect(drawn).toEqual([{ source: element, x: 0, y: 0, width: 1280, height: 720 }]);
  });

  it("paints the frame black before drawing, so letterboxing sits on black", () => {
    const { compositor, fills } = build();

    compositor.setLayers([{ id: "a", label: "Studio", element: fakeVideo(1080, 1920) }]);
    compositor.drawFrame();

    expect(fills[0]).toEqual({ x: 0, y: 0, width: 1280, height: 720 });
  });

  it("draws only as many sources as the layout shows", () => {
    const { compositor, drawn } = build();

    compositor.setLayers([
      { id: "a", label: "Studio", element: fakeVideo(1280, 720) },
      { id: "b", label: "Phone", element: fakeVideo(1280, 720) },
      { id: "c", label: "Spare", element: fakeVideo(1280, 720) },
    ]);

    compositor.setLayout("solo");
    compositor.drawFrame();
    expect(drawn).toHaveLength(1);

    drawn.length = 0;
    compositor.setLayout("side-by-side");
    compositor.drawFrame();
    expect(drawn).toHaveLength(2);
  });

  it("skips a source that has not produced a frame yet rather than drawing nothing at all", () => {
    // A video element reports zero dimensions until its first frame, and drawing one throws in some
    // browsers. The other source must still appear.
    const { compositor, drawn } = build();
    const ready = fakeVideo(1280, 720);

    compositor.setLayout("side-by-side");
    compositor.setLayers([
      { id: "a", label: "Studio", element: ready },
      { id: "b", label: "Phone", element: fakeVideo(0, 0) },
    ]);
    compositor.drawFrame();

    expect(drawn).toHaveLength(1);
    expect(drawn[0]?.source).toBe(ready);
  });

  it("names sources only when more than one is on air", () => {
    // A permanent caption over a solo shot ends up in the recording.
    const { compositor, texts } = build();

    compositor.setLayers([{ id: "a", label: "Studio", element: fakeVideo(1280, 720) }]);
    compositor.drawFrame();
    expect(texts).toEqual([]);

    compositor.setLayout("side-by-side");
    compositor.setLayers([
      { id: "a", label: "Studio", element: fakeVideo(1280, 720) },
      { id: "b", label: "Stage left", element: fakeVideo(1280, 720) },
    ]);
    compositor.drawFrame();

    expect(texts).toEqual(["Studio", "Stage left"]);
  });

  it("can be told not to name sources", () => {
    const { compositor, texts } = build();

    compositor.setShowLabels(false);
    compositor.setLayout("side-by-side");
    compositor.setLayers([
      { id: "a", label: "Studio", element: fakeVideo(1280, 720) },
      { id: "b", label: "Phone", element: fakeVideo(1280, 720) },
    ]);
    compositor.drawFrame();

    expect(texts).toEqual([]);
  });

  it("draws an upright phone pillarboxed rather than stretched", () => {
    const { compositor, drawn } = build();

    compositor.setLayers([{ id: "a", label: "Phone", element: fakeVideo(1080, 1920) }]);
    compositor.drawFrame();

    const placement = drawn[0];
    expect(placement?.height).toBe(720);
    expect(placement?.width).toBeLessThan(1280);
    expect(placement?.x).toBeGreaterThan(0);
  });

  it("runs a frame loop only while started", () => {
    const scheduleFrame = vi.fn(() => 1);
    const { compositor } = build({ scheduleFrame });

    compositor.setLayers([{ id: "a", label: "Studio", element: fakeVideo(1280, 720) }]);
    expect(scheduleFrame).not.toHaveBeenCalled();

    compositor.start();
    expect(scheduleFrame).toHaveBeenCalledTimes(1);

    // Starting twice must not run two loops, which would double the frame rate and the CPU cost.
    compositor.start();
    expect(scheduleFrame).toHaveBeenCalledTimes(1);
  });

  it("releases the composed track on dispose", () => {
    const { compositor, videoTrack } = build();

    void compositor.videoTrack;
    compositor.dispose();

    expect(videoTrack.stop).toHaveBeenCalled();
  });
});

describe("ProgramCompositor overlays", () => {
  it("draws a caption over the picture", () => {
    const { compositor, texts } = build();

    compositor.setLayers([{ id: "a", label: "Studio", element: fakeVideo(1280, 720) }]);
    compositor.setOverlay({
      lowerThird: { title: "Ada Lovelace", subtitle: "Analytical Engines", accentColor: "#0EA5E9" },
    });
    compositor.drawFrame();

    expect(texts).toEqual(["Ada Lovelace", "Analytical Engines"]);
  });

  it("draws a caption with no subtitle when none is given", () => {
    const { compositor, texts } = build();

    compositor.setLayers([{ id: "a", label: "Studio", element: fakeVideo(1280, 720) }]);
    compositor.setOverlay({ lowerThird: { title: "Ada Lovelace", accentColor: "#0EA5E9" } });
    compositor.drawFrame();

    expect(texts).toEqual(["Ada Lovelace"]);
  });

  it("draws nothing for an empty caption", () => {
    const { compositor, texts } = build();

    compositor.setLayers([{ id: "a", label: "Studio", element: fakeVideo(1280, 720) }]);
    compositor.setOverlay({ lowerThird: { title: "", accentColor: "#0EA5E9" } });
    compositor.drawFrame();

    expect(texts).toEqual([]);
  });

  it("draws the watermark over the picture", () => {
    const { compositor, drawn } = build();
    const video = fakeVideo(1280, 720);
    const logo = { width: 200, height: 200 } as unknown as Watermark["image"];

    compositor.setLayers([{ id: "a", label: "Studio", element: video }]);
    compositor.setOverlay({ watermark: { image: logo, corner: "TopRight", opacity: 0.8 } });
    compositor.drawFrame();

    // Order matters: the logo has to land after the picture, or the picture covers it.
    expect(drawn.map((call) => call.source)).toEqual([video, logo]);
  });

  it("restores the frame alpha after a watermark, including when drawing throws", () => {
    // A leaked alpha would fade the whole of the next frame, which is a far more visible fault
    // than a missing logo.
    const harness = fakeCanvas();
    const compositor = new ProgramCompositor({
      createCanvas: () => harness.canvas,
      scheduleFrame: () => 1,
      cancelFrame: () => undefined,
    });

    const context = harness.canvas.getContext("2d")!;
    let calls = 0;
    context.drawImage = (() => {
      calls += 1;
      if (calls === 2) throw new Error("broken image");
    }) as typeof context.drawImage;

    compositor.setLayers([{ id: "a", label: "Studio", element: fakeVideo(1280, 720) }]);
    compositor.setOverlay({
      watermark: { image: { width: 100, height: 100 } as unknown as Watermark["image"], corner: "TopLeft", opacity: 0.5 },
    });

    expect(() => compositor.drawFrame()).toThrow();
    expect(context.globalAlpha).toBe(1);
  });

  it("skips a watermark whose image has not decoded", () => {
    const { compositor, drawn } = build();
    const video = fakeVideo(1280, 720);

    compositor.setLayers([{ id: "a", label: "Studio", element: video }]);
    compositor.setOverlay({
      watermark: { image: { width: 0, height: 0 } as unknown as Watermark["image"], corner: "TopRight", opacity: 1 },
    });
    compositor.drawFrame();

    expect(drawn).toHaveLength(1);
  });
});
