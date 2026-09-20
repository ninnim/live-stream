import type { APIRequestContext } from "@playwright/test";
import { expect, test } from "@playwright/test";
import { API_URL, createBroadcaster, PASSWORD } from "./support/journey";

/**
 * Recovering a lost password, all the way through.
 *
 * The API tests prove the endpoints against a fake sender. This runs against a **real mail server**
 * — a disposable one started by `docker-compose.e2e.yml`, the same way the RTMP receiver stands in
 * for a platform — so the SMTP handshake, MailKit, and the message this platform actually composes
 * are all exercised, and the link is read back from a genuine inbox.
 *
 * What only this can prove: that the link the server builds lands on a page that exists, that the
 * page returns the token in the shape the server expects, and that the new password then works at
 * the real sign-in form.
 */

const NEW_PASSWORD = "a-replacement-password-9876";

/** The disposable mail server's HTTP API, as reachable from wherever the specs run. */
const MAILPIT_API = process.env.E2E_MAILPIT_API ?? "http://localhost:8025";

interface MailpitMessage {
  ID: string;
  To: { Address: string }[];
}

/**
 * Reads the most recent reset link actually delivered to an address.
 *
 * Polled, because SMTP delivery is not synchronous with the HTTP request that triggered it: the API
 * answers 202 once the message is accepted, and the mail server writes it a moment later.
 */
async function resetLinkFor(request: APIRequestContext, email: string): Promise<string> {
  let found: string | null = null;

  await expect
    .poll(
      async () => {
        const listed = await request.get(`${MAILPIT_API}/api/v1/messages?limit=100`);
        if (!listed.ok()) return null;

        const messages = ((await listed.json()) as { messages?: MailpitMessage[] }).messages ?? [];
        const mine = messages.find((message) =>
          message.To.some((to) => to.Address.toLowerCase() === email.toLowerCase()),
        );

        if (!mine) return null;

        const body = await request.get(`${MAILPIT_API}/api/v1/message/${mine.ID}`);
        if (!body.ok()) return null;

        const text = ((await body.json()) as { Text?: string }).Text ?? "";
        found = /https?:\/\/[^\s]*\/reset-password\?token=[A-Za-z0-9_-]+/.exec(text)?.[0] ?? null;
        return found;
      },
      { timeout: 20_000, message: `no reset email arrived for ${email}` },
    )
    .toBeTruthy();

  return found!;
}

/** The link's own path, so it is followed exactly as built rather than reconstructed. */
function pathOf(link: string): string {
  const url = new URL(link);
  return url.pathname + url.search;
}

test.describe("forgetting a password", () => {
  test("a reset link from the email signs you back in with a new password", async ({ page, request }) => {
    const { email } = await createBroadcaster(request);

    // Straight from the sign-in page, carrying the address already typed.
    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByRole("link", { name: /forgot your password/i }).click();

    await expect(page).toHaveURL(/\/forgot-password/);
    await expect(page.getByLabel("Email")).toHaveValue(email);

    await page.getByRole("button", { name: /send reset link/i }).click();
    await expect(page.getByRole("heading", { name: /check your email/i })).toBeVisible();

    // Read from a real inbox. Following the link by its own path is what catches it pointing at
    // the API's origin instead of the web app's.
    const link = await resetLinkFor(request, email);
    await page.goto(pathOf(link));

    await expect(page.getByRole("heading", { name: /choose a new password/i })).toBeVisible();

    await page.getByLabel("New password", { exact: true }).fill(NEW_PASSWORD);
    await page.getByLabel("Confirm new password").fill(NEW_PASSWORD);
    await page.getByRole("button", { name: /change password/i }).click();

    await expect(page.getByRole("heading", { name: /password changed/i })).toBeVisible();
    await expect(page.getByText(/signed out everywhere/i)).toBeVisible();

    // The real form, with the real new password.
    await page.getByRole("button", { name: /go to sign in/i }).click();
    await expect(page).toHaveURL(/\/login/);

    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(NEW_PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page).toHaveURL(/\/dashboard/);

    // And the old one is genuinely gone, asked of the API rather than of the page.
    const oldPassword = await request.post(`${API_URL}/api/v1/auth/login`, {
      data: { email, password: PASSWORD },
    });
    expect(oldPassword.status()).toBe(401);
  });

  test("a link that has already been used says so and offers a new one", async ({ page, request }) => {
    const { email } = await createBroadcaster(request);
    await request.post(`${API_URL}/api/v1/auth/forgot-password`, { data: { email } });

    const path = pathOf(await resetLinkFor(request, email));

    await page.goto(path);
    await page.getByLabel("New password", { exact: true }).fill(NEW_PASSWORD);
    await page.getByLabel("Confirm new password").fill(NEW_PASSWORD);
    await page.getByRole("button", { name: /change password/i }).click();
    await expect(page.getByRole("heading", { name: /password changed/i })).toBeVisible();

    // Opening the same link again — from a browser's history, or a forwarded email.
    await page.goto(path);
    await page.getByLabel("New password", { exact: true }).fill("a-third-password-4321");
    await page.getByLabel("Confirm new password").fill("a-third-password-4321");
    await page.getByRole("button", { name: /change password/i }).click();

    // Matched on the text, not on `role="alert"`: Next.js renders its own route announcer with
    // that role, so the role alone resolves to two elements and the assertion fails on
    // strictness rather than on behaviour.
    await expect(page.getByText(/no longer valid/i)).toBeVisible();
    await expect(page.getByRole("link", { name: /send a new link/i })).toBeVisible();
  });

  test("the form refuses a password that will not be accepted", async ({ page, request }) => {
    // Checked in the page so a typo costs no round trip — and, more to the point, so it cannot
    // burn the single-use link the person is holding.
    const { email } = await createBroadcaster(request);
    await request.post(`${API_URL}/api/v1/auth/forgot-password`, { data: { email } });

    await page.goto(pathOf(await resetLinkFor(request, email)));

    await page.getByLabel("New password", { exact: true }).fill("too-short");
    await expect(page.getByText(/more characters? needed/i)).toBeVisible();
    await expect(page.getByRole("button", { name: /change password/i })).toBeDisabled();

    await page.getByLabel("New password", { exact: true }).fill(NEW_PASSWORD);
    await page.getByLabel("Confirm new password").fill("something-else-entirely");
    await expect(page.getByText(/do not match/i)).toBeVisible();
    await expect(page.getByRole("button", { name: /change password/i })).toBeDisabled();
  });

  test("an unknown address is answered exactly like a known one", async ({ page }) => {
    // The page must not undo the server's care by saying "no account with that address".
    await page.goto("/forgot-password");
    await page.getByLabel("Email").fill(`nobody-${Date.now()}@example.com`);
    await page.getByRole("button", { name: /send reset link/i }).click();

    await expect(page.getByRole("heading", { name: /check your email/i })).toBeVisible();
    await expect(page.getByText(/no account|not found|unknown/i)).toHaveCount(0);
  });
});
