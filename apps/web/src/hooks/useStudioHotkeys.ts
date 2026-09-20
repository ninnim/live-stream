"use client";

import { useEffect, useRef } from "react";

export interface StudioHotkeyActions {
  /** Recall the nth scene, zero-based. */
  recallScene: (index: number) => void;
  /** Cut to the nth source on the switcher, zero-based. */
  cutToSource: (index: number) => void;
  setLayout: (layout: "solo" | "side-by-side" | "picture-in-picture") => void;
  toggleLowerThird: () => void;
  toggleMicrophone: () => void;
  toggleCamera: () => void;
}

/**
 * Keyboard control for the switcher.
 *
 * A live show is operated by hand, and reaching for a mouse costs a beat that is visible on air.
 * The bindings follow the vocabulary of a hardware switcher rather than the application: number
 * keys select, letters modify.
 *
 * | Key | Action |
 * |---|---|
 * | `1`–`9` | Recall scene |
 * | `Shift`+`1`–`9` | Cut to source |
 * | `Q` / `W` / `E` | Full screen / side by side / picture in picture |
 * | `L` | Caption on or off |
 * | `M` | Microphone on or off |
 * | `V` | Camera on or off |
 *
 * Deliberately no binding for going live or stopping. Ending a broadcast with a stray keystroke is
 * a failure no undo can repair, so it stays a deliberate press of a button.
 */
export function useStudioHotkeys(actions: StudioHotkeyActions, enabled: boolean): void {
  // Held in a ref so a caller passing inline closures — which every caller does — does not rebind
  // the listener on every render.
  const actionsRef = useRef(actions);

  useEffect(() => {
    actionsRef.current = actions;
  }, [actions]);

  useEffect(() => {
    if (!enabled || typeof window === "undefined") return;

    const onKeyDown = (event: KeyboardEvent): void => {
      // Never steal a keystroke from someone typing. A producer naming a scene "1" must get the
      // character, not a cut.
      if (isTypingTarget(event.target) || event.metaKey || event.ctrlKey || event.altKey) return;

      const digit = Number.parseInt(event.key, 10);
      if (Number.isInteger(digit) && digit >= 1 && digit <= 9) {
        event.preventDefault();
        actionsRef.current.recallScene(digit - 1);
        return;
      }

      // Shift+number arrives as a symbol on most layouts, so the physical key is used instead —
      // `code` is layout-independent and `Digit1`…`Digit9` are stable everywhere.
      if (event.shiftKey && /^Digit[1-9]$/.test(event.code)) {
        event.preventDefault();
        actionsRef.current.cutToSource(Number(event.code.slice(-1)) - 1);
        return;
      }

      switch (event.key.toLowerCase()) {
        case "q":
          event.preventDefault();
          actionsRef.current.setLayout("solo");
          break;
        case "w":
          event.preventDefault();
          actionsRef.current.setLayout("side-by-side");
          break;
        case "e":
          event.preventDefault();
          actionsRef.current.setLayout("picture-in-picture");
          break;
        case "l":
          event.preventDefault();
          actionsRef.current.toggleLowerThird();
          break;
        case "m":
          event.preventDefault();
          actionsRef.current.toggleMicrophone();
          break;
        case "v":
          event.preventDefault();
          actionsRef.current.toggleCamera();
          break;
        default:
          break;
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [enabled]);
}

/** True when the keystroke belongs to whatever the person is typing into. */
export function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;

  const tag = target.tagName.toLowerCase();
  if (tag === "input" || tag === "textarea" || tag === "select") return true;

  // Compared rather than returned directly: `isContentEditable` is not implemented everywhere, and
  // an undefined leaking out of a function declared to return a boolean is the kind of thing that
  // reads as false in one place and is checked with `=== false` in another.
  return target.isContentEditable === true;
}
