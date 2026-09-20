import { expect, test } from "@playwright/test";
import { createBroadcaster, PASSWORD } from "./support/journey";

/**
 * Hardware detection driving the studio's starting settings.
 *
 * The unit tests cover the policy against invented answers. What only a browser can show is that
 * `MediaCapabilities.encodingInfo` answers at all for `type: "webrtc"`, that the answer reaches the
 * quality picker before anything is captured, and that it never overrules a person.
 *
 * The container this runs in has no GPU, so the interesting assertions are the software ones —
 * which is the case that actually needed protecting.
 */

async function openStudio(page: import("@playwright/test").Page, email: string, title: string) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).toHaveURL(/\/dashboard/);

  await page.getByRole("link", { name: "New live session" }).click();
  await page.getByLabel("Title").fill(title);
  await page.getByRole("button", { name: /create and open studio/i }).click();
  await expect(page).toHaveURL(/\/studio\//);
}

test.describe("what this machine can encode", () => {
  test("the browser answers, and the studio says what it found", async ({ page, request }) => {
    const { email } = await createBroadcaster(request);
    await openStudio(page, email, "Capability");

    // The API exists and answers for real-time sending, which is the premise of all of this.
    const machine = await page.evaluate(async () => {
      const info = await navigator.mediaCapabilities.encodingInfo({
        type: "webrtc",
        video: {
          contentType: "video/H264;codecs=avc1.42E01E",
          width: 1280,
          height: 720,
          bitrate: 2_500_000,
          framerate: 30,
        },
      } as MediaEncodingConfiguration);

      return {
        supported: info.supported,
        powerEfficient: info.powerEfficient,
        cores: navigator.hardwareConcurrency ?? 0,
      };
    });

    expect(machine.supported).toBe(true);

    await page.getByRole("button", { name: /use camera and microphone/i }).click();

    // Whatever it decided, it tells the operator — the settings visibly moved and a silent change
    // would read as the studio having a mind of its own.
    const note = page.getByText(/hardware encoder|graphics card|does not report what it can encode/i);
    await expect(note).toBeVisible();

    // Asserted against the policy rather than against a guess: with no hardware encoder, plenty
    // of cores is a legitimate reason to start at 1080p — it is *60 fps* that is off the table.
    // A machine that is short of both must start lower.
    if (!machine.powerEfficient && machine.cores < 12) {
      await expect(page.getByRole("combobox", { name: "Quality" })).not.toHaveValue("1080p");
    }

    if (!machine.powerEfficient) {
      await expect(note).toContainText(/CPU cores|hardware encoder/i);
    }
  });

  test("a rung the operator picks is never overruled", async ({ page, request }) => {
    // The probe resolves asynchronously. One that landed after somebody chose 1080p and quietly
    // moved them back would be worse than no recommendation at all.
    const { email } = await createBroadcaster(request);
    await openStudio(page, email, "Operator wins");

    await page.getByRole("button", { name: /use camera and microphone/i }).click();

    const quality = page.getByRole("combobox", { name: "Quality" });
    await expect(quality).toBeVisible();

    await quality.selectOption("1080p");
    await expect(quality).toHaveValue("1080p");

    // Long enough for any late probe to have resolved and re-applied itself.
    await page.waitForTimeout(1500);
    await expect(quality).toHaveValue("1080p");
  });

  test("the screen frame rate starts where the machine can hold it", async ({ page, request }) => {
    // Software 1080p60 is where a machine without a hardware encoder falls over, and it falls
    // over live rather than in a settings panel.
    const { email } = await createBroadcaster(request);
    await openStudio(page, email, "Frame rate");

    const powerEfficient = await page.evaluate(async () => {
      const info = await navigator.mediaCapabilities.encodingInfo({
        type: "webrtc",
        video: {
          contentType: "video/H264;codecs=avc1.42E01E",
          width: 1920,
          height: 1080,
          bitrate: 6_000_000,
          framerate: 60,
        },
      } as MediaEncodingConfiguration);

      return info.powerEfficient;
    });

    await page.getByRole("button", { name: /use camera and microphone/i }).click();
    await expect(page.getByRole("combobox", { name: "Quality" })).toBeVisible();

    await page.getByRole("button", { name: "Share screen" }).click();
    const frameRate = page.getByRole("combobox", { name: "Frame rate" });
    await expect(frameRate).toBeVisible();

    if (!powerEfficient) {
      await expect(frameRate).toHaveValue("30");
    }
  });
});
