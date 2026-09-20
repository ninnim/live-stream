import { expect, test } from "@playwright/test";
import { createBroadcaster, goLive, statusOf } from "./support/journey";

/**
 * Studio resilience against the things that actually happen to a broadcaster mid-stream:
 * reloading the control room, opening it twice, and stopping from a recovering state.
 *
 * Acceptance criterion 10 of implementation/phase-1-native-web-broadcasting.md lives here.
 */
test.describe("studio resilience", () => {
  test("reloading the control room does not corrupt session state", async ({ page, request }) => {
    const { sessionId, token } = await goLive(page, request, "E2E reload");

    // Reloading tears down the page — and with it the peer connection publishing the stream.
    await page.reload();

    // The studio must come back showing the server's state, not a fresh session.
    await expect(page.getByRole("button", { name: "Start Live" })).toBeHidden();

    // The session itself must still be the same session, still owned by this user, and still
    // in a broadcasting state rather than reset or failed.
    const status = await statusOf(request, sessionId, token);
    expect(["LIVE", "DEGRADED", "RECONNECTING"]).toContain(status.status);
    expect(status.id).toBe(sessionId);

    // And it must still be stoppable from the reloaded page.
    await page.getByRole("button", { name: "Stop Live" }).click();

    await expect
      .poll(async () => (await statusOf(request, sessionId, token)).status, {
        message: "session did not end after stopping from a reloaded studio",
        timeout: 45_000,
      })
      .toBe("ENDED");
  });

  test("a reloaded studio resumes publishing without a new session", async ({ page, request }) => {
    const { sessionId, token } = await goLive(page, request, "E2E resume");

    const startedAt = (await statusOf(request, sessionId, token)).startedAt;
    expect(startedAt).toBeTruthy();

    await page.reload();

    // Wait for the server to actually observe that ingest stopped. Without this the assertion
    // below would pass against state left over from before the reload rather than proving anything.
    await expect
      .poll(async () => (await statusOf(request, sessionId, token)).status, {
        message: "server never noticed the publisher had gone",
        timeout: 45_000,
      })
      .toBe("RECONNECTING");

    // Now the real assertion: the studio republishes into the same session, so a reload does not
    // bleed out the recovery window and fail the broadcast.
    await expect
      .poll(async () => (await statusOf(request, sessionId, token)).status, {
        message: "session never returned to LIVE after a studio reload",
        timeout: 90_000,
      })
      .toBe("LIVE");

    // Same session, same start time: the duration must not restart.
    const resumed = await statusOf(request, sessionId, token);
    expect(resumed.startedAt).toBe(startedAt);
  });

  test("a session can be stopped while it is reconnecting", async ({ page, request }) => {
    const { sessionId, token } = await goLive(page, request, "E2E stop while reconnecting");

    // Take the studio offline so it stops republishing, then let ingest lapse.
    await page.close();

    await expect
      .poll(async () => (await statusOf(request, sessionId, token)).status, {
        message: "session never entered a recovering state",
        timeout: 60_000,
      })
      .toBe("RECONNECTING");

    const response = await request.post(`${process.env.E2E_API_URL ?? "http://localhost:8080"}/api/v1/live-sessions/${sessionId}/stop`, {
      headers: { Authorization: `Bearer ${token}` },
    });

    expect(response.ok()).toBeTruthy();
    expect((await response.json()).status).toBe("ENDED");
  });

  test("two concurrent broadcasts stay independent", async ({ browser, request }) => {
    // One tenant's stream must not affect another's — the isolation the API enforces has to hold
    // when both are genuinely publishing at once.
    const first = await browser.newContext();
    const second = await browser.newContext();

    try {
      const firstPage = await first.newPage();
      const secondPage = await second.newPage();

      const a = await goLive(firstPage, request, "E2E concurrent A");
      const b = await goLive(secondPage, request, "E2E concurrent B");

      expect(a.sessionId).not.toBe(b.sessionId);

      // Ending one must leave the other broadcasting.
      await firstPage.getByRole("button", { name: "Stop Live" }).click();

      await expect
        .poll(async () => (await statusOf(request, a.sessionId, a.token)).status, {
          message: "first session did not end",
          timeout: 45_000,
        })
        .toBe("ENDED");

      const other = await statusOf(request, b.sessionId, b.token);
      expect(["LIVE", "DEGRADED"]).toContain(other.status);
    } finally {
      await first.close();
      await second.close();
    }
  });

  test("a broadcaster cannot reach another workspace's session", async ({ page, request }) => {
    const { sessionId } = await goLive(page, request, "E2E isolation");
    const stranger = await createBroadcaster(request);

    const apiUrl = process.env.E2E_API_URL ?? "http://localhost:8080";
    for (const path of ["", "/status", "/health", "/recordings"]) {
      const response = await request.get(`${apiUrl}/api/v1/live-sessions/${sessionId}${path}`, {
        headers: { Authorization: `Bearer ${stranger.accessToken}` },
      });

      expect(response.status(), `GET ${path} should be forbidden for a stranger`).toBe(403);
    }

    const stop = await request.post(`${apiUrl}/api/v1/live-sessions/${sessionId}/stop`, {
      headers: { Authorization: `Bearer ${stranger.accessToken}` },
    });
    expect(stop.status()).toBe(403);
  });
});
