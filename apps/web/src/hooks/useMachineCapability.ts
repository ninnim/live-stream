"use client";

import { useEffect, useRef, useState } from "react";
import {
  type EncodingProbe,
  type MachineCapability,
  probeMachine,
} from "@/lib/media/capability";

/**
 * Asks the browser what this machine can encode, once, on the way into the studio.
 *
 * Runs in an effect rather than during render because it is asynchronous and because it touches
 * `navigator` — a server render has neither. Until it answers, `capability` is null and every
 * caller keeps the platform default, which is what makes this an improvement on a good machine
 * rather than a dependency on a fast answer.
 */
export function useMachineCapability(probe?: EncodingProbe | null, cores?: number | null): MachineCapability | null {
  const [capability, setCapability] = useState<MachineCapability | null>(null);

  // Probing is a fixed, cheap question about the machine; re-running it on every render of a
  // component that passes an inline probe would be pure waste.
  const requested = useRef(false);

  useEffect(() => {
    if (requested.current) return;
    requested.current = true;

    let cancelled = false;

    void probeMachine(probe, cores).then((result) => {
      if (!cancelled) setCapability(result);
    });

    return () => {
      cancelled = true;
    };
    // Deliberately once: the answer describes hardware, which does not change while a tab is open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return capability;
}
