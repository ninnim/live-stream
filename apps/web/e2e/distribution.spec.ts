import type { APIRequestContext, Page } from "@playwright/test";
import { expect, test } from "@playwright/test";
import { API_URL, createBroadcaster, PASSWORD, statusOf } from "./support/journey";

/**
 * Multi-platform distribution, driven through the real studio against a running stack.
 *
 * The "external platform" is a disposable RTMP receiver started by
 * `infrastructure/docker/docker-compose.e2e.yml`. It is a real RTMP server, so the relay, the
 * encoder, and the handshake are all exercised for real — only the company on the other end is
 * fictional.
 */

/** Hostname the relay publishes to. Inside the compose network this resolves to the receiver. */
const PLATFORM_HOST = process.env.E2E_FAKE_PLATFORM ?? "fake-platform";

/** Its control API, as reachable from wherever the tests run. */
const PLATFORM_API = process.env.E2E_FAKE_PLATFORM_API ?? "http://localhost:9998";

interface RtmpConnection {
  id: string;
  path: string;
  state: string;
}

/** Connections the platform currently has, which is how a test sees what actually arrived. */
async function platformConnections(request: APIRequestContext): Promise<RtmpConnection[]> {
  try {
    const response = await request.get(`${PLATFORM_API}/v3/rtmpconns/list`, { timeout: 5000 });
    if (!response.ok()) return [];

    return ((await response.json()) as { items?: RtmpConnection[] }).items ?? [];
  } catch {
    return [];
  }
}

async function platformIsReceiving(request: APIRequestContext, streamKey: string): Promise<boolean> {
  const connections = await platformConnections(request);
  return connections.some((c) => c.path === `live/${streamKey}` && c.state === "publish");
}

/**
 * Severs the relay's connection at the platform.
 *
 * Closer to what a platform outage actually looks like than stopping the container: the service
 * stays up, the connection does not. It also keeps this spec free of any dependency on the Docker
 * CLI being available wherever the tests happen to run.
 */
async function severPlatformConnection(request: APIRequestContext, streamKey: string): Promise<boolean> {
  const connections = await platformConnections(request);
  const publisher = connections.find((c) => c.path === `live/${streamKey}` && c.state === "publish");

  if (!publisher) return false;

  await request.post(`${PLATFORM_API}/v3/rtmpconns/kick/${publisher.id}`, { timeout: 5000 });
  return true;
}

/** Signs in, creates a session, adds a destination through the UI, then goes live. */
async function goLiveWithDestination(
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
  await page.getByRole("button", { name: "Start Live" }).click();

  const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
  expect(token).toBeTruthy();

  return { sessionId, token: token! };
}

function destinationRow(page: Page) {
  return page.getByRole("listitem").filter({ hasText: "Test platform" });
}

/**
 * The status badge for the destination.
 *
 * Matched exactly: the row also carries a countdown line ("Reconnecting in 5s…"), so a substring
 * match would resolve to two elements and fail on strictness rather than on behaviour.
 */
function destinationBadge(page: Page, status: string) {
  return destinationRow(page).getByText(status, { exact: true });
}

/**
 * States in which the broadcast is healthy from the creator's point of view.
 *
 * DEGRADED belongs here: Chromium's synthetic camera produces a very low bitrate, so a session
 * carrying it legitimately reports reduced quality. That is Phase 1 quality detection working on a
 * deliberately tiny stream, and asserting strictly on LIVE would make these specs fail for a reason
 * that has nothing to do with distribution.
 *
 * What must never appear here is RECONNECTING, FAILED, or ENDED — those would mean a destination
 * had disturbed the broadcast.
 */
const BROADCASTING = ["LIVE", "DEGRADED"];

async function expectStillBroadcasting(request: APIRequestContext, sessionId: string, token: string) {
  const status = (await statusOf(request, sessionId, token)).status;
  expect(BROADCASTING, `session moved to ${status}`).toContain(status);
}

test.describe.configure({ mode: "serial" });

test.describe("multi-platform distribution", () => {
  test("a broadcast reaches an external platform and the studio shows it live", async ({ page, request }) => {
    const streamKey = `e2e-${Date.now()}`;
    const { sessionId, token } = await goLiveWithDestination(page, request, "Distribution journey", streamKey);

    // The session goes live first; the destination follows once the relay connects.
    await expect
      .poll(async () => BROADCASTING.includes((await statusOf(request, sessionId, token)).status), {
        timeout: 60_000,
      })
      .toBeTruthy();

    await expect(destinationBadge(page, "Live")).toBeVisible({ timeout: 90_000 });

    // The proof that real media arrived: the platform reports a publisher on our exact path.
    await expect
      .poll(() => platformIsReceiving(request, streamKey), { timeout: 60_000 })
      .toBeTruthy();

    await page.getByRole("button", { name: "Stop Live" }).click();

    await expect
      .poll(async () => (await statusOf(request, sessionId, token)).status, { timeout: 60_000 })
      .toBe("ENDED");

    // Stopping the session must take its destinations down with it.
    await expect.poll(() => platformIsReceiving(request, streamKey), { timeout: 30_000 }).toBeFalsy();
  });

  /**
   * The Phase 2 acceptance criterion, driven through the UI:
   * "Core stream can remain LIVE if one destination fails."
   */
  test("the session stays live when the platform drops, and the destination recovers", async ({ page, request }) => {
    const streamKey = `e2e-outage-${Date.now()}`;
    const { sessionId, token } = await goLiveWithDestination(page, request, "Destination outage", streamKey);

    await expect(destinationBadge(page, "Live")).toBeVisible({ timeout: 90_000 });
    await expect.poll(() => platformIsReceiving(request, streamKey), { timeout: 60_000 }).toBeTruthy();

    expect(await severPlatformConnection(request, streamKey)).toBeTruthy();

    // The destination notices and starts reconnecting…
    await expect(destinationBadge(page, "Reconnecting")).toBeVisible({ timeout: 90_000 });

    // …while the session is entirely unaffected. This is the assertion that matters.
    await expectStillBroadcasting(request, sessionId, token);

    // And it comes back on its own, without the broadcaster doing anything.
    await expect(destinationBadge(page, "Live")).toBeVisible({ timeout: 120_000 });
    await expectStillBroadcasting(request, sessionId, token);
    await expect.poll(() => platformIsReceiving(request, streamKey), { timeout: 60_000 }).toBeTruthy();

    await page.getByRole("button", { name: "Stop Live" }).click();
  });

  test("a stream key never leaves the server", async ({ page, request }) => {
    const streamKey = `e2e-secret-${Date.now()}`;
    const { sessionId, token } = await goLiveWithDestination(page, request, "Secret handling", streamKey);

    const response = await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/destinations`, {
      headers: { Authorization: `Bearer ${token}` },
    });

    expect(response.ok()).toBeTruthy();
    expect(await response.text()).not.toContain(streamKey);

    // Nor does it survive in the page: the studio only ever learns that a key exists.
    expect(await page.content()).not.toContain(streamKey);

    await page.getByRole("button", { name: "Stop Live" }).click();
  });
});
