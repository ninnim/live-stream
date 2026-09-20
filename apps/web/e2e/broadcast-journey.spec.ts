import { expect, test } from "@playwright/test";
import { kickPublisher } from "./support/journey";

/**
 * The Phase 1 acceptance journey against real infrastructure.
 *
 * Nothing is mocked here: a real Chromium publishes a real WebRTC stream over WHIP into the real
 * media gateway, which authorizes it by calling back into the real API, which drives the real state
 * machine and writes a real recording.
 *
 * This is the one test that proves "a creator can broadcast from our platform without OBS".
 */

const API_URL = process.env.E2E_API_URL ?? "http://localhost:8080";
const PASSWORD = "an-example-password-1234";

interface Registered {
  accessToken: string;
  email: string;
}

async function register(request: import("@playwright/test").APIRequestContext): Promise<Registered> {
  const email = `e2e-${Date.now()}-${Math.floor(Math.random() * 10_000)}@example.com`;

  const response = await request.post(`${API_URL}/api/v1/auth/register`, {
    data: { email, password: PASSWORD, displayName: "E2E Broadcaster", workspaceName: "E2E Workspace" },
  });

  expect(response.ok(), `registration failed: ${await response.text()}`).toBeTruthy();
  const body = (await response.json()) as { accessToken: string };
  return { accessToken: body.accessToken, email };
}

