"use client";

import { LiveStudio } from "@/components/studio/LiveStudio";
import { MobileStudio } from "@/components/mobile/MobileStudio";
import { useFormFactor } from "@/hooks/useFormFactor";

/**
 * Chooses which broadcaster this device gets.
 *
 * Both drive the same live session through the same API, so this is a presentation decision and
 * nothing more — which is what makes it safe to get wrong occasionally, and why each screen links
 * to the other rather than trapping anybody in the one they were given.
 *
 * Nothing is rendered until the answer is known. The alternative is rendering the desktop studio
 * on the server and replacing it on the client, which on a phone means acquiring a camera into a
 * component tree that is about to be thrown away — and a permission prompt answered against a
 * screen that no longer exists.
 */
export function StudioShell({ sessionId }: { sessionId: string }) {
  const formFactor = useFormFactor();

  if (formFactor === null) {
    return (
      <div className="flex min-h-[60vh] items-center justify-center">
        <p className="text-sm text-slate-500">Loading your studio…</p>
      </div>
    );
  }

  return formFactor === "mobile" ? (
    <MobileStudio sessionId={sessionId} />
  ) : (
    <LiveStudio sessionId={sessionId} />
  );
}
