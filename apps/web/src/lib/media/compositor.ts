/**
 * The program compositor.
 *
 * Draws the sources that are on air into a canvas, and publishes that canvas as the outgoing video
 * track. This is what makes program switching seamless: cutting between sources changes what is
 * drawn, not which track is being sent, so the encoder never restarts, the resolution never
 * changes, and viewers see a cut rather than a stall.
 *
 * It is also why the media plane needed no changes for Phase 5. The studio still publishes one WHIP
 * stream into the session path exactly as it did in Phase 1; what varies is the picture
 * (docs/decisions/0012-program-switching-and-composition.md).
 */

import {
  type LayoutId,
  type LogoCorner,
  type Rect,
  capacityOf,
  computeSlots,
  containWithin,
  lowerThirdRect,
  watermarkRect,
} from "@/lib/media/layout";

/** A caption over the picture: who is speaking, and what about. */
export interface LowerThird {
  title: string;
  subtitle?: string | null;
  /** Hex, already validated by the server. Drives the accent bar. */
  accentColor: string;
}

/** A logo drawn over every frame. The image is loaded by the caller and may not be ready yet. */
export interface Watermark {
  image: CanvasImageSource & { width?: number; height?: number };
  corner: LogoCorner;
  /** 0 to 1. */
  opacity: number;
}

export interface CompositorOverlay {
  lowerThird?: LowerThird | null;
  watermark?: Watermark | null;
}

/** A source the compositor can draw: a video element fed by a camera, a screen, or a remote device. */
export interface CompositorLayer {
  id: string;
  label: string;
  element: HTMLVideoElement;
}

/** The subset of a canvas this class uses, so tests can supply one without a DOM implementation. */
export interface CompositorCanvas {
  width: number;
  height: number;
  getContext(contextId: "2d"): CanvasRenderingContext2D | null;
  captureStream(frameRate?: number): MediaStream;
}

export interface CompositorOptions {
  width?: number;
  height?: number;
  frameRate?: number;
  /** Injectable so the compositor can be exercised without a real canvas. */
  createCanvas?: () => CompositorCanvas;
  /** Injectable frame scheduler. Returns a handle the matching canceller understands. */
  scheduleFrame?: (callback: () => void) => number;
  cancelFrame?: (handle: number) => void;
}

/**
 * 720p30 by default, matching the studio's default capture rung. Composing above the resolution
 * sources actually arrive at buys nothing but encoder load.
 */
const DEFAULT_WIDTH = 1280;
const DEFAULT_HEIGHT = 720;
const DEFAULT_FRAME_RATE = 30;

/** Filled behind every frame, so a source that is letterboxed sits on black rather than on garbage. */
const BACKGROUND = "#000000";

const LABEL_FONT = "600 20px system-ui, -apple-system, Segoe UI, sans-serif";
const LABEL_PADDING = 10;
const LABEL_HEIGHT = 34;
const LABEL_MARGIN = 16;

const FONT_STACK = "system-ui, -apple-system, Segoe UI, sans-serif";
/** The coloured spine down the left of a lower third, as a fraction of its height. */
const ACCENT_BAR_RATIO = 0.09;

export class ProgramCompositor {
  private readonly canvas: CompositorCanvas;
  private readonly context: CanvasRenderingContext2D | null;
  private readonly frameRate: number;
  private readonly scheduleFrame: (callback: () => void) => number;
  private readonly cancelFrame: (handle: number) => void;

  private layers: CompositorLayer[] = [];
  private layout: LayoutId = "solo";
  private stream: MediaStream | null = null;
  private frameHandle: number | null = null;
  private running = false;
  /** Labels are drawn only when more than one source is on air; alone, the frame speaks for itself. */
  private showLabels = true;
  private overlay: CompositorOverlay = {};

  constructor(options: CompositorOptions = {}) {
    this.frameRate = options.frameRate ?? DEFAULT_FRAME_RATE;

    this.canvas =
      options.createCanvas?.() ??
      (document.createElement("canvas") as unknown as CompositorCanvas);
    this.canvas.width = options.width ?? DEFAULT_WIDTH;
    this.canvas.height = options.height ?? DEFAULT_HEIGHT;

    this.context = this.canvas.getContext("2d");

    this.scheduleFrame =
      options.scheduleFrame ?? ((callback) => window.requestAnimationFrame(() => callback()));
    this.cancelFrame = options.cancelFrame ?? ((handle) => window.cancelAnimationFrame(handle));
  }

  get width(): number {
    return this.canvas.width;
  }

  get height(): number {
    return this.canvas.height;
  }

  /**
   * The composed video track.
   *
   * Created once and kept for the compositor's whole life. That stability is the point: the studio
   * publishes this track, and every subsequent cut, layout change and source arrival happens inside
   * it rather than by replacing it.
   */
  get videoTrack(): MediaStreamTrack | null {
    if (!this.stream) {
      this.stream = this.canvas.captureStream(this.frameRate);
    }

    return this.stream.getVideoTracks()[0] ?? null;
  }

  /** The sources to draw, in order. The first is the one on air. */
  setLayers(layers: CompositorLayer[]): void {
    this.layers = layers;
  }

  setLayout(layout: LayoutId): void {
    this.layout = layout;
  }

  setShowLabels(show: boolean): void {
    this.showLabels = show;
  }

  /** The caption and watermark drawn over the picture. Absent members mean "draw nothing". */
  setOverlay(overlay: CompositorOverlay): void {
    this.overlay = overlay;
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.loop();
  }

  stop(): void {
    this.running = false;

    if (this.frameHandle !== null) {
      this.cancelFrame(this.frameHandle);
      this.frameHandle = null;
    }
  }

