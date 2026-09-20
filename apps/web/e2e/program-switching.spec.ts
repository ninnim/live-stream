import type { APIRequestContext, Browser, Page } from "@playwright/test";
import { expect, test } from "@playwright/test";
import { API_URL, createBroadcaster, PASSWORD, mediaControlApi, mediaPathOf, statusOf } from "./support/journey";

/**
 * Program switching, driven through two real browsers against a live stack.
 *
 * The claim: cutting between sources changes what the audience sees without restarting the
 * broadcast. The studio composes the program in a canvas and publishes that, so a cut redraws the
 * frame rather than re-forming the connection.
 *
 * The proof is the media gateway's own WebRTC session id for the session path. It is recorded
 * before the cut and again after it: the same id means the transport survived, and the audience saw
 * a cut rather than a stall.
 */

interface WebRtcSession {
  id: string;
  path: string;
  state: string;
}

function publisherSessionId(mediaPath: string): string | null {
  const listing = mediaControlApi("/v3/webrtcsessions/list") as { items?: WebRtcSession[] } | null;
  return listing?.items?.find((s) => s.path === mediaPath && s.state === "publish")?.id ?? null;
}

/** `getByText` matches substrings, which would let a button's own label satisfy an assertion. */
function exactly(page: Page, text: string) {
  return page.getByText(text, { exact: true });
}

async function openStudio(page: Page, request: APIRequestContext, title: string): Promise<string> {
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

  return page.url().split("/studio/")[1]!;
}

async function invite(page: Page, name: string): Promise<string> {
  await page.getByRole("button", { name: "Add device" }).click();
  await page.getByLabel("Role").selectOption("Camera");
  await page.getByLabel("Device name").fill(name);
  await page.getByRole("button", { name: "Create pairing code" }).click();

  const code = page.locator("p.font-mono").first();
  await expect(code).toBeVisible({ timeout: 30_000 });
  return (await code.innerText()).trim();
}

async function newDevice(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ permissions: ["camera", "microphone"] });
  return context.newPage();
}

/** The monitor tile for one source, inside the switcher. */
function monitor(page: Page, name: string) {
  return page.getByLabel(`${name} monitor`).locator("xpath=ancestor::div[1]/parent::div");
}

/** A session that is on air. DEGRADED counts: the synthetic camera is below the health threshold. */
const BROADCASTING = ["LIVE", "DEGRADED"];

/**
 * Takes the studio live and waits for the server to say so.
 *
 * "Stop Live" appears as soon as the session is STARTING, which is before the health monitor has
 * confirmed ingest. Asserting on the button alone lets a test run ahead of the session it is
 * testing, and then read STARTING at the moment it expects LIVE.
 */
async function goLive(page: Page, request: APIRequestContext, sessionId: string): Promise<string> {
  await page.getByRole("button", { name: /use camera and microphone/i }).click();
  await page.getByRole("button", { name: "Start Live" }).click();
  await expect(page.getByRole("button", { name: "Stop Live" })).toBeVisible({ timeout: 60_000 });

  const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
  expect(token).toBeTruthy();

  await expect
    .poll(async () => (await statusOf(request, sessionId, token!)).status, { timeout: 60_000 })
    .toMatch(/LIVE|DEGRADED/);

  return token!;
}

/** Brings a phone into the session and gets it sending. */
async function joinAndSend(browser: Browser, code: string, sessionTitle: string): Promise<Page> {
  const phone = await newDevice(browser);

  await phone.goto(`/join/${code.replace("-", "")}`);
  await expect(phone.getByText(sessionTitle)).toBeVisible({ timeout: 60_000 });

  await phone.getByRole("button", { name: /enable camera and microphone/i }).click();
  await phone.getByRole("button", { name: /start sending video/i }).click();
  await expect(exactly(phone, "Sending")).toBeVisible({ timeout: 60_000 });

  return phone;
}

test.describe.configure({ mode: "serial" });

