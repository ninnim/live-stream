import type { APIRequestContext, Browser, Page } from "@playwright/test";
import { expect, test } from "@playwright/test";
import { API_URL, createBroadcaster, PASSWORD, statusOf } from "./support/journey";

/**
 * Multi-device contribution, driven through two real browsers against a live stack.
 *
 * One context is the control room; the other is a phone that scans a pairing code and starts
 * sending video. Nothing is mocked: real pairing, real device tokens, real WebRTC media into a real
 * gateway, and a real revocation.
 */

/**
 * Text assertions here are exact.
 *
 * `getByText` matches case-insensitive substrings by default, so "Sending" would also match the
 * phone's own "Start sending video" button — and a test that passes on the button it just clicked
 * proves nothing.
 */
function exactly(page: Page, text: string) {
  return page.getByText(text, { exact: true });
}

/** Signs in and opens a fresh studio. */
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

/** Invites a device from the control room and reads the code off the screen, as an operator would. */
async function invite(page: Page, role: string, name: string): Promise<string> {
  await page.getByRole("button", { name: "Add device" }).click();
  await page.getByLabel("Role").selectOption(role);
  await page.getByLabel("Device name").fill(name);
  await page.getByRole("button", { name: "Create pairing code" }).click();

  const code = page.locator("p.font-mono").first();
  await expect(code).toBeVisible({ timeout: 30_000 });

  return (await code.innerText()).trim();
}

function sourceRow(page: Page, name: string) {
  return page.getByRole("listitem").filter({ hasText: name });
}

function sourceBadge(page: Page, name: string, status: string) {
  return sourceRow(page, name).getByText(status, { exact: true });
}

/**
 * Takes the studio live.
 *
 * Contributing devices need the session to be holding an ingest path open before they can publish,
 * which is the realistic order anyway: the producer opens the show, then cameras join it.
 */
async function goLive(page: Page): Promise<void> {
  await page.getByRole("button", { name: /use camera and microphone/i }).click();
  await page.getByRole("button", { name: "Start Live" }).click();
  await expect(page.getByRole("button", { name: "Stop Live" })).toBeVisible({ timeout: 60_000 });
}

test.describe.configure({ mode: "serial" });

test.describe("multi-device contribution", () => {
  test("a phone joins with a pairing code and appears in the control room", async ({ page, browser, request }) => {
    const sessionId = await openStudio(page, request, "Multi-camera show");
    await goLive(page);

    const code = await invite(page, "Camera", "Phone camera");

    expect(code).toMatch(/^[A-Z2-9]{4}-[A-Z2-9]{4}$/);

    // A second browser context stands in for the phone: its own storage, its own permissions, and
    // no knowledge of the operator's account.
    const phone = await newDevice(browser);

    try {
      await phone.goto(`/join/${code.replace("-", "")}`);

      // Scanning a code joins without a further tap, so the studio name appears on its own.
      await expect(phone.getByText("Multi-camera show")).toBeVisible({ timeout: 60_000 });
      await expect(exactly(phone, "Standing by")).toBeVisible();

      // The control room sees it join, without the phone telling it anything.
      await expect(sourceBadge(page, "Phone camera", "Joined")).toBeVisible({ timeout: 60_000 });

      // The phone starts sending.
      await phone.getByRole("button", { name: /enable camera and microphone/i }).click();
      await phone.getByRole("button", { name: /start sending video/i }).click();

      await expect(exactly(phone, "Sending")).toBeVisible({ timeout: 60_000 });

      // ... and presence is observed server-side, not declared by the device.
      await expect(sourceBadge(page, "Phone camera", "Sending")).toBeVisible({ timeout: 90_000 });

      // A device joining does not disturb the broadcast it joined.
      const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
      expect(["LIVE", "DEGRADED"]).toContain((await statusOf(request, sessionId, token!)).status);
    } finally {
      await phone.context().close();
    }
  });

  /**
   * The security property Phase 4 turns on: docs/05-multi-device.md, "Revocation is immediate."
   */
  test("removing a device cuts it off at once", async ({ page, browser, request }) => {
    await openStudio(page, request, "Revocation");
    const code = await invite(page, "Camera", "Borrowed phone");

    const phone = await newDevice(browser);

    try {
      await phone.goto(`/join/${code.replace("-", "")}`);
      await expect(exactly(phone, "Standing by")).toBeVisible({ timeout: 60_000 });

      const deviceToken = await phone.evaluate(() => {
        const key = Object.keys(window.localStorage).find((k) => k.startsWith("livestream.deviceToken."));
        return key ? window.localStorage.getItem(key) : null;
      });
      expect(deviceToken).toBeTruthy();

      // The token works right up until the moment it is revoked.
      const before = await request.get(`${API_URL}/api/v1/device/session`, {
        headers: { Authorization: `Device ${deviceToken}` },
      });
      expect(before.ok()).toBeTruthy();

      await sourceRow(page, "Borrowed phone").getByRole("button", { name: "Remove" }).click();
      await expect(sourceBadge(page, "Borrowed phone", "Removed")).toBeVisible({ timeout: 30_000 });

      // The very next request fails — not when the token would have expired.
      const after = await request.get(`${API_URL}/api/v1/device/session`, {
        headers: { Authorization: `Device ${deviceToken}` },
      });
      expect(after.status()).toBe(401);
    } finally {
      await phone.context().close();
    }
  });

  test("a pairing code cannot be used twice", async ({ page, browser, request }) => {
    await openStudio(page, request, "Single use");
    const code = await invite(page, "Camera", "First phone");

    const first = await newDevice(browser);
    const second = await newDevice(browser);

    try {
      await first.goto(`/join/${code.replace("-", "")}`);
      await expect(first.getByText("Standing by")).toBeVisible({ timeout: 60_000 });

      // A second device with the same code is refused, and told what to do about it.
      await second.goto(`/join/${code.replace("-", "")}`);
      await expect(second.getByText(/pairing code is not valid/i)).toBeVisible({ timeout: 60_000 });
    } finally {
      await first.context().close();
      await second.context().close();
    }
  });
});

/**
 * A browser context standing in for a phone.
 *
 * Its own context rather than a tab, so it has separate storage and permissions — a device joining
 * a session must work with no trace of the operator's session in the browser.
 */
async function newDevice(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ permissions: ["camera", "microphone"] });
  return context.newPage();
}
