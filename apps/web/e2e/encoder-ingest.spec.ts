import { execFileSync, spawn } from "node:child_process";
import { expect, test } from "@playwright/test";
import { API_URL, PASSWORD, createBroadcaster, statusOf } from "./support/journey";

/**
 * External encoder ingest (docs/decisions/0022-external-encoder-ingest.md).
 *
 * The case this exists for is the one native broadcasting cannot reach: a mobile game. No browser
 * on any phone can record another app, so the video has to arrive from an encoder — a screen
 * capture app, OBS, a capture card — over RTMP.
 *
 * FFmpeg stands in for that encoder here, exactly as the disposable MediaMTX stands in for an
 * external platform in the distribution specs: it is a real encoder speaking real RTMP into the
 * real gateway, which authorizes it through the real control plane.
 */

/** Whether a real encoder is available to this run. */
function hasFfmpeg(): boolean {
  try {
    execFileSync("ffmpeg", ["-version"], { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

const ffmpegAvailable = hasFfmpeg();

/**
 * Publishes a test pattern to an RTMP URL, returning a handle that kills it.
 *
 * Not awaited: the point is to have an encoder *running* while the session is driven, which is how
 * a real broadcast works and the only way the gateway reports ingest as connected.
 */
function publish(url: string): { stop: () => void } {
  const child = spawn(
    "ffmpeg",
    [
      "-hide_banner", "-loglevel", "error",
      "-re",
      "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
      "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
      "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
      "-b:v", "2000k", "-pix_fmt", "yuv420p", "-g", "60",
      "-c:a", "aac", "-b:a", "128k",
      "-f", "flv", url,
    ],
    { stdio: ["ignore", "ignore", "pipe"] },
  );

  return { stop: () => child.kill("SIGKILL") };
}

interface StreamKey {
  serverUrl: string;
  streamKey: string;
  fullUrl: string;
  srtUrl: string | null;
}

test.describe("external encoder ingest", () => {
  test("the studio issues a key, and rotating it invalidates the previous one", async ({
    page,
    request,
  }) => {
    // The browser half, which needs no encoder: the key is created from the studio, shown once,
    // and rotating replaces it.
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("E2E encoder");
    await page.getByRole("button", { name: /create and open studio/i }).click();
    await expect(page).toHaveURL(/\/studio\//);

    await page.getByRole("button", { name: /create a stream key/i }).click();

    // The server half is shown in the clear; the key is masked until asked for, because this panel
    // gets opened by somebody who is often already on camera.
    await expect(page.getByTestId("value-server")).toContainText("rtmp://");
    await expect(page.getByTestId("value-stream-key")).toContainText("•");

    await page.getByRole("button", { name: "Show" }).first().click();
    const first = await page.getByTestId("value-stream-key").textContent();
    expect(first).toContain("pass=");

    await page.getByRole("button", { name: /rotate key/i }).click();
    await page.getByRole("button", { name: "Show" }).first().click();

    await expect
      .poll(async () => page.getByTestId("value-stream-key").textContent())
      .not.toBe(first);
  });

  test("an encoder publishing over RTMP puts the session on air", async ({ page, request }) => {
    test.skip(!ffmpegAvailable, "needs ffmpeg on PATH to stand in for a phone's capture app");

    const { accessToken } = await createBroadcaster(request);

    const created = await request.post(`${API_URL}/api/v1/live-sessions`, {
      headers: { Authorization: `Bearer ${accessToken}` },
      data: { title: "E2E encoder publish", visibility: "PUBLIC", recordingEnabled: false },
    });
    expect(created.ok(), `create failed: ${await created.text()}`).toBeTruthy();
    const sessionId = ((await created.json()) as { id: string }).id;

    await request.post(`${API_URL}/api/v1/live-sessions/${sessionId}/prepare`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });

    const keyResponse = await request.post(
      `${API_URL}/api/v1/live-sessions/${sessionId}/sources/stream-key`,
      { headers: { Authorization: `Bearer ${accessToken}` } },
    );
    expect(keyResponse.ok(), `stream key failed: ${await keyResponse.text()}`).toBeTruthy();
    const key = (await keyResponse.json()) as StreamKey;

    // The pre-joined URL is what an encoder that takes a single field is given, and asserting on it
    // is what proves the two halves join into something that actually connects.
    const encoder = publish(key.fullUrl);

    try {
      await request.post(`${API_URL}/api/v1/live-sessions/${sessionId}/start`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      });

      await expect
        .poll(async () => (await statusOf(request, sessionId, accessToken)).isBroadcasting, {
          timeout: 60_000,
        })
        .toBeTruthy();

      // And the viewer page plays it, which is the difference between "the gateway accepted a
      // connection" and "somebody can watch a game being streamed from a phone".
      await page.goto(`/watch/${sessionId}`);
      await expect(page.getByTestId("live-player")).toBeVisible({ timeout: 30_000 });
    } finally {
      encoder.stop();
    }

    await request.post(`${API_URL}/api/v1/live-sessions/${sessionId}/stop`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
  });

  test("a wrong key is refused by the gateway", async ({ request }) => {
    test.skip(!ffmpegAvailable, "needs ffmpeg on PATH to stand in for a phone's capture app");

    const { accessToken } = await createBroadcaster(request);

    const created = await request.post(`${API_URL}/api/v1/live-sessions`, {
      headers: { Authorization: `Bearer ${accessToken}` },
      data: { title: "E2E encoder refusal", visibility: "PUBLIC", recordingEnabled: false },
    });
    const sessionId = ((await created.json()) as { id: string }).id;

    await request.post(`${API_URL}/api/v1/live-sessions/${sessionId}/prepare`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });

    const key = (await (
      await request.post(`${API_URL}/api/v1/live-sessions/${sessionId}/sources/stream-key`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      })
    ).json()) as StreamKey;

    const wrong = key.fullUrl.replace(/pass=[^&]*/, "pass=definitely-not-the-key");
    const encoder = publish(wrong);

    try {
      // Asserted on the session rather than on the encoder's exit code, deliberately: ffmpeg exits
      // 0 when an RTMP server closes the connection on it, so "the encoder did not complain" is no
      // evidence at all that a key was accepted. The session is the only honest witness.
      await waitFor(3000);
      const status = await statusOf(request, sessionId, accessToken);
      expect(status.isBroadcasting).toBeFalsy();
    } finally {
      encoder.stop();
    }
  });
});

/** A plain delay. The assertion above is about something *not* happening, which needs a wait. */
function waitFor(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
