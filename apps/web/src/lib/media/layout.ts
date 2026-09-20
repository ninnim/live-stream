/**
 * Program layout geometry.
 *
 * Deliberately pure: no canvas, no DOM, no video elements. Where each source belongs in the frame
 * is the part worth testing exhaustively, and the drawing that follows is a thin shell over it.
 */

export type LayoutId = "solo" | "side-by-side" | "picture-in-picture";

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface LayoutDescriptor {
  id: LayoutId;
  label: string;
  /** How many sources this layout shows. Anything beyond is off air but still previewable. */
  capacity: number;
}

export const LAYOUTS: LayoutDescriptor[] = [
  { id: "solo", label: "Full screen", capacity: 1 },
  { id: "side-by-side", label: "Side by side", capacity: 2 },
  { id: "picture-in-picture", label: "Picture in picture", capacity: 2 },
];

/** Gap between slots, as a fraction of frame width. Small enough to read as a seam, not a border. */
const GUTTER_RATIO = 0.008;

/** The inset in picture-in-picture, as a fraction of frame width. */
const INSET_RATIO = 0.28;
const INSET_MARGIN_RATIO = 0.025;

/**
 * Where each visible source is drawn, in draw order.
 *
 * The first entry is always the source on air: in `solo` it is the whole frame, in
 * `picture-in-picture` it is the full-frame background, and in `side-by-side` it takes the left.
 * That ordering is what lets the operator cut without also having to think about position.
 */
export function computeSlots(layout: LayoutId, sourceCount: number, frame: Rect): Rect[] {
  if (sourceCount <= 0) return [];

  const visible = Math.min(sourceCount, capacityOf(layout));

  if (layout === "solo" || visible === 1) {
    return [{ ...frame }];
  }

  if (layout === "side-by-side") {
    const gutter = Math.round(frame.width * GUTTER_RATIO);
    const columnWidth = Math.round((frame.width - gutter) / 2);

    return [
      { x: frame.x, y: frame.y, width: columnWidth, height: frame.height },
      {
        x: frame.x + columnWidth + gutter,
        // Rounding the first column down can leave a pixel over; the second column absorbs it so
        // the pair always spans the full frame rather than leaving a seam at the right edge.
        y: frame.y,
        width: frame.width - columnWidth - gutter,
        height: frame.height,
      },
    ];
  }

  // Picture-in-picture: the second source sits over the bottom-right of the first, which is where
  // broadcast convention puts it and where it is least likely to cover a face.
  const insetWidth = Math.round(frame.width * INSET_RATIO);
  const insetHeight = Math.round((insetWidth * frame.height) / frame.width);
  const margin = Math.round(frame.width * INSET_MARGIN_RATIO);

  return [
    { ...frame },
    {
      x: frame.x + frame.width - insetWidth - margin,
      y: frame.y + frame.height - insetHeight - margin,
      width: insetWidth,
      height: insetHeight,
    },
  ];
}

export function capacityOf(layout: LayoutId): number {
  return LAYOUTS.find((candidate) => candidate.id === layout)?.capacity ?? 1;
}

/** Where a watermark sits. Mirrors the server's `LogoPosition`. */
export type LogoCorner = "TopLeft" | "TopRight" | "BottomLeft" | "BottomRight";

/** Widest a watermark gets, as a fraction of frame width. Branding, not a billboard. */
const WATERMARK_WIDTH_RATIO = 0.12;
const WATERMARK_MARGIN_RATIO = 0.025;

/**
 * Places a watermark in a corner, scaled to a fixed fraction of the frame and never distorted.
 *
 * Scaling by width rather than to a box means a wide logo and a square one occupy the same visual
 * weight, which is what makes a watermark read as branding rather than as an object in the shot.
 */
export function watermarkRect(
  imageWidth: number,
  imageHeight: number,
  corner: LogoCorner,
  frame: Rect,
): Rect {
  if (imageWidth <= 0 || imageHeight <= 0) {
    return { x: frame.x, y: frame.y, width: 0, height: 0 };
  }

  const width = Math.round(frame.width * WATERMARK_WIDTH_RATIO);
  const height = Math.round((width * imageHeight) / imageWidth);
  const margin = Math.round(frame.width * WATERMARK_MARGIN_RATIO);

  const left = corner === "TopLeft" || corner === "BottomLeft";
  const top = corner === "TopLeft" || corner === "TopRight";

  return {
    x: left ? frame.x + margin : frame.x + frame.width - width - margin,
    y: top ? frame.y + margin : frame.y + frame.height - height - margin,
    width,
    height,
  };
}

// Proportions of the frame, so a lower third looks the same at every composition size.
const LOWER_THIRD_LEFT_RATIO = 0.06;
const LOWER_THIRD_BOTTOM_RATIO = 0.12;
const LOWER_THIRD_HEIGHT_RATIO = 0.14;
const LOWER_THIRD_MAX_WIDTH_RATIO = 0.55;

/**
 * The band a lower third occupies.
 *
 * Sized as a share of the frame rather than in pixels: a caption that is legible at 720p and
 * illegible at 1080p is worse than one that is merely small, and viewers watch at every size.
 */
export function lowerThirdRect(frame: Rect): Rect {
  const height = Math.round(frame.height * LOWER_THIRD_HEIGHT_RATIO);

  return {
    x: frame.x + Math.round(frame.width * LOWER_THIRD_LEFT_RATIO),
    y: frame.y + frame.height - Math.round(frame.height * LOWER_THIRD_BOTTOM_RATIO) - height,
    width: Math.round(frame.width * LOWER_THIRD_MAX_WIDTH_RATIO),
    height,
  };
}

export interface Placement {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Fits a source inside a slot without cropping it, centred, preserving aspect ratio.
 *
 * "Contain" rather than "cover" because a phone held upright is a first-class source here. Filling
 * a 16:9 slot with a 9:16 camera means showing a narrow vertical strip of the middle of it — which
 * routinely means showing a torso and no head. Bars at the sides are honest; a cropped face is not.
 *
 * Returns a zero-sized placement for a source that has not reported its dimensions yet, which is
 * the normal state for the first frames after a video element is attached.
 */
export function containWithin(sourceWidth: number, sourceHeight: number, slot: Rect): Placement {
  if (sourceWidth <= 0 || sourceHeight <= 0 || slot.width <= 0 || slot.height <= 0) {
    return { x: slot.x, y: slot.y, width: 0, height: 0 };
  }

  const scale = Math.min(slot.width / sourceWidth, slot.height / sourceHeight);
  const width = Math.round(sourceWidth * scale);
  const height = Math.round(sourceHeight * scale);

  return {
    x: Math.round(slot.x + (slot.width - width) / 2),
    y: Math.round(slot.y + (slot.height - height) / 2),
    width,
    height,
  };
}