  /** Releases the composed track. The compositor is not reusable afterwards. */
  dispose(): void {
    this.stop();
    this.stream?.getTracks().forEach((track) => track.stop());
    this.stream = null;
  }

  private loop(): void {
    if (!this.running) return;

    this.drawFrame();
    this.frameHandle = this.scheduleFrame(() => {
      this.frameHandle = null;
      this.loop();
    });
  }

  /** Composes one frame. Exposed for tests, which drive frames rather than waiting for them. */
  drawFrame(): void {
    const context = this.context;
    if (!context) return;

    const frame: Rect = { x: 0, y: 0, width: this.canvas.width, height: this.canvas.height };

    context.fillStyle = BACKGROUND;
    context.fillRect(frame.x, frame.y, frame.width, frame.height);

    const visible = this.layers.slice(0, capacityOf(this.layout));
    const slots = computeSlots(this.layout, visible.length, frame);

    visible.forEach((layer, index) => {
      const slot = slots[index];
      if (!slot) return;

      const { videoWidth, videoHeight } = layer.element;
      const placement = containWithin(videoWidth, videoHeight, slot);

      // A source that has not produced a frame yet reports zero dimensions, and drawing it throws
      // in some browsers. Skipping leaves black in its slot, which is the honest picture.
      if (placement.width <= 0 || placement.height <= 0) return;

      context.drawImage(layer.element, placement.x, placement.y, placement.width, placement.height);

      if (this.showLabels && visible.length > 1) {
        this.drawLabel(context, layer.label, placement);
      }
    });

    // Overlays go on last, so they sit above the picture whatever the layout is doing beneath.
    this.drawWatermark(context, frame);
    this.drawLowerThird(context, frame);
  }

  /**
   * Draws the watermark.
   *
   * Skipped silently while the image is still loading. An `<img>` that has not decoded reports zero
   * dimensions and throws when drawn, and a broadcast must not stop for a logo.
   */
  private drawWatermark(context: CanvasRenderingContext2D, frame: Rect): void {
    const watermark = this.overlay.watermark;
    if (!watermark) return;

    const { width = 0, height = 0 } = watermark.image;
    const rect = watermarkRect(width, height, watermark.corner, frame);
    if (rect.width <= 0 || rect.height <= 0) return;

    const previousAlpha = context.globalAlpha;
    context.globalAlpha = Math.min(Math.max(watermark.opacity, 0), 1);

    try {
      context.drawImage(watermark.image, rect.x, rect.y, rect.width, rect.height);
    } finally {
      // Restored even if drawing throws: leaking a low alpha would fade the entire next frame.
      context.globalAlpha = previousAlpha;
    }
  }

  /**
   * Draws the lower third: an accent spine, a dark plate, a name and an optional subtitle.
   *
   * Sized from the frame rather than in fixed pixels, so a caption that reads well at 720p still
   * reads well at 1080p.
   */
  private drawLowerThird(context: CanvasRenderingContext2D, frame: Rect): void {
    const lowerThird = this.overlay.lowerThird;
    if (!lowerThird?.title) return;

    const rect = lowerThirdRect(frame);
    if (rect.width <= 0 || rect.height <= 0) return;

    const barWidth = Math.max(3, Math.round(rect.height * ACCENT_BAR_RATIO));

    context.fillStyle = "rgba(2, 6, 23, 0.78)";
    context.fillRect(rect.x, rect.y, rect.width, rect.height);

    context.fillStyle = lowerThird.accentColor;
    context.fillRect(rect.x, rect.y, barWidth, rect.height);

    const padding = Math.round(rect.height * 0.18);
    const textX = rect.x + barWidth + padding;
    const maxTextWidth = rect.width - barWidth - padding * 2;
    const hasSubtitle = Boolean(lowerThird.subtitle);

    const titleSize = Math.round(rect.height * (hasSubtitle ? 0.34 : 0.4));
    context.font = `700 ${titleSize}px ${FONT_STACK}`;
    context.fillStyle = "#F8FAFC";
    context.textBaseline = "middle";

    const titleY = hasSubtitle ? rect.y + rect.height * 0.36 : rect.y + rect.height / 2;
    context.fillText(lowerThird.title, textX, titleY, maxTextWidth);

    if (hasSubtitle) {
      const subtitleSize = Math.round(rect.height * 0.24);
      context.font = `400 ${subtitleSize}px ${FONT_STACK}`;
      context.fillStyle = "#CBD5F5";
      context.fillText(lowerThird.subtitle!, textX, rect.y + rect.height * 0.7, maxTextWidth);
    }
  }

  /**
   * Names a source in its own slot.
   *
   * Only when more than one is on air. In full screen the operator knows what they cut to, and a
   * permanent caption over a solo shot is the kind of thing that ends up in a recording.
   */
  private drawLabel(context: CanvasRenderingContext2D, label: string, placement: Rect): void {
    if (!label) return;

    context.font = LABEL_FONT;
    const textWidth = context.measureText(label).width;
    const boxWidth = Math.min(textWidth + LABEL_PADDING * 2, placement.width - LABEL_MARGIN * 2);
    if (boxWidth <= 0) return;

    const x = placement.x + LABEL_MARGIN;
    const y = placement.y + placement.height - LABEL_MARGIN - LABEL_HEIGHT;

    context.fillStyle = "rgba(2, 6, 23, 0.72)";
    context.fillRect(x, y, boxWidth, LABEL_HEIGHT);

    context.fillStyle = "#f8fafc";
    context.textBaseline = "middle";
    context.fillText(label, x + LABEL_PADDING, y + LABEL_HEIGHT / 2, boxWidth - LABEL_PADDING * 2);
  }
}
