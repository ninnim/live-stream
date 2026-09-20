import { describe, expect, it } from "vitest";
import {
  type Rect,
  capacityOf,
  computeSlots,
  containWithin,
  lowerThirdRect,
  watermarkRect,
} from "@/lib/media/layout";

const FRAME: Rect = { x: 0, y: 0, width: 1280, height: 720 };

/** Slots must not overlap, or a source is drawn over another rather than beside it. */
function overlaps(a: Rect, b: Rect): boolean {
  return (
    a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height
  );
}

describe("computeSlots", () => {
  it("gives the whole frame to a solo source", () => {
    expect(computeSlots("solo", 1, FRAME)).toEqual([FRAME]);
  });

  it("draws nothing when there is nothing on air", () => {
    expect(computeSlots("solo", 0, FRAME)).toEqual([]);
  });

  it("falls back to full screen when a multi-source layout has only one source", () => {
    // Half a frame of picture next to half a frame of black is not a layout, it is a fault.
    expect(computeSlots("side-by-side", 1, FRAME)).toEqual([FRAME]);
    expect(computeSlots("picture-in-picture", 1, FRAME)).toEqual([FRAME]);
  });

  it("splits side-by-side into two non-overlapping columns spanning the full frame", () => {
    const [left, right] = computeSlots("side-by-side", 2, FRAME);

    expect(left).toBeDefined();
    expect(right).toBeDefined();
    expect(overlaps(left!, right!)).toBe(false);
    expect(left!.height).toBe(FRAME.height);
    expect(right!.height).toBe(FRAME.height);

    // Rounding the first column must not leave a gap at the right edge.
    expect(right!.x + right!.width).toBe(FRAME.width);
  });

  it("keeps the picture-in-picture inset inside the frame", () => {
    const [background, inset] = computeSlots("picture-in-picture", 2, FRAME);

    expect(background).toEqual(FRAME);
    expect(inset!.x).toBeGreaterThan(FRAME.width / 2);
    expect(inset!.y).toBeGreaterThan(FRAME.height / 2);
    expect(inset!.x + inset!.width).toBeLessThanOrEqual(FRAME.width);
    expect(inset!.y + inset!.height).toBeLessThanOrEqual(FRAME.height);
  });

  it("gives the inset the frame's aspect ratio, so a 16:9 source is not distorted", () => {
    const [, inset] = computeSlots("picture-in-picture", 2, FRAME);

    expect(inset!.width / inset!.height).toBeCloseTo(FRAME.width / FRAME.height, 1);
  });

  it("never returns more slots than the layout shows", () => {
    expect(computeSlots("solo", 5, FRAME)).toHaveLength(1);
    expect(computeSlots("side-by-side", 5, FRAME)).toHaveLength(2);
    expect(computeSlots("picture-in-picture", 5, FRAME)).toHaveLength(2);
  });

  it("puts the program in slot zero for every layout", () => {
    // The layouts' shared contract: cutting decides what is on air, never where it sits.
    for (const layout of ["solo", "side-by-side", "picture-in-picture"] as const) {
      const [first] = computeSlots(layout, 2, FRAME);
      expect(first!.x).toBe(FRAME.x);
      expect(first!.y).toBe(FRAME.y);
    }
  });
});

describe("capacityOf", () => {
  it("reports how many sources each layout shows", () => {
    expect(capacityOf("solo")).toBe(1);
    expect(capacityOf("side-by-side")).toBe(2);
    expect(capacityOf("picture-in-picture")).toBe(2);
  });
});

