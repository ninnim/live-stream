import { describe, expect, it } from "vitest";
import { type CaptureAttempt, type CaptureOutcome, summarizeCaptureAttempt } from "@/hooks/media-access";
import { MediaAccessError, type MediaErrorReason } from "@/lib/media/devices";

function failed(reason: MediaErrorReason): CaptureOutcome {
  return { status: "failed", error: new MediaAccessError(reason, "message", "recovery") };
}

const opened: CaptureOutcome = { status: "opened" };
const skipped: CaptureOutcome = { status: "skipped" };

function attempt(camera: CaptureOutcome, microphone: CaptureOutcome): CaptureAttempt {
  return { camera, microphone };
}

/**
 * Entry policy for the studio (docs/04-native-broadcasting.md).
 *
 * The rule under test: **one working device is enough.** A machine with no webcam is an ordinary
 * machine, and refusing to let it broadcast decides that owning a webcam is a requirement — which
 * it is not, because a screen and a voice are a broadcast.
 */
describe("summarizeCaptureAttempt", () => {
  it("lets a machine with no camera into the studio on its microphone alone", () => {
    const result = summarizeCaptureAttempt(attempt(failed("no-device"), opened));

    expect(result.granted).toBe(true);
    expect(result.error).toBeNull();
    expect(result.notice).toMatch(/No camera found/);
  });

  it("lets a machine with no microphone in, and says the broadcast will be silent", () => {
    const result = summarizeCaptureAttempt(attempt(opened, failed("no-device")));

    expect(result.granted).toBe(true);
    expect(result.notice).toMatch(/no sound/i);
  });

  it("says nothing when both devices opened", () => {
    const result = summarizeCaptureAttempt(attempt(opened, opened));

    expect(result.granted).toBe(true);
    expect(result.notice).toBeNull();
    expect(result.error).toBeNull();
  });

  it("treats a blocked camera as a reason to offer the screen, not as a failure", () => {
    const result = summarizeCaptureAttempt(attempt(failed("permission-denied"), opened));

    expect(result.granted).toBe(true);
    expect(result.notice).toMatch(/blocked.*share a screen/i);
  });

  it("refuses when neither device could be opened", () => {
    const result = summarizeCaptureAttempt(attempt(failed("no-device"), failed("no-device")));

    expect(result.granted).toBe(false);
    // One message about the machine, not two about its parts — and it names the way forward.
    expect(result.error?.userMessage).toBe("No camera or microphone was found.");
    expect(result.error?.recovery).toMatch(/Share a screen/);
  });

  it("reports a blanket denial rather than either device's own message", () => {
    // Denying the browser prompt fails both. Reporting the camera's message alone would send
    // somebody looking for a hardware fault.
    const result = summarizeCaptureAttempt(
      attempt(failed("permission-denied"), failed("permission-denied")),
    );

    expect(result.granted).toBe(false);
    expect(result.error?.reason).toBe("permission-denied");
  });

  it("prefers the denial when one device is absent and the other was blocked", () => {
    const result = summarizeCaptureAttempt(attempt(failed("no-device"), failed("permission-denied")));

    expect(result.error?.reason).toBe("permission-denied");
  });

  it("reports a device that is in use rather than swallowing it", () => {
    const result = summarizeCaptureAttempt(attempt(failed("device-in-use"), failed("no-device")));

    expect(result.granted).toBe(false);
    expect(result.error?.reason).toBe("device-in-use");
  });

  it("does not describe a device that was never asked for as missing", () => {
    // Entering by sharing a screen skips the camera. Saying "no camera found" would be a
    // statement about the hardware that nobody checked.
    const result = summarizeCaptureAttempt(attempt(skipped, opened));

    expect(result.granted).toBe(true);
    expect(result.notice).toBeNull();
  });
});
