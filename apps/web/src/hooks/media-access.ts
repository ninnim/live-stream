import { MediaAccessError } from "@/lib/media/devices";

/** What happened when the studio tried to open one kind of device. */
export type CaptureOutcome =
  | { status: "opened" }
  | { status: "skipped" }
  | { status: "failed"; error: MediaAccessError };

export interface CaptureAttempt {
  camera: CaptureOutcome;
  microphone: CaptureOutcome;
}

/**
 * Decides what an entry attempt means, given what each device did.
 *
 * Pure, because the interesting part is the policy rather than the plumbing: which combinations
 * count as being in the studio, which count as failure, and what the operator is told about the
 * device they did not get.
 *
 * The rule: **one working device is enough.** A machine with no webcam is a normal machine, and a
 * platform that refuses to let it broadcast has decided that owning a webcam is a requirement —
 * which it is not, because a screen and a voice are a broadcast.
 */
export function summarizeCaptureAttempt(attempt: CaptureAttempt): {
  granted: boolean;
  error: MediaAccessError | null;
  notice: string | null;
} {
  const opened = (outcome: CaptureOutcome): boolean => outcome.status === "opened";
  const failure = (outcome: CaptureOutcome): MediaAccessError | null =>
    outcome.status === "failed" ? outcome.error : null;

  if (opened(attempt.camera) || opened(attempt.microphone)) {
    return { granted: true, error: null, notice: describeMissing(attempt) };
  }

  const cameraError = failure(attempt.camera);
  const microphoneError = failure(attempt.microphone);

  // A blanket permission denial is the more useful thing to report: it is the one the operator can
  // act on, and it explains both failures at once.
  const denial = [cameraError, microphoneError].find((e) => e?.reason === "permission-denied");

  if (denial) {
    return { granted: false, error: denial, notice: null };
  }

  // Nothing opened and nothing was denied, so the machine simply has neither device. Say that,
  // rather than repeating a per-device message that reads like a hardware fault.
  if (cameraError?.reason === "no-device" && microphoneError?.reason === "no-device") {
    return {
      granted: false,
      error: new MediaAccessError(
        "no-device",
        "No camera or microphone was found.",
        "Share a screen instead, or connect a device and try again.",
      ),
      notice: null,
    };
  }

  return { granted: false, error: cameraError ?? microphoneError, notice: null };
}

/**
 * One line about the device that is not there, or null when both are.
 *
 * Deliberately a notice rather than an error: nothing is broken, and the operator is about to be
 * offered the alternative.
 */
function describeMissing(attempt: CaptureAttempt): string | null {
  const missing = (outcome: CaptureOutcome): boolean => outcome.status === "failed";

  const cameraMissing = missing(attempt.camera);
  const microphoneMissing = missing(attempt.microphone);

  if (cameraMissing && microphoneMissing) return null; // Unreachable: one of them opened.

  if (cameraMissing) {
    return attempt.camera.status === "failed" && attempt.camera.error.reason === "permission-denied"
      ? "Camera access was blocked. You can still share a screen."
      : "No camera found. You can share a screen instead.";
  }

  if (microphoneMissing) {
    return attempt.microphone.status === "failed"
      && attempt.microphone.error.reason === "permission-denied"
      ? "Microphone access was blocked. Your broadcast will have no sound."
      : "No microphone found. Your broadcast will have no sound.";
  }

  return null;
}