describe("containWithin", () => {
  it("fills a matching slot exactly", () => {
    expect(containWithin(1920, 1080, FRAME)).toEqual({ x: 0, y: 0, width: 1280, height: 720 });
  });

  it("pillarboxes an upright phone rather than cropping it", () => {
    // A 9:16 camera stretched to fill 16:9 shows a narrow strip of the middle — which routinely
    // means a torso and no head. Bars are honest; a cropped face is not.
    const placement = containWithin(1080, 1920, FRAME);

    expect(placement.height).toBe(FRAME.height);
    expect(placement.width).toBeLessThan(FRAME.width);
    expect(placement.x).toBeGreaterThan(0);
    // Centred, to within the pixel that integer rounding cannot split evenly.
    expect(Math.abs(placement.x * 2 + placement.width - FRAME.width)).toBeLessThanOrEqual(1);
  });

  it("letterboxes a wider-than-frame source", () => {
    const placement = containWithin(2000, 500, FRAME);

    expect(placement.width).toBe(FRAME.width);
    expect(placement.height).toBeLessThan(FRAME.height);
    expect(placement.y).toBeGreaterThan(0);
  });

  it("preserves aspect ratio in a narrow side-by-side column", () => {
    const [, right] = computeSlots("side-by-side", 2, FRAME);
    const placement = containWithin(1280, 720, right!);

    expect(placement.width / placement.height).toBeCloseTo(16 / 9, 1);
    expect(placement.width).toBeLessThanOrEqual(right!.width);
    expect(placement.height).toBeLessThanOrEqual(right!.height);
  });

  it("reports nothing to draw for a source that has not produced a frame yet", () => {
    // A video element reports zero dimensions until its first frame, and drawing it throws in
    // some browsers. This is the normal state for the second or so after a source attaches.
    expect(containWithin(0, 0, FRAME)).toMatchObject({ width: 0, height: 0 });
    expect(containWithin(1280, 720, { x: 0, y: 0, width: 0, height: 0 })).toMatchObject({
      width: 0,
      height: 0,
    });
  });
});

describe("watermarkRect", () => {
  it("scales by frame width so every logo carries the same visual weight", () => {
    // A wide logo and a square one should read as equally prominent branding; scaling to a box
    // would make a wide one dominate.
    const wide = watermarkRect(400, 100, "TopRight", FRAME);
    const square = watermarkRect(200, 200, "TopRight", FRAME);

    expect(wide.width).toBe(square.width);
    expect(wide.height).toBeLessThan(square.height);
  });

  it("never distorts the logo", () => {
    const rect = watermarkRect(400, 100, "TopLeft", FRAME);

    // Stated in pixels rather than as a ratio: the dimensions are integers, so the aspect can only
    // ever be right to within the rounding, and asserting a ratio to more precision than that is
    // asserting the rounding rather than the behaviour.
    expect(Math.abs(rect.height - rect.width / 4)).toBeLessThanOrEqual(1);
  });

  it("places each corner inside the frame", () => {
    const corners = ["TopLeft", "TopRight", "BottomLeft", "BottomRight"] as const;

    for (const corner of corners) {
      const rect = watermarkRect(200, 200, corner, FRAME);

      expect(rect.x).toBeGreaterThanOrEqual(0);
      expect(rect.y).toBeGreaterThanOrEqual(0);
      expect(rect.x + rect.width).toBeLessThanOrEqual(FRAME.width);
      expect(rect.y + rect.height).toBeLessThanOrEqual(FRAME.height);
    }
  });

  it("puts each corner where its name says", () => {
    const topLeft = watermarkRect(200, 200, "TopLeft", FRAME);
    const bottomRight = watermarkRect(200, 200, "BottomRight", FRAME);

    expect(topLeft.x).toBeLessThan(FRAME.width / 2);
    expect(topLeft.y).toBeLessThan(FRAME.height / 2);
    expect(bottomRight.x).toBeGreaterThan(FRAME.width / 2);
    expect(bottomRight.y).toBeGreaterThan(FRAME.height / 2);
  });

  it("has nothing to draw for an image that has not decoded", () => {
    expect(watermarkRect(0, 0, "TopRight", FRAME)).toMatchObject({ width: 0, height: 0 });
  });
});

describe("lowerThirdRect", () => {
  it("sits in the lower third of the frame, inset from the edges", () => {
    const rect = lowerThirdRect(FRAME);

    expect(rect.x).toBeGreaterThan(0);
    expect(rect.y).toBeGreaterThan(FRAME.height / 2);
    expect(rect.y + rect.height).toBeLessThan(FRAME.height);
    expect(rect.width).toBeLessThan(FRAME.width);
  });

  it("scales with the frame, so a caption reads the same at every size", () => {
    // Sizing in pixels would make a caption legible at 720p and illegible at 1080p, and viewers
    // watch at every size.
    const small = lowerThirdRect({ x: 0, y: 0, width: 640, height: 360 });
    const large = lowerThirdRect({ x: 0, y: 0, width: 1920, height: 1080 });

    expect(large.height / large.width).toBeCloseTo(small.height / small.width, 2);
  });
});
