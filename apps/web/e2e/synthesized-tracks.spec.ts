import type { APIRequestContext, Page } from "@playwright/test";
import { expect, test } from "@playwright/test";
import { createBroadcaster, PASSWORD, statusOf } from "./support/journey";

/**
 * A broadcast missing a kind of media still has to arrive somewhere that will show it.
 *
 * Facebook Live requires an audio track. A video-only stream is accepted, counted as live, and
 * never shown to anybody — no error, no warning, nothing in any log to read afterwards. So the
 * relay generates the missing kind rather than sending a stream the platform will quietly hide.
 *
 * These specs prove it end to end: a real studio, the real gateway, the real source inspection and
 * the real encoder, into a real RTMP receiver that reports what it actually got.
 */

const PLATFORM_HOST = process.env.E2E_FAKE_PLATFORM ?? "fake-platform";
const PLATFORM_API = process.env.E2E_FAKE_PLATFORM_API ?? "http://localhost:9998";

const BROADCASTING = ["LIVE", "DEGRADED"];

/**
 * Takes one kind of capture device away from the browser.
 *
 * Chromium's fake-device flags synthesise a camera *and* a microphone, and there is no flag to
 * supply only one — so the machine this suite runs on cannot express "no microphone" any other way.
 * This substitutes hardware, exactly as `--use-fake-device-for-media-stream` already does for the
 * rest of the suite. Everything downstream of `getUserMedia` is the real thing.
 */
async function without(page: Page, missing: "audioinput" | "videoinput"): Promise<void> {
  await page.addInitScript((kind: string) => {
    const refused: "audio" | "video" = kind === "audioinput" ? "audio" : "video";
    const media = navigator.mediaDevices;
    const openDevice = media.getUserMedia.bind(media);
    const listDevices = media.enumerateDevices.bind(media);

    media.getUserMedia = (constraints?: MediaStreamConstraints) => {
      if (constraints?.[refused]) {
        const error = new Error("Requested device not found");
        error.name = "NotFoundError";
        return Promise.reject(error);
      }

      return openDevice(constraints);
    };

    media.enumerateDevices = async () =>
      (await listDevices()).filter((device) => device.kind !== kind);
  }, missing);
}

interface PlatformPath {
  tracks?: string[];
  ready?: boolean;
}

/** What the receiver says it is holding, which is the only assertion that counts here. */
async function platformTracks(request: APIRequestContext, streamKey: string): Promise<string[]> {
  try {
    const response = await request.get(`${PLATFORM_API}/v3/paths/get/live/${streamKey}`, {
      timeout: 5000,
    });

    if (!response.ok()) return [];

    const path = (await response.json()) as PlatformPath;
    return path.ready ? (path.tracks ?? []) : [];
  } catch {
    return [];
  }
}

/** Signs in, creates a session with one destination, and goes live on whatever devices opened. */
async function goLive(
  page: Page,
  request: APIRequestContext,
  title: string,
  streamKey: string,
): Promise<{ sessionId: string; token: string }> {
  const { email } = await createBroadcaster(request);

  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).toHaveURL(/\/dashboard/);

  await page.getByRole("link", { name: "New live session" }).click();
  await page.getByLabel("Title").fill(title);
  await page.getByRole("button", { name: /create and open studio/i }).click();
  await expect(page).toHaveURL(/\/studio\//);

  const sessionId = page.url().split("/studio/")[1]!;

  await page.getByRole("button", { name: "Add destination" }).click();
  await page.getByLabel("Platform").selectOption("CustomRtmp");
  await page.getByLabel("Name").fill("Test platform");
  await page.getByLabel("Server URL").fill(`rtmp://${PLATFORM_HOST}:1935/live`);
  await page.getByLabel("Stream key").fill(streamKey);
  // With the form open the toggle reads "Cancel", so this matches only the submit button.
  await page.getByRole("button", { name: "Add destination" }).click();
  await expect(page.getByText("Test platform")).toBeVisible();

  await page.getByRole("button", { name: /use camera and microphone/i }).click();

  // One device is enough to broadcast, which is what makes the rest of this spec reachable.
  await expect(page.getByRole("button", { name: "Start Live" })).toBeEnabled();
  await page.getByRole("button", { name: "Start Live" }).click();

  const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
  expect(token).toBeTruthy();

  await expect
    .poll(async () => BROADCASTING.includes((await statusOf(request, sessionId, token!)).status), {
      timeout: 60_000,
    })
    .toBeTruthy();

  return { sessionId, token: token! };
}

test.describe.configure({ mode: "serial" });

test.describe("a broadcast missing a kind of media", () => {
  test("with no microphone, reaches the platform carrying audio", async ({ page, request }) => {
    const streamKey = `e2e-silent-${Date.now()}`;
    await without(page, "audioinput");

    await goLive(page, request, "No microphone", streamKey);

    // The studio itself is honest about having no sound of its own.
    await expect(page.getByRole("button", { name: /None\s*Microphone/i })).toBeDisabled();

    // The assertion this spec exists for. The source has no audio; what reaches the platform does.
    await expect
      .poll(() => platformTracks(request, streamKey), { timeout: 120_000 })
      .toEqual(expect.arrayContaining([expect.stringMatching(/audio/i)]));

    // And the picture is still the broadcast's own, not a generated one.
    expect(await platformTracks(request, streamKey)).toEqual(
      expect.arrayContaining([expect.stringMatching(/h264/i)]),
    );

    await page.getByRole("button", { name: "Stop Live" }).click();

    // The generated track never ends on its own. A relay that did not cut it short would keep
    // pushing silence at the platform long after the broadcaster had gone.
    await expect.poll(() => platformTracks(request, streamKey), { timeout: 60_000 }).toEqual([]);
  });

  test("with no camera, reaches the platform carrying a picture", async ({ page, request }) => {
    // The same failure in the other direction: a platform will not show an audio-only RTMP stream
    // either, and a machine with a headset and no webcam is an ordinary way to arrive here.
    const streamKey = `e2e-blank-${Date.now()}`;
    await without(page, "videoinput");

    await goLive(page, request, "No camera", streamKey);

    await expect
      .poll(() => platformTracks(request, streamKey), { timeout: 120_000 })
      .toEqual(expect.arrayContaining([expect.stringMatching(/h264/i)]));

    expect(await platformTracks(request, streamKey)).toEqual(
      expect.arrayContaining([expect.stringMatching(/audio/i)]),
    );

    await page.getByRole("button", { name: "Stop Live" }).click();
    await expect.poll(() => platformTracks(request, streamKey), { timeout: 60_000 }).toEqual([]);
  });
});
