import type { APIRequestContext, Page } from "@playwright/test";
import { expect, test } from "@playwright/test";
import { PASSWORD, createBroadcaster, mediaControlApi, mediaPathOf, statusOf } from "./support/journey";

/**
 * The production tools: scenes, captions, branding and keyboard control, against a live stack.
 *
 * These are the parts of Phase 5 that are not about media transport, so what matters is that they
 * persist, that recalling one actually rearranges the studio, and — the thing worth proving with a
 * real browser — that none of it interrupts the broadcast.
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

const BROADCASTING = ["LIVE", "DEGRADED"];

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

async function grantMedia(page: Page): Promise<void> {
  await page.getByRole("button", { name: /use camera and microphone/i }).click();
  await expect(page.getByRole("heading", { name: "Scenes" })).toBeVisible({ timeout: 30_000 });
}

test.describe.configure({ mode: "serial" });

test.describe("studio production tools", () => {
  test("a scene is saved, survives a reload, and rearranges the studio when recalled", async ({
    page,
    request,
  }) => {
    await openStudio(page, request, "Scene recall");
    await grantMedia(page);

    // Set up a shot, then name it.
    await page.getByRole("button", { name: "Save current shot" }).click();
    await page.getByLabel("Scene name").fill("Opening");
    await page.getByLabel("Layout").selectOption("PictureInPicture");
    await page.getByLabel("Caption", { exact: true }).fill("Ada Lovelace");
    await page.getByRole("button", { name: "Save scene" }).click();

    await expect(page.getByText("Opening", { exact: true })).toBeVisible({ timeout: 30_000 });

    // Scenes are configuration, so they have to outlive the page that made them.
    await page.reload();
    await grantMedia(page);
    await expect(page.getByText("Opening", { exact: true })).toBeVisible({ timeout: 30_000 });

    // Recalling arranges the studio: the caption text comes back and the layout changes with it.
    await page.getByRole("button", { name: "Recall" }).click();

    await expect(page.getByLabel("Lower third title")).toHaveValue("Ada Lovelace");
    await expect(page.getByRole("button", { name: "Hide caption" })).toBeVisible();
  });

  test("branding is saved against the session", async ({ page, request }) => {
    await openStudio(page, request, "Branding");
    await grantMedia(page);

    // The colour input is the one piece of branding that needs no file to exercise. It commits on
    // blur rather than on every step of a drag, so the save is awaited explicitly instead of raced.
    const saved = page.waitForResponse(
      (response) => response.url().includes("/branding") && response.request().method() === "PUT",
    );

    await page.getByLabel("Accent colour").fill("#ff8800");
    await page.getByRole("heading", { name: "Overlays" }).click();
    expect((await saved).ok()).toBe(true);

    await page.reload();
    await grantMedia(page);

    await expect(page.getByLabel("Accent colour")).toHaveValue("#ff8800");
  });

  test("keyboard shortcuts drive the switcher without touching the broadcast", async ({
    page,
    request,
  }) => {
    const sessionId = await openStudio(page, request, "Hotkeys");
    await grantMedia(page);

    await page.getByRole("button", { name: "Start Live" }).click();
    await expect(page.getByRole("button", { name: "Stop Live" })).toBeVisible({ timeout: 60_000 });

    const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
    await expect
      .poll(async () => (await statusOf(request, sessionId, token!)).status, { timeout: 60_000 })
      .toMatch(/LIVE|DEGRADED/);

    const mediaPath = await mediaPathOf(request, sessionId);
    const before = publisherSessionId(mediaPath);
    expect(before).not.toBeNull();

    // Type a caption, then move focus out of the field so the keyboard belongs to the switcher.
    await page.getByLabel("Lower third title").fill("Ada Lovelace");
    await page.getByRole("heading", { name: "Overlays" }).click();

    // L puts the caption up, which is enough on its own to start composing.
    await page.keyboard.press("l");
    await expect(page.getByRole("button", { name: "Hide caption" })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText("Captions are drawn into the program.")).toBeVisible({
      timeout: 30_000,
    });

    // M mutes the microphone. The studio reads it back from the track, not from its own guess.
    await page.keyboard.press("m");
    await expect(page.getByRole("button", { name: /microphone/i, pressed: false })).toBeVisible();

    // L again takes it down, which returns the studio to sending the camera directly.
    await page.keyboard.press("l");
    await expect(page.getByRole("button", { name: "Show caption" })).toBeVisible({ timeout: 30_000 });

    // Composing started and stopped, and the broadcast noticed neither.
    expect(BROADCASTING).toContain((await statusOf(request, sessionId, token!)).status);
    expect(publisherSessionId(mediaPath)).toBe(before);
  });

  test("a keystroke typed into a field never reaches the switcher", async ({ page, request }) => {
    // A producer naming a scene "1" must get the character, not a cut.
    await openStudio(page, request, "Typing");
    await grantMedia(page);

    await page.getByRole("button", { name: "Save current shot" }).click();

    const name = page.getByLabel("Scene name");
    await name.fill("");
    await name.type("1 Camera q w");

    await expect(name).toHaveValue("1 Camera q w");
  });
});