test.describe("program switching", () => {
  test("cutting to a phone keeps the same broadcast on air", async ({ page, browser, request }) => {
    const sessionId = await openStudio(page, request, "Two camera show");
    const token = await goLive(page, request, sessionId);

    const mediaPath = await mediaPathOf(request, sessionId);
    await expect.poll(() => publisherSessionId(mediaPath), { timeout: 30_000 }).not.toBeNull();

    const code = await invite(page, "Stage camera");
    const phone = await joinAndSend(browser, code, "Two camera show");

    try {
      // The switcher only appears once there is something to switch between.
      await expect(page.getByRole("heading", { name: "Program" })).toBeVisible({ timeout: 90_000 });

      const before = publisherSessionId(mediaPath);
      expect(before).not.toBeNull();

      // Cut. The studio starts composing, so the outgoing track changes once — but the transport
      // carrying it must not.
      const phoneTile = monitor(page, "Stage camera");
      await phoneTile.getByRole("button", { name: "Cut" }).click();

      await expect(phoneTile.getByText("On air", { exact: true })).toBeVisible({ timeout: 30_000 });

      // The server records the decision...
      await expect
        .poll(async () => (await sourcesOf(request, sessionId, token)).find((s) => s.isProgram)?.role, {
          timeout: 30_000,
        })
        .toBe("Camera");

      // ... the session never left the air ...
      expect(BROADCASTING).toContain((await statusOf(request, sessionId, token)).status);

      // ... and it is the same connection throughout. A different id would mean the studio tore
      // down and republished, which every viewer would have seen.
      expect(publisherSessionId(mediaPath)).toBe(before);
    } finally {
      await phone.context().close();
    }
  });

  test("cutting back to the studio returns the program without a restart", async ({
    page,
    browser,
    request,
  }) => {
    const sessionId = await openStudio(page, request, "Return leg");
    const token = await goLive(page, request, sessionId);

    const mediaPath = await mediaPathOf(request, sessionId);
    await expect.poll(() => publisherSessionId(mediaPath), { timeout: 30_000 }).not.toBeNull();

    const code = await invite(page, "Roving camera");
    const phone = await joinAndSend(browser, code, "Return leg");

    try {
      await expect(page.getByRole("heading", { name: "Program" })).toBeVisible({ timeout: 90_000 });

      const before = publisherSessionId(mediaPath);

      await monitor(page, "Roving camera").getByRole("button", { name: "Cut" }).click();
      await expect(monitor(page, "Roving camera").getByText("On air", { exact: true })).toBeVisible({
        timeout: 30_000,
      });

      await monitor(page, "Studio").getByRole("button", { name: "Cut" }).click();
      await expect(monitor(page, "Studio").getByText("On air", { exact: true })).toBeVisible({
        timeout: 30_000,
      });

      await expect
        .poll(async () => (await sourcesOf(request, sessionId, token)).find((s) => s.isProgram)?.role, {
          timeout: 30_000,
        })
        .toBe("Host");

      expect(BROADCASTING).toContain((await statusOf(request, sessionId, token)).status);
      expect(publisherSessionId(mediaPath)).toBe(before);
    } finally {
      await phone.context().close();
    }
  });

  test("a multi-source layout keeps one broadcast on air", async ({ page, browser, request }) => {
    const sessionId = await openStudio(page, request, "Side by side");
    const token = await goLive(page, request, sessionId);

    const mediaPath = await mediaPathOf(request, sessionId);
    await expect.poll(() => publisherSessionId(mediaPath), { timeout: 30_000 }).not.toBeNull();

    const code = await invite(page, "Guest camera");
    const phone = await joinAndSend(browser, code, "Side by side");

    try {
      await expect(page.getByRole("heading", { name: "Program" })).toBeVisible({ timeout: 90_000 });

      /*
       * Drop the camera to 540p first.
       *
       * The compositor always outputs 720p, so once the camera and the canvas disagree on size, the
       * preview's own dimensions say which of the two is actually being published. Without this the
       * two are both 1280×720 and the assertion below would pass either way.
       */
      await page.getByRole("combobox", { name: "Quality" }).selectOption("540p");
      await expect
        .poll(() => page.getByTestId("camera-preview").evaluate((el: HTMLVideoElement) => el.videoWidth), {
          timeout: 30_000,
        })
        .toBe(960);

      const before = publisherSessionId(mediaPath);

      await page.getByRole("button", { name: "Side by side" }).click();

      // Both sources on air at once means the canvas is doing real work, not passing one through.
      await expect(page.getByText("Composing in the studio.", { exact: false })).toBeVisible({
        timeout: 30_000,
      });

      // The preview is now the composed frame, not the camera — so the canvas is genuinely in the
      // pipeline rather than the studio quietly still sending its own picture.
      await expect
        .poll(() => page.getByTestId("camera-preview").evaluate((el: HTMLVideoElement) => el.videoWidth), {
          timeout: 30_000,
        })
        .toBe(1280);

      expect(BROADCASTING).toContain((await statusOf(request, sessionId, token)).status);
      expect(publisherSessionId(mediaPath)).toBe(before);

      // Exactly one publisher: composing must never mean two encoders on one path.
      const listing = mediaControlApi("/v3/webrtcsessions/list") as { items?: WebRtcSession[] } | null;
      const publishers = (listing?.items ?? []).filter((s) => s.path === mediaPath && s.state === "publish");
      expect(publishers).toHaveLength(1);
    } finally {
      await phone.context().close();
    }
  });
});

interface SourceRow {
  id: string;
  role: string;
  isProgram: boolean;
  status: string;
}

async function sourcesOf(
  request: APIRequestContext,
  sessionId: string,
  token: string,
): Promise<SourceRow[]> {
  const response = await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/sources`, {
    headers: { Authorization: `Bearer ${token}` },
  });

  expect(response.ok(), `source read failed: ${await response.text()}`).toBeTruthy();
  return (await response.json()) as SourceRow[];
}
