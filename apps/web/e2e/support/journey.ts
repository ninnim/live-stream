import { execFileSync } from "node:child_process";
import type { APIRequestContext, Page } from "@playwright/test";
import { expect } from "@playwright/test";

/**
 * Shared steps for end-to-end specs that run against a live stack.
 *
 * Nothing here is mocked: these drive the real studio through a real browser and read state back
 * from the real API.
 */

export const API_URL = process.env.E2E_API_URL ?? "http://localhost:8080";
export const PASSWORD = "an-example-password-1234";

export interface Broadcaster {
  accessToken: string;
  email: string;
}

export async function createBroadcaster(request: APIRequestContext): Promise<Broadcaster> {
  const email = `e2e-${Date.now()}-${Math.floor(Math.random() * 100_000)}@example.com`;

  const response = await request.post(`${API_URL}/api/v1/auth/register`, {
    data: { email, password: PASSWORD, displayName: "E2E Broadcaster", workspaceName: "E2E Workspace" },
  });

  expect(response.ok(), `registration failed: ${await response.text()}`).toBeTruthy();
  return { accessToken: ((await response.json()) as { accessToken: string }).accessToken, email };
}

export interface SessionStatus {
  id: string;
  status: string;
  startedAt: string | null;
  isBroadcasting: boolean;
}

export async function statusOf(
  request: APIRequestContext,
  sessionId: string,
  token: string,
): Promise<SessionStatus> {
  const response = await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/status`, {
    headers: { Authorization: `Bearer ${token}` },
  });

  expect(response.ok(), `status read failed: ${await response.text()}`).toBeTruthy();
  return (await response.json()) as SessionStatus;
}

export interface LiveBroadcast {
  sessionId: string;
  token: string;
  email: string;
}

/** Signs in, creates a session, captures media, and goes live — the common prelude. */
export async function goLive(
  page: Page,
  request: APIRequestContext,
  title: string,
): Promise<LiveBroadcast> {
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

  await page.getByRole("button", { name: /use camera and microphone/i }).click();
  await page.getByRole("button", { name: "Start Live" }).click();
  await expect(page.getByText("Live", { exact: true }).first()).toBeVisible({ timeout: 60_000 });

  const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
  expect(token).toBeTruthy();

  return { sessionId, token: token!, email };
}

/**
 * Reaches the media gateway's control API.
 *
 * That API is deliberately not published to the host — it is private to the compose network — so it
 * is queried from a throwaway container on the same network rather than by weakening the deployment.
 */
export function mediaControlApi(path: string, method: "GET" | "POST" = "GET"): unknown {
  // When the control API is reachable directly — the e2e compose overlay publishes it — use it.
  // That keeps the suite runnable from anywhere, including inside a container with no Docker CLI.
  const directBase = process.env.E2E_MEDIA_CONTROL_API;
  if (directBase) {
    const output = execFileSync("curl", ["-fsS", "-X", method, `${directBase}${path}`], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });

    return output.trim() ? JSON.parse(output) : null;
  }

  const network = process.env.E2E_DOCKER_NETWORK ?? "livestream-phase1_default";
  const args = [
    "run", "--rm", "--network", network, "curlimages/curl:latest",
    "-fsS", "-X", method, `http://mediamtx:9997${path}`,
  ];

  const output = execFileSync("docker", args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  return output.trim() ? JSON.parse(output) : null;
}

interface WebRtcSession {
  id: string;
  path: string;
  state: string;
}

/**
 * Severs an active publisher at the gateway, which is what a real broadcaster network drop looks
 * like from the server's point of view: the gateway stays up, but the path stops being ready.
 *
 * Playwright's `setOffline` only affects the browser's HTTP stack, so it does not interrupt the
 * WebRTC media transport and cannot exercise this path.
 */
export function kickPublisher(mediaPath: string): boolean {
  const listing = mediaControlApi("/v3/webrtcsessions/list") as { items?: WebRtcSession[] } | null;
  const publisher = listing?.items?.find((s) => s.path === mediaPath && s.state === "publish");

  if (!publisher) return false;

  mediaControlApi(`/v3/webrtcsessions/kick/${publisher.id}`, "POST");
  return true;
}

/** Resolves the gateway path name for a session from its public playback URL. */
export async function mediaPathOf(request: APIRequestContext, sessionId: string): Promise<string> {
  const response = await request.get(`${API_URL}/api/v1/live-sessions/${sessionId}/playback`);
  expect(response.ok()).toBeTruthy();

  const playback = (await response.json()) as { hlsUrl: string };
  return playback.hlsUrl.split("/").at(-2)!;
}
