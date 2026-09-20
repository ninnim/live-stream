import { expect, test } from "@playwright/test";
import { createBroadcaster, PASSWORD } from "./support/journey";

/**
 * Recording to a file, in a real browser.
 *
 * The unit tests prove the orchestration against a fake `MediaRecorder`. What they cannot prove is
 * that a real one, handed the studio's real tracks in the container this studio chose, produces a
 * file with bytes in it — which is the only thing the operator cares about.
 *
 * These take the **in-memory path** — the one a browser without the File System Access API uses —
 * because the save dialog cannot be driven by a test. Headless Chromium rejects
 * `showSaveFilePicker` with `AbortError`, which is exactly what a person pressing Cancel produces
 * and is correctly treated as "they changed their mind", so the API is removed rather than left to
 * silently decline. Everything after choosing where to save is the real thing.
 */

/** A browser with no file picker, which is most of them. */
async function withoutAFilePicker(page: import("@playwright/test").Page): Promise<void> {
  await page.addInitScript(() => {
    delete (globalThis as Record<string, unknown>).showSaveFilePicker;
  });
}

test.describe("recording to this computer", () => {
  test("writes a real file without ever going live", async ({ page, request }) => {
    await withoutAFilePicker(page);
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("Screen recording");
    await page.getByRole("button", { name: /create and open studio/i }).click();
    await expect(page).toHaveURL(/\/studio\//);

    await page.getByRole("button", { name: /use camera and microphone/i }).click();

    const start = page.getByRole("button", { name: /start recording/i });
    await expect(start).toBeEnabled();

    const downloadPromise = page.waitForEvent("download", { timeout: 30_000 });
    await start.click();

    // Running, and saying how much it has actually written — a timer alone would keep counting
    // over a recorder that had died.
    await expect(page.getByRole("button", { name: /stop recording/i })).toBeVisible();
    await expect(page.getByText(/holds the recording in memory/i)).toBeVisible();

    // Long enough for several chunks at the recorder's one-second cadence.
    await page.waitForTimeout(4000);
    await page.getByRole("button", { name: /stop recording/i }).click();

    const download = await downloadPromise;
    const path = await download.path();
    expect(path).toBeTruthy();

    const { statSync } = await import("node:fs");
    const size = statSync(path!).size;

    // The assertion that matters. A container the browser accepted but could not write would
    // produce a zero-byte file and look identical from the interface.
    expect(size, "the recording must contain actual media").toBeGreaterThan(10_000);

    // Named so it can be found again.
    expect(download.suggestedFilename()).toMatch(/^screen-recording-\d{4}-\d{2}-\d{2}-\d{4}\.(mp4|webm)$/);

    // And none of it was a broadcast: the session never left DRAFT.
    await expect(page.getByRole("button", { name: "Start Live" })).toBeEnabled();
  });

  test("keeps the recording when the screen share it was recording ends", async ({ page, request }) => {
    // The one source change that cannot be hidden from a recording. Everything written up to that
    // point is the operator's, and losing it silently would be the worst outcome in this feature.
    await withoutAFilePicker(page);
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("Interrupted");
    await page.getByRole("button", { name: /create and open studio/i }).click();

    await page.getByRole("button", { name: /use camera and microphone/i }).click();

    const downloadPromise = page.waitForEvent("download", { timeout: 30_000 });
    await page.getByRole("button", { name: /start recording/i }).click();
    await expect(page.getByRole("button", { name: /stop recording/i })).toBeVisible();

    await page.waitForTimeout(3000);

    // Ends the camera the way unplugging it would, which is what the studio sees when a screen
    // share is stopped from the browser's own sharing bar.
    await page.evaluate(() => {
      const video = document.querySelector("video");
      const stream = video?.srcObject as MediaStream | null;
      stream?.getVideoTracks().forEach((track) => track.stop());
      stream?.getVideoTracks().forEach((track) => track.dispatchEvent(new Event("ended")));
    });

    const download = await downloadPromise;
    const { statSync } = await import("node:fs");
    expect(statSync((await download.path())!).size).toBeGreaterThan(10_000);

    // And it says why it stopped rather than leaving the operator to guess.
    await expect(page.getByText(/screen share or camera ended/i)).toBeVisible();
  });
});
