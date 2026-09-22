"use client";

import { useEffect, useSyncExternalStore } from "react";
import { type FormFactor, MOBILE_VIEWPORT_WIDTH, detectFormFactor } from "@/lib/media/mobile";

/** Where a manual choice is kept, so it survives the reload a broadcast recovery may involve. */
const OVERRIDE_KEY = "livestream.studio.view";

/** The `view` query parameter, which is how each studio links to the other. */
function requestedView(): FormFactor | null {
  if (typeof window === "undefined") return null;

  const requested = new URLSearchParams(window.location.search).get("view");
  return requested === "mobile" || requested === "desktop" ? requested : null;
}

function storedView(): FormFactor | null {
  try {
    const stored = window.localStorage.getItem(OVERRIDE_KEY);
    return stored === "mobile" || stored === "desktop" ? stored : null;
  } catch {
    // Private mode, or storage disabled. Detection decides instead.
    return null;
  }
}

/**
 * Subscribes to the one thing that can change the answer.
 *
 * Only the viewport is watched. A pointer does not become a finger, and a user agent does not
 * change — but a tablet rotates, and a desktop window is resized.
 */
function subscribe(onChange: () => void): () => void {
  const query = window.matchMedia?.(`(max-width: ${MOBILE_VIEWPORT_WIDTH - 1}px)`);
  query?.addEventListener?.("change", onChange);
  return () => query?.removeEventListener?.("change", onChange);
}

function getSnapshot(): FormFactor {
  // A manual choice wins, and is not re-evaluated on rotation: somebody who asked for the full
  // studio on a phone asked for it, and having it taken away by turning the phone would be
  // maddening.
  const override = requestedView() ?? storedView();
  if (override) return override;

  return detectFormFactor({
    coarsePointer: window.matchMedia?.("(pointer: coarse)").matches ?? false,
    maxTouchPoints: navigator.maxTouchPoints ?? 0,
    viewportWidth: window.innerWidth,
    userAgent: navigator.userAgent,
  });
}

/** There is no viewport and no pointer on the server, so there is no honest answer to give. */
function getServerSnapshot(): FormFactor | null {
  return null;
}

/**
 * Which broadcaster to render.
 *
 * `null` until the first client render, and callers must handle it. The answer depends on the
 * viewport and the pointer, neither of which exists on the server; guessing during server
 * rendering produces a hydration mismatch that React resolves by re-rendering the whole studio —
 * on the device that can least afford it.
 *
 * Read through `useSyncExternalStore` rather than kept in state, because that is what this is: a
 * value owned by the browser, which React subscribes to.
 */
export function useFormFactor(): FormFactor | null {
  const formFactor = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);

  // Remembering an explicit `?view=` is a side effect and belongs here rather than in the
  // snapshot, which has to stay pure and is read on every render.
  useEffect(() => {
    const requested = requestedView();
    if (!requested) return;

    try {
      window.localStorage.setItem(OVERRIDE_KEY, requested);
    } catch {
      // The parameter still applies to this page; it simply will not survive a reload.
    }
  }, []);

  return formFactor;
}
