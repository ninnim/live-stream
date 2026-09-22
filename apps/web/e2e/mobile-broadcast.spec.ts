import { expect, test } from "@playwright/test";
import { API_URL, PASSWORD, createBroadcaster, statusOf } from "./support/journey";

/**
 * The Phase 3 acceptance journey: a creator goes live from a phone.
 *
 * Runs on the `mobile-chrome` project — a Pixel-sized viewport with touch and a coarse pointer, so
 * the studio classifies it as a phone the same way a real one would, and the mobile broadcaster is
 * what gets rendered. Everything below it is real: real capture, real WHIP publish into the real
 * gateway, real session state read back from the real API.
 *
 * Two things from the phase-2 rig apply here and are worth repeating, because both have cost real
 * time before:
 *
 * - Chromium's synthetic camera publishes far below the "poor" bitrate threshold, so a healthy
 *   session legitimately reports **DEGRADED**. Assert on "still broadcasting", never on LIVE.
 * - The studio must be reached as `localhost`; any other hostname is not a secure context and
 *   `getUserMedia` refuses, leaving the go-live control disabled.
 */

test.describe("mobile broadcasting", () => {
  test("a creator starts a live session from a phone, flips the camera, and stops", async ({
    page,
    request,
  }) => {
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("E2E mobile broadcast");
    await page.getByRole("button", { name: /create and open studio/i }).click();
    await expect(page).toHaveURL(/\/studio\//);

    const sessionId = page.url().split("/studio/")[1]!.split("?")[0]!;

    // The phone-shaped broadcaster, chosen by detection rather than by a query parameter: that
    // choice is the thing under test.
    await expect(page.getByTestId("mobile-studio")).toBeVisible();

    await page.getByTestId("mobile-allow").click();
    await expect(page.getByTestId("mobile-preview")).toBeVisible();

    const goLive = page.getByTestId("mobile-go-live");
    await expect(goLive).toBeEnabled();
    await goLive.click();

    // The server decides when this is live, and the timer only appears once it has said so.
    await expect(page.getByTestId("mobile-timer")).toBeVisible({ timeout: 60_000 });

    const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
    expect(token).toBeTruthy();

    const live = await statusOf(request, sessionId, token!);
    expect(live.isBroadcasting).toBeTruthy();

    // ---------------------------------------------------------------------------------------
    // Flipping the camera must not interrupt the broadcast. Chromium's fake device reports a
    // single camera, so the control is only present when the platform offers two — which is why
    // this is conditional rather than an assertion about the button existing.
    // ---------------------------------------------------------------------------------------
    const flip = page.getByTestId("mobile-flip");
    if (await flip.isVisible()) {
      await flip.click();
      await expect(page.getByTestId("mobile-timer")).toBeVisible();

      const afterFlip = await statusOf(request, sessionId, token!);
      expect(afterFlip.isBroadcasting).toBeTruthy();
    }

    // ---------------------------------------------------------------------------------------
    // The settings sheet: quality rungs and the measured connection, one tap from the viewfinder.
    // ---------------------------------------------------------------------------------------
    await page.getByTestId("mobile-details").click();
    await expect(page.getByTestId("mobile-sheet")).toBeVisible();
    await expect(page.getByTestId("mobile-quality-720p")).toHaveAttribute("aria-pressed", "true");
    await page.getByRole("button", { name: "Done" }).click();

    // ---------------------------------------------------------------------------------------
    // Stop, and confirm the server agrees the session is over.
    // ---------------------------------------------------------------------------------------
    await page.getByTestId("mobile-stop").click();

    await expect
      .poll(async () => (await statusOf(request, sessionId, token!)).status, { timeout: 60_000 })
      .toBe("ENDED");
  });

  test("the same session can be taken over from the full studio", async ({ page, request }) => {
    // The two broadcasters are two views of one session, not two products. Proving the link works
    // is proving that somebody who starts on a phone is not stranded there.
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("E2E mobile handover");
    await page.getByRole("button", { name: /create and open studio/i }).click();
    await expect(page.getByTestId("mobile-studio")).toBeVisible();

    await page.getByTestId("mobile-details").click();
    await page.getByRole("link", { name: /full studio/i }).click();

    await expect(page.getByRole("button", { name: /use camera and microphone/i })).toBeVisible();
  });
});

test.describe("mobile broadcasting — API surface", () => {
  test("uses the same short-lived ingest credential as the desktop studio", async ({ request }) => {
    // Phase 3 asks for "secure short-lived ingest credentials" on mobile. They are the same ones,
    // from the same endpoint, because the mobile broadcaster is the same client — so this asserts
    // the property rather than a second implementation of it.
    const { accessToken } = await createBroadcaster(request);

    const created = await request.post(`${API_URL}/api/v1/live-sessions`, {
      headers: { Authorization: `Bearer ${accessToken}` },
      data: { title: "E2E credential check", visibility: "PRIVATE", recordingEnabled: false },
    });
    expect(created.ok(), `create failed: ${await created.text()}`).toBeTruthy();
    const sessionId = ((await created.json()) as { id: string }).id;

    await request.post(`${API_URL}/api/v1/live-sessions/${sessionId}/prepare`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });

    const credential = await request.post(
      `${API_URL}/api/v1/live-sessions/${sessionId}/sources/credentials`,
      { headers: { Authorization: `Bearer ${accessToken}` } },
    );

    expect(credential.ok(), `credential failed: ${await credential.text()}`).toBeTruthy();
    const body = (await credential.json()) as { token: string; expiresInSeconds: number };

    expect(body.token).toBeTruthy();
    // Short-lived means minutes, not a stream key that lives as long as the session.
    expect(body.expiresInSeconds).toBeLessThanOrEqual(3600);
  });
});
