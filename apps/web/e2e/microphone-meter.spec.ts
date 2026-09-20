import { expect, test } from "@playwright/test";
import { createBroadcaster, PASSWORD } from "./support/journey";

/**
 * The microphone meter, against real Web Audio and a real capture track.
 *
 * The unit tests feed the meter a waveform whose level is known, which proves the arithmetic. What
 * they cannot prove is that an `AnalyserNode` attached to a live `getUserMedia` track reads anything
 * at all — a graph that is built but suspended returns a flat zero, and the meter would confidently
 * report silence over a working microphone. That failure only exists in a browser.
 *
 * Chromium's fake device generates a tone, so a working meter has to see it.
 */
test.describe("the microphone meter", () => {
  test("reads a live microphone, and says what it is picking up", async ({ page, request }) => {
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("Microphone check");
    await page.getByRole("button", { name: /create and open studio/i }).click();
    await expect(page).toHaveURL(/\/studio\//);

    await page.getByRole("button", { name: /use camera and microphone/i }).click();

    const meter = page.getByRole("meter", { name: /Your microphone level/i });
    await expect(meter).toBeVisible();

    // The assertion that matters: a tone is playing into the microphone, so the meter must not be
    // reporting silence. Anything else — quiet, good, loud — means the graph is live and reading.
    await expect
      .poll(async () => meter.getAttribute("aria-valuetext"), { timeout: 20_000 })
      .not.toBe("No sound");

    // And it must be a real measurement rather than a pinned bar.
    const reading = Number(await meter.getAttribute("aria-valuenow"));
    expect(reading).toBeGreaterThan(0);

    // Speech is the default, because almost every broadcast is a person talking.
    await expect(page.getByLabel(/Sound type/i)).toHaveValue("voice");
  });

  test("reports a muted microphone as not open rather than as silence", async ({ page, request }) => {
    // A muted track is a deliberate act, not a fault. Telling somebody who has just muted
    // themselves to check their device is not muted would be absurd.
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("Muted check");
    await page.getByRole("button", { name: /create and open studio/i }).click();

    await page.getByRole("button", { name: /use camera and microphone/i }).click();

    const meter = page.getByRole("meter", { name: /Your microphone level/i });
    await expect.poll(async () => meter.getAttribute("aria-valuetext"), { timeout: 20_000 }).not.toBe(
      "No sound",
    );

    await page.getByRole("button", { name: /On\s*Microphone/i }).click();

    await expect(meter).toHaveAttribute("aria-valuetext", "Not open");
    await expect(page.getByText(/not muted on the device itself/i)).toHaveCount(0);
  });
});
