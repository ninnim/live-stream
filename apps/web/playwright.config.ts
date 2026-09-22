import { defineConfig, devices } from "@playwright/test";

/**
 * End-to-end configuration.
 *
 * These specs run against a **running stack** (API, media gateway, studio) — they are the only
 * tests that exercise the real media path, so nothing here is mocked. Start the stack first:
 *
 *   docker compose -f infrastructure/docker/docker-compose.yml --env-file .env up -d
 *
 * Chromium is launched with a synthetic camera and microphone so a real WebRTC stream is published
 * without needing physical hardware or a human to click the permission prompt.
 */
/**
 * `getUserMedia` is only exposed in a secure context, and `http://localhost` is the only plaintext
 * origin that qualifies. These specs must therefore reach the studio as localhost — from a
 * container, that means `--network host`, not a compose-network hostname. See
 * docs/troubleshooting/phase-2-distribution.md.
 */
const webBaseUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";

/** Synthetic camera, synthetic microphone, and no permission prompt to click. */
const FAKE_MEDIA_ARGS = [
  // A synthetic 30fps video source and a tone generator, so no hardware is required.
  "--use-fake-device-for-media-stream",
  // Auto-accept the camera/microphone prompt.
  "--use-fake-ui-for-media-stream",
  "--autoplay-policy=no-user-gesture-required",
];

export default defineConfig({
  testDir: "./e2e",
  // Publishing, going live and finalizing a recording involve real media and real timers.
  timeout: 120_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],

  use: {
    baseURL: webBaseUrl,
    trace: "retain-on-failure",
    video: "off",
    // localhost is a secure context, so getUserMedia is permitted.
    permissions: ["camera", "microphone"],
  },

  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"],
        launchOptions: { args: FAKE_MEDIA_ARGS },
      },
      // The mobile spec has its own project; running it here would exercise the desktop studio
      // under a phone-shaped spec and fail on controls that do not exist there.
      testIgnore: /mobile-broadcast\.spec\.ts/,
    },
    {
      /**
       * Phase 3. A Pixel-sized viewport with touch and a phone user agent — the three signals the
       * studio's form-factor detection actually reads — so the mobile broadcaster is chosen the
       * way a real phone would choose it rather than by a test flag.
       *
       * Built from `Desktop Chrome` with those overrides rather than from a `devices["Pixel 7"]`
       * descriptor, because that descriptor also sets `isMobile`, which is Chromium-only and
       * changes nothing this detection looks at.
       */
      name: "mobile-chrome",
      use: {
        ...devices["Desktop Chrome"],
        viewport: { width: 412, height: 915 },
        deviceScaleFactor: 2.6,
        hasTouch: true,
        userAgent:
          "Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36",
        launchOptions: { args: FAKE_MEDIA_ARGS },
      },
      testMatch: /mobile-broadcast\.spec\.ts/,
    },
  ],
});
