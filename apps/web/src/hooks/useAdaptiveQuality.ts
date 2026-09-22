"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import {
  type AdaptiveState,
  type EncodingRung,
  INITIAL_ADAPTIVE_STATE,
  MOBILE_LADDER,
  adapt,
  resetLadder,
} from "@/lib/media/adaptive";
import type { PublishQuality } from "@/lib/media/quality";

export interface UseAdaptiveQualityResult {
  /** The rung the encoder is being held at. The top rung means nothing has been taken away. */
  rung: EncodingRung;
  /** True while the ladder is holding the broadcast below full quality. */
  reduced: boolean;
  /** What the ladder last did and why, for the broadcaster to read. Dismissable. */
  notice: string | null;
  dismissNotice: () => void;
  /** Puts the ladder back at the top — used when the capture rung is changed by hand. */
  reset: () => void;
}

interface UseAdaptiveQualityOptions {
  /**
   * Whether to adapt at all.
   *
   * Off is the desktop policy and the documented default (ADR 0011 §7): at a desk somebody is
   * watching, and a quality change they did not ask for is a change they have to diagnose. On is
   * the mobile policy (ADR 0021), where there is no such person.
   */
  enabled: boolean;
  /** The latest measurement from the publisher. A new object per sample; null when not publishing. */
  quality: PublishQuality | null;
  applyEncoding: (limits: EncodingRung) => Promise<void>;
}

/**
 * Turns measurements into encoder changes.
 *
 * The decision itself lives in `@/lib/media/adaptive` and is pure. This hook is the part that
 * cannot be: it holds the ladder's position across samples, calls the publisher, and keeps one
 * sentence of explanation for the interface.
 *
 * The position is held in a ref rather than state on purpose. It changes on every sample, and
 * re-rendering a full-screen camera preview twice a second to record that nothing happened is
 * exactly the kind of waste this whole phase exists to remove.
 */
export function useAdaptiveQuality({
  enabled,
  quality,
  applyEncoding,
}: UseAdaptiveQualityOptions): UseAdaptiveQualityResult {
  const stateRef = useRef<AdaptiveState>(INITIAL_ADAPTIVE_STATE);
  const [rung, setRung] = useState<EncodingRung>(MOBILE_LADDER[0]!);
  const [notice, setNotice] = useState<string | null>(null);

  // Held in a ref so a caller passing an inline function — which the studio does — cannot restart
  // the ladder on every render.
  const applyRef = useRef(applyEncoding);

  useEffect(() => {
    applyRef.current = applyEncoding;
  }, [applyEncoding]);

  /**
   * Whether the ladder was running on the previous sample.
   *
   * Each broadcast gets a new publisher, and a new publisher starts at full quality with no limits
   * applied. Carrying a rung across from the last one would leave the ladder believing the encoder
   * is held down when it is not — and the first "climb" would then *apply* a reduced rung to a
   * perfectly healthy stream.
   */
  const wasEnabledRef = useRef(false);

  useEffect(() => {
    if (!enabled) {
      wasEnabledRef.current = false;
      return;
    }

    if (!wasEnabledRef.current) {
      wasEnabledRef.current = true;
      stateRef.current = INITIAL_ADAPTIVE_STATE;
      setRung(MOBILE_LADDER[0]!);
    }

    const decision = adapt(stateRef.current, quality, Date.now());
    stateRef.current = decision.state;

    if (!decision.change) return;

    const { rung: next, reason } = decision.change;
    setRung(next);
    setNotice(reason);

    // Fire-and-forget: `applyEncoding` swallows its own failures, and a rung that would not apply
    // must not stop the ladder from trying the next one.
    void applyRef.current(next);
  }, [enabled, quality]);

  const reset = useCallback(() => {
    stateRef.current = resetLadder(Date.now());
    setRung(MOBILE_LADDER[0]!);
    setNotice(null);
    void applyRef.current(MOBILE_LADDER[0]!);
  }, []);

  const dismissNotice = useCallback(() => setNotice(null), []);

  return {
    rung,
    reduced: rung.id !== MOBILE_LADDER[0]!.id,
    notice,
    dismissNotice,
    reset,
  };
}
