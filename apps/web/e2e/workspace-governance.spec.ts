import { expect, test } from "@playwright/test";
import { API_URL, PASSWORD, createBroadcaster } from "./support/journey";

/**
 * Tenant governance against the real stack
 * (implementation/phase-7-scale-security-and-globalization.md).
 *
 * These exercise the parts that only exist once everything is wired together: the readiness probe
 * an orchestrator will actually call, the operator API guarded by a shared secret, and a limit
 * saved in the browser being enforced by the API on the next request.
 */

const INTERNAL_SECRET = process.env.E2E_INTERNAL_SECRET ?? "";

test.describe("Health probes", () => {
  test("liveness and readiness answer separately", async ({ request }) => {
    const live = await request.get(`${API_URL}/health/live`);
    expect(live.ok()).toBeTruthy();

    const ready = await request.get(`${API_URL}/health/ready`);
    expect(ready.ok()).toBeTruthy();

    // Readiness names each check, so an operator can tell a draining instance from a database
    // outage without reading logs.
    const body = (await ready.json()) as { status: string; checks: Record<string, unknown> };
    expect(body.status).toBe("Healthy");
    expect(Object.keys(body.checks)).toEqual(expect.arrayContaining(["database", "draining"]));
  });

  test("the dependency probe reports the media gateway", async ({ request }) => {
    const response = await request.get(`${API_URL}/health/dependencies`);
    const body = (await response.json()) as { checks: Record<string, { status: string }> };

    expect(body.checks["media-gateway"]).toBeDefined();

    // Against a running stack the gateway is reachable. That it is reported at all — separately
    // from readiness — is the point: a gateway outage must not empty the load balancer.
    expect(body.checks["media-gateway"]?.status).toBe("Healthy");
  });
});

test.describe("Workspace administration", () => {
  test("a limit saved in the browser is enforced by the API", async ({ page, request }) => {
    const { email } = await createBroadcaster(request);

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    await page.getByRole("link", { name: "Workspace" }).click();
    await expect(page).toHaveURL(/\/workspace/);

    // Usage is measured, not modelled: a brand new workspace has run nothing.
    await expect(page.getByText("0 / 3")).toBeVisible();

    await page.getByLabel(/Open sessions at once/).fill("1");
    await page.getByRole("button", { name: "Save limits" }).click();
    await expect(page.getByText("Limits saved.")).toBeVisible();

    const token = await page.evaluate(() => window.localStorage.getItem("livestream.accessToken"));
    const headers = { Authorization: `Bearer ${token}` };

    const first = await request.post(`${API_URL}/api/v1/live-sessions`, {
      headers,
      data: { title: "Within the limit", visibility: "PRIVATE", recordingEnabled: false },
    });
    expect(first.ok()).toBeTruthy();

    const second = await request.post(`${API_URL}/api/v1/live-sessions`, {
      headers,
      data: { title: "One too many", visibility: "PRIVATE", recordingEnabled: false },
    });

    // 409 rather than 429: waiting will not help, only ending a session or changing the plan will.
    expect(second.status()).toBe(409);
    expect((await second.json()).errorCode).toBe("LIVE_033_PLAN_LIMIT_REACHED");
  });

  test("a workspace cannot raise its own limit above its plan", async ({ request }) => {
    const { accessToken } = await createBroadcaster(request);
    const me = await request.get(`${API_URL}/api/v1/auth/me`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });

    const workspaceId = (await me.json()).workspaces[0].workspaceId as string;

    const response = await request.put(`${API_URL}/api/v1/workspaces/${workspaceId}/limits`, {
      headers: { Authorization: `Bearer ${accessToken}` },
      data: {
        maxConcurrentSessions: 999,
        maxDestinationsPerSession: null,
        maxSourcesPerSession: null,
        recordingRetentionDays: null,
        residencyRegion: null,
      },
    });

    expect(response.status()).toBe(409);
    expect((await response.json()).errorCode).toBe("LIVE_033_PLAN_LIMIT_REACHED");
  });
});

test.describe("Operator API", () => {
  test.skip(INTERNAL_SECRET === "", "E2E_INTERNAL_SECRET is not set for this run");

  test("capacity is invisible without the shared secret and readable with it", async ({ request }) => {
    const { accessToken } = await createBroadcaster(request);

    // A signed-in member is not an operator. The route answers 404 rather than 403, so its
    // existence is not confirmed to somebody probing for it.
    const asMember = await request.get(`${API_URL}/api/v1/operations/capacity`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    expect(asMember.status()).toBe(404);

    const asOperator = await request.get(`${API_URL}/api/v1/operations/capacity`, {
      headers: { "X-Internal-Auth": INTERNAL_SECRET },
    });
    expect(asOperator.ok()).toBeTruthy();

    const capacity = (await asOperator.json()) as {
      role: string;
      workspaces: number;
      sessionsByStatus: Record<string, number>;
      leases: { name: string; ownerId: string; held: boolean }[];
    };

    expect(capacity.role).toBe("ALL");
    expect(capacity.workspaces).toBeGreaterThan(0);
    expect(capacity.sessionsByStatus).toHaveProperty("LIVE");

    // The loops take a lease before doing any work, which is what proves leader election is
    // running rather than merely compiled in. Not every lease is held at every instant: one
    // released by a departing instance stays free until another loop's next tick, and
    // asserting otherwise would be asserting a race.
    expect(capacity.leases.length).toBeGreaterThan(0);
    expect(capacity.leases.some((lease) => lease.held)).toBeTruthy();
    expect(capacity.leases.every((lease) => lease.ownerId.length > 0)).toBeTruthy();
  });

  test("only an operator can move a workspace between plans", async ({ request }) => {
    const { accessToken } = await createBroadcaster(request);
    const me = await request.get(`${API_URL}/api/v1/auth/me`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });

    const workspaceId = (await me.json()).workspaces[0].workspaceId as string;

    const asMember = await request.put(`${API_URL}/api/v1/operations/workspaces/${workspaceId}/plan`, {
      headers: { Authorization: `Bearer ${accessToken}` },
      data: { plan: "Enterprise" },
    });
    expect(asMember.status()).toBe(404);

    const asOperator = await request.put(`${API_URL}/api/v1/operations/workspaces/${workspaceId}/plan`, {
      headers: { "X-Internal-Auth": INTERNAL_SECRET },
      data: { plan: "Business" },
    });

    expect(asOperator.ok()).toBeTruthy();
    expect((await asOperator.json()).plan).toBe("BUSINESS");

    // And the workspace can now do what only that plan allows.
    const limits = await request.get(`${API_URL}/api/v1/workspaces/${workspaceId}/limits`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    expect((await limits.json()).singleSignOnAllowed).toBe(true);
  });
});