test.describe("native web broadcasting", () => {
  test("a creator broadcasts, a viewer watches, and the recording is finalized", async ({
    page,
    request,
    context,
  }) => {
    // ---------------------------------------------------------------------------------------
    // Sign in through the UI, so the studio holds real tokens.
    // ---------------------------------------------------------------------------------------
    const { email } = await register(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page).toHaveURL(/\/dashboard/);

    // ---------------------------------------------------------------------------------------
    // Create a recorded session.
    // ---------------------------------------------------------------------------------------
    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("E2E broadcast");
    await page.getByLabel("Visibility").selectOption("PUBLIC");
    await page.getByRole("button", { name: /create and open studio/i }).click();

    await expect(page).toHaveURL(/\/studio\//);
    const sessionId = page.url().split("/studio/")[1]!;
    expect(sessionId).toBeTruthy();

    // ---------------------------------------------------------------------------------------
    // Camera and microphone — Chromium's fake device satisfies getUserMedia for real.
    // ---------------------------------------------------------------------------------------
    await page.getByRole("button", { name: /use camera and microphone/i }).click();

    const preview = page.getByTestId("camera-preview");
    await expect(preview).toBeVisible();

    // A real MediaStream must be attached and producing frames, not just an empty element.
    await expect
      .poll(
        async () =>
          preview.evaluate((el: HTMLVideoElement) => el.srcObject !== null && el.videoWidth > 0),
        { message: "camera preview never produced video frames", timeout: 20_000 },
      )
      .toBe(true);

    await expect(page.getByRole("combobox", { name: "Camera" })).toBeEnabled();

    // ---------------------------------------------------------------------------------------
    // Go live. This publishes over WHIP and waits for the server to confirm ingest.
    // ---------------------------------------------------------------------------------------
    await page.getByRole("button", { name: "Start Live" }).click();

    // The badge only reads LIVE once the API has confirmed media reached the gateway.
    await expect(page.getByText("Live", { exact: true }).first()).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole("button", { name: "Stop Live" })).toBeVisible();

    // ---------------------------------------------------------------------------------------
    // The server agrees, independently of what the UI is rendering.
    // ---------------------------------------------------------------------------------------
    const { accessToken } = await register(request); // separate token for a clean API check
    void accessToken;

    await expect
      .poll(
        async () => {
          const response = await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/playback`);
          if (!response.ok()) return null;
          return ((await response.json()) as { isLive: boolean }).isLive;
        },
        { message: "API never reported the session as live", timeout: 60_000 },
      )
      .toBe(true);

    // ---------------------------------------------------------------------------------------
    // Health reflects a real stream: ingest connected and bytes flowing.
    // ---------------------------------------------------------------------------------------
    await expect
      .poll(
        async () => page.getByText("Stable").isVisible().catch(() => false),
        { message: "connection never reported Stable", timeout: 45_000 },
      )
      .toBe(true);

    // ---------------------------------------------------------------------------------------
    // A viewer can watch it.
    // ---------------------------------------------------------------------------------------
    const viewer = await context.newPage();
    await viewer.goto(`/watch/${sessionId}`);

    await expect(viewer.getByText("LIVE").first()).toBeVisible({ timeout: 30_000 });

    const player = viewer.getByTestId("live-player");
    await expect
      .poll(
        async () => player.evaluate((el: HTMLVideoElement) => el.readyState >= 2 || el.currentTime > 0),
        { message: "viewer playback never started", timeout: 60_000 },
      )
      .toBe(true);

    await viewer.close();

    // ---------------------------------------------------------------------------------------
    // Stop cleanly.
    // ---------------------------------------------------------------------------------------
    await page.getByRole("button", { name: "Stop Live" }).click();
    await expect(page.getByText("Ended")).toBeVisible({ timeout: 45_000 });

    // ---------------------------------------------------------------------------------------
    // Recording metadata is finalized.
    // ---------------------------------------------------------------------------------------
    await expect
      .poll(
        async () => {
          const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
          const response = await request.get(
            `${API_URL}/api/v1/live-sessions/${sessionId}/recordings`,
            { headers: { Authorization: `Bearer ${token}` } },
          );

          if (!response.ok()) return null;
          const recordings = (await response.json()) as { status: string; sizeBytes: number | null }[];
          return recordings[0] ?? null;
        },
        { message: "recording never reached a terminal state", timeout: 45_000 },
      )
      .toMatchObject({ status: "READY" });
  });

  test("a dropped broadcaster recovers instead of ending the session", async ({ page, request }) => {
    // The blueprint's most important reliability rule (§11.3), proven against the real stack:
    // losing ingest must never silently finish someone's broadcast.
    const { email } = await register(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("E2E reconnect");
    await page.getByRole("button", { name: /create and open studio/i }).click();
    await expect(page).toHaveURL(/\/studio\//);

    const sessionId = page.url().split("/studio/")[1]!;

    await page.getByRole("button", { name: /use camera and microphone/i }).click();
    await page.getByRole("button", { name: "Start Live" }).click();
    await expect(page.getByText("Live", { exact: true }).first()).toBeVisible({ timeout: 60_000 });

    // Find the gateway path for this session, then sever its publisher.
    const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
    const statusResponse = await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/status`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(statusResponse.ok()).toBeTruthy();

    const playback = await (
      await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/playback`)
    ).json();
    const mediaPath = String(playback.hlsUrl).split("/").at(-2)!;

    expect(kickPublisher(mediaPath), "no active publisher found to interrupt").toBe(true);

    // The server must move the session to a recovering state, holding it open.
    await expect
      .poll(
        async () => {
          const response = await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/status`, {
            headers: { Authorization: `Bearer ${token}` },
          });
          return response.ok() ? ((await response.json()) as { status: string }).status : null;
        },
        { message: "session never entered a recovering state", timeout: 45_000 },
      )
      .toBe("RECONNECTING");

    // Critically: it must not have ended.
    await expect(page.getByText("Ended")).toBeHidden();

    // The publisher retries on its own, and the session returns to LIVE inside the recovery window.
    await expect
      .poll(
        async () => {
          const response = await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/status`, {
            headers: { Authorization: `Bearer ${token}` },
          });
          return response.ok() ? ((await response.json()) as { status: string }).status : null;
        },
        { message: "session never recovered to LIVE", timeout: 90_000 },
      )
      .toBe("LIVE");
  });
});
