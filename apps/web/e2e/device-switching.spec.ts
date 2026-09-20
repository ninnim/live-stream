import { expect, test } from "@playwright/test";
import { goLive, mediaControlApi, mediaPathOf, statusOf } from "./support/journey";

/**
 * Changing capture devices mid-broadcast, against real infrastructure.
 *
 * The claim under test is narrow and load-bearing: swapping a camera must not restart the stream.
 * `RTCRtpSender.replaceTrack` exchanges the source inside the transport that is already open, so
 * the proof is that the **gateway sees the same WebRTC session before and after**. A new session id
 * would mean the audience got a dropout, which is exactly the behaviour that used to force the
 * studio to disable the picker while live.
 */

/**
 * Two synthetic cameras rather than the suite's usual one, so "switch to the other camera" is a
 * real device change rather than re-opening the same hardware.
 */
test.use({
  launchOptions: {
    args: [
      "--use-fake-device-for-media-stream=device-count=2",
      "--use-fake-ui-for-media-stream",
      "--autoplay-policy=no-user-gesture-required",
    ],
  },
});

interface WebRtcSession {
  id: string;
  path: string;
  state: string;
}

/** The gateway's view of who is publishing to a path. */
function publisherSessionId(mediaPath: string): string | null {
  const listing = mediaControlApi("/v3/webrtcsessions/list") as { items?: WebRtcSession[] } | null;
  return listing?.items?.find((s) => s.path === mediaPath && s.state === "publish")?.id ?? null;
}

/** A session that is on air. DEGRADED counts: the synthetic camera is below the health threshold. */
const BROADCASTING = ["LIVE", "DEGRADED"];

test.describe("Changing devices while live", () => {
  test("swaps camera without interrupting the broadcast", async ({ page, request }) => {
    const { sessionId, token } = await goLive(page, request, "Device switching");
    const mediaPath = await mediaPathOf(request, sessionId);

    await expect
      .poll(() => publisherSessionId(mediaPath), { timeout: 30_000 })
      .not.toBeNull();

    const before = publisherSessionId(mediaPath);

    // `getByLabel` is ambiguous here: the preview element is also labelled "Camera preview". The
    // role narrows it to the picker, and would fail loudly rather than silently matching a video.
    const cameraPicker = page.getByRole("combobox", { name: "Camera" });
    const options = await cameraPicker.locator("option").all();
    expect(
      options.length,
      "this spec needs two synthetic cameras — check the --use-fake-device-for-media-stream flag",
    ).toBeGreaterThan(1);

    // The picker is enabled while live. That is the feature.
    await expect(cameraPicker).toBeEnabled();

    const current = await cameraPicker.inputValue();
    const other = (await Promise.all(options.map((option) => option.getAttribute("value")))).find(
      (value) => value && value !== current,
    );
    expect(other).toBeTruthy();

    await cameraPicker.selectOption(other!);

    // The studio must land on the chosen device rather than silently falling back.
    await expect.poll(() => cameraPicker.inputValue(), { timeout: 15_000 }).toBe(other);

    // The session never left the air...
    const status = await statusOf(request, sessionId, token);
    expect(BROADCASTING).toContain(status.status);

    // ...and, the point of the exercise, it is the same transport throughout. A different id here
    // would mean the publisher tore down and reconnected, which the audience would have seen.
    expect(publisherSessionId(mediaPath)).toBe(before);
  });

  test("changes quality without interrupting the broadcast", async ({ page, request }) => {
    // A quality change re-opens the camera at a new resolution, so it exercises the same swap with
    // a track the browser has genuinely re-negotiated rather than merely a different device.
    const { sessionId, token } = await goLive(page, request, "Quality switching");
    const mediaPath = await mediaPathOf(request, sessionId);

    await expect
      .poll(() => publisherSessionId(mediaPath), { timeout: 30_000 })
      .not.toBeNull();

    const before = publisherSessionId(mediaPath);

    const qualityPicker = page.getByRole("combobox", { name: "Quality" });
    await qualityPicker.selectOption("540p");
    await expect.poll(() => qualityPicker.inputValue(), { timeout: 15_000 }).toBe("540p");

    const status = await statusOf(request, sessionId, token);
    expect(BROADCASTING).toContain(status.status);
    expect(publisherSessionId(mediaPath)).toBe(before);
  });

  test("keeps exactly one publisher on the session path after a switch", async ({ page, request }) => {
    // A swap that added a publisher rather than replacing one would leave two encoders fighting for
    // the same path, which the gateway resolves by rejecting one — intermittently, and in
    // production rather than here.
    const { sessionId } = await goLive(page, request, "Single publisher");
    const mediaPath = await mediaPathOf(request, sessionId);

    await expect
      .poll(() => publisherSessionId(mediaPath), { timeout: 30_000 })
      .not.toBeNull();

    const qualityPicker = page.getByRole("combobox", { name: "Quality" });
    await qualityPicker.selectOption("540p");
    await expect.poll(() => qualityPicker.inputValue(), { timeout: 15_000 }).toBe("540p");

    const listing = mediaControlApi("/v3/webrtcsessions/list") as { items?: WebRtcSession[] } | null;
    const publishers = (listing?.items ?? []).filter(
      (s) => s.path === mediaPath && s.state === "publish",
    );

    expect(publishers).toHaveLength(1);
  });
});
