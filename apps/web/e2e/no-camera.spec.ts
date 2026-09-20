import { expect, test } from "@playwright/test";
import { PASSWORD, createBroadcaster } from "./support/journey";

/**
 * Broadcasting from a machine with no webcam (docs/04-native-broadcasting.md).
 *
 * The camera is removed the only way a browser can be told it is absent: `getUserMedia` is made to
 * reject a video request with `NotFoundError`, which is exactly what Chrome raises on a desktop
 * with no camera attached. Everything after that is the real studio.
 *
 * What is *not* covered here is publishing a screen: `getDisplayMedia` needs a real desktop capture
 * source, and driving that in a headless browser tests the flags rather than the product. The
 * screen-only publish path is covered in `tests/whip-publisher.test.ts` and
 * `tests/live-studio.test.tsx`.
 */
test.describe("A machine with no camera", () => {
  test.beforeEach(async ({ context }) => {
    await context.addInitScript(() => {
      const media = navigator.mediaDevices;
      const original = media.getUserMedia.bind(media);

      media.getUserMedia = (constraints?: MediaStreamConstraints) => {
        if (constraints?.video) {
          return Promise.reject(
            Object.assign(new Error("Requested device not found"), { name: "NotFoundError" }),
          );
        }

        return original(constraints);
      };

      // The device list has to agree with the failure, or the studio would offer a camera that
      // cannot be opened.
      const enumerate = media.enumerateDevices.bind(media);
      media.enumerateDevices = async () =>
        (await enumerate()).filter((device) => device.kind !== "videoinput");
    });
  });

  test("opens the studio on the microphone alone", async ({ page, request }) => {
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("No camera here");
    await page.getByRole("button", { name: /create and open studio/i }).click();
    await expect(page).toHaveURL(/\/studio\//);

    await page.getByRole("button", { name: /use camera and microphone/i }).click();

    // In the studio, on sound alone — not stuck on the prompt, and not shown a fault.
    await expect(page.getByText(/give your broadcast a picture/i)).toBeVisible();
    await expect(page.getByRole("button", { name: "Start Live" })).toBeEnabled();

    // The camera controls say what is true rather than pretending to work.
    await expect(page.getByRole("combobox", { name: "Camera" })).toBeDisabled();
    await expect(page.getByRole("button", { name: /None\s*Camera/i })).toBeDisabled();

    // The microphone did open, so it is offered normally.
    await expect(page.getByRole("combobox", { name: "Microphone" })).toBeEnabled();
  });

  test("offers screen sharing as a way in before any device is opened", async ({ page, request }) => {
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();

    await page.getByRole("link", { name: "New live session" }).click();
    await page.getByLabel("Title").fill("Screen only");
    await page.getByRole("button", { name: /create and open studio/i }).click();
    await expect(page).toHaveURL(/\/studio\//);

    // Both ways in are offered together, rather than screen sharing being something you find
    // after a camera has already succeeded.
    await expect(page.getByRole("button", { name: /use camera and microphone/i })).toBeEnabled();
    await expect(page.getByRole("button", { name: /share a screen instead/i })).toBeEnabled();
  });
});
