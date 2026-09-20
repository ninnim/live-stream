import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { LimitsPanel } from "@/components/workspace/LimitsPanel";
import { SsoPanel } from "@/components/workspace/SsoPanel";
import { UsagePanel } from "@/components/workspace/UsagePanel";
import type { SsoConnection, WorkspaceLimits, WorkspaceUsage } from "@/lib/types";

function buildLimits(overrides: Partial<WorkspaceLimits> = {}): WorkspaceLimits {
  return {
    workspaceId: "11111111-1111-1111-1111-111111111111",
    plan: "PRO",
    effectiveMaxConcurrentSessions: 3,
    effectiveMaxDestinationsPerSession: 5,
    effectiveMaxSourcesPerSession: 8,
    effectiveRecordingRetentionDays: 30,
    planMaxConcurrentSessions: 3,
    planMaxDestinationsPerSession: 5,
    planMaxSourcesPerSession: 8,
    planRecordingRetentionDays: 30,
    maxConcurrentSessionsOverride: null,
    maxDestinationsPerSessionOverride: null,
    maxSourcesPerSessionOverride: null,
    recordingRetentionDaysOverride: null,
    residencyRegion: null,
    deploymentRegion: "eu-central",
    singleSignOnAllowed: false,
    dataResidencyAllowed: false,
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function buildUsage(overrides: Partial<WorkspaceUsage> = {}): WorkspaceUsage {
  return {
    workspaceId: "11111111-1111-1111-1111-111111111111",
    plan: "PRO",
    periodStart: "2026-08-07T00:00:00Z",
    periodEnd: "2026-09-06T00:00:00Z",
    capacity: {
      openSessions: 2,
      broadcastingSessions: 1,
      maxConcurrentSessions: 3,
      maxDestinationsPerSession: 5,
      maxSourcesPerSession: 8,
      recordingRetentionDays: 30,
      residencyRegion: null,
    },
    usage: {
      sessions: 4,
      streamingHours: 6.25,
      relayHours: 3,
      ingestGb: 12.5,
      relayEgressGb: 9.75,
      storedRecordingGb: 4.5,
      recordingsStored: 3,
      aiJobs: 2,
    },
    cost: {
      currency: "USD",
      ratesConfigured: true,
      lines: [
        { key: "streaming", label: "Broadcast hours", quantity: 6.25, unit: "hour", rate: 0.5, amount: 3.125 },
        { key: "relay", label: "Platform relay hours", quantity: 3, unit: "hour", rate: null, amount: null },
        { key: "ai", label: "AI analysis", quantity: 2, unit: "job", rate: null, amount: 0.0295 },
      ],
      estimatedTotal: 3.1545,
      notMetered: ["Viewer delivery — CDN and WebRTC egress to viewers is not metered"],
    },
    ...overrides,
  };
}

function buildConnection(overrides: Partial<SsoConnection> = {}): SsoConnection {
  return {
    workspaceId: "11111111-1111-1111-1111-111111111111",
    protocol: "OIDC",
    issuer: "https://login.example.com",
    clientId: "client-id",
    enabled: true,
    jitProvisioning: true,
    defaultRole: "HOST",
    usable: true,
    domains: [{ domain: "example.com", verified: true, verifiedAt: new Date().toISOString() }],
    redirectUri: "https://studio.example.com/sign-in/sso/callback",
    lastUsedAt: null,
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

beforeEach(() => vi.clearAllMocks());

describe("UsagePanel", () => {
  it("shows an unpriced line as unknown rather than as free", () => {
    // A cost line showing $0.00 reads as free, and nobody budgets for free.
    render(<UsagePanel usage={buildUsage()} />);

    const relayRow = screen.getByRole("row", { name: /Platform relay hours/ });
    expect(relayRow).toHaveTextContent("—");

    const streamingRow = screen.getByRole("row", { name: /Broadcast hours/ });
    expect(streamingRow).toHaveTextContent("$3.13");
  });

  it("names what the platform does not measure", () => {
    render(<UsagePanel usage={buildUsage()} />);

    expect(screen.getByText(/Viewer delivery/)).toBeInTheDocument();
  });

  it("says so when the deployment has configured no rates at all", () => {
    const usage = buildUsage();
    render(
      <UsagePanel
        usage={{ ...usage, cost: { ...usage.cost, ratesConfigured: false, estimatedTotal: null } }}
      />,
    );

    expect(screen.getByText(/No unit rates are configured/)).toBeInTheDocument();
  });

  it("shows headroom against the plan, not just a count", () => {
    render(<UsagePanel usage={buildUsage()} />);

    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    expect(screen.getByText("1 more allowed")).toBeInTheDocument();
  });
});

describe("LimitsPanel", () => {
  it("shows what the plan allows next to what is enforced", () => {
    render(<LimitsPanel limits={buildLimits()} canEdit onSave={vi.fn()} />);

    expect(screen.getByText(/Plan allows 3 sessions. Currently enforced: 3./)).toBeInTheDocument();
  });

  it("leaves an unset limit empty rather than pre-filling the plan value", () => {
    // Pre-filling would turn every save into a permanent override of a number nobody chose, so a
    // later plan upgrade would silently not apply.
    render(<LimitsPanel limits={buildLimits()} canEdit onSave={vi.fn()} />);

    expect(screen.getByLabelText(/Open sessions at once/)).toHaveValue(null);
  });

  it("sends an empty field as null so the plan value applies again", async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(
      <LimitsPanel
        limits={buildLimits({ maxConcurrentSessionsOverride: 2, effectiveMaxConcurrentSessions: 2 })}
        canEdit
        onSave={onSave}
      />,
    );

    await userEvent.clear(screen.getByLabelText(/Open sessions at once/));
    await userEvent.click(screen.getByRole("button", { name: "Save limits" }));

    await waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(onSave.mock.calls[0]?.[0]).toMatchObject({ maxConcurrentSessions: null });
  });

  it("offers no save button to somebody who cannot change limits", () => {
    render(<LimitsPanel limits={buildLimits()} canEdit={false} onSave={vi.fn()} />);

    expect(screen.queryByRole("button", { name: "Save limits" })).not.toBeInTheDocument();
    expect(screen.getByLabelText(/Open sessions at once/)).toBeDisabled();
  });

  it("disables data residency on a plan that does not include it", () => {
    render(<LimitsPanel limits={buildLimits()} canEdit onSave={vi.fn()} />);

    expect(screen.getByLabelText(/Data residency/)).toBeDisabled();
    expect(screen.getByText(/Business and Enterprise plans/)).toBeInTheDocument();
  });

  it("surfaces a rejected save instead of appearing to succeed", async () => {
    const onSave = vi.fn().mockRejectedValue(new Error("The PRO plan allows 3 concurrent sessions."));
    render(<LimitsPanel limits={buildLimits()} canEdit onSave={onSave} />);

    await userEvent.click(screen.getByRole("button", { name: "Save limits" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/PRO plan allows 3/);
  });
});

describe("SsoPanel", () => {
  it("says nothing is possible on a plan without single sign-on", () => {
    render(<SsoPanel connection={null} allowed={false} onSave={vi.fn()} onRemove={vi.fn()} />);

    expect(screen.getByText(/Business and Enterprise plans/)).toBeInTheDocument();
    expect(screen.queryByLabelText("Issuer")).not.toBeInTheDocument();
  });

  it("explains that a claimed domain does nothing until an operator verifies it", () => {
    render(
      <SsoPanel
        connection={buildConnection({
          usable: false,
          domains: [{ domain: "example.com", verified: false, verifiedAt: null }],
        })}
        allowed
        onSave={vi.fn()}
        onRemove={vi.fn()}
      />,
    );

    expect(screen.getByText(/Awaiting verification/)).toBeInTheDocument();
    expect(screen.getByText(/not self-service/)).toBeInTheDocument();
  });

  it("never offers Owner or Admin as a role single sign-on can grant", () => {
    // An identity provider that could mint an admin could reconfigure the identity provider.
    render(<SsoPanel connection={buildConnection()} allowed onSave={vi.fn()} onRemove={vi.fn()} />);

    const roles = screen.getByLabelText(/Role for new members/);
    expect(roles).toHaveTextContent("Host");
    expect(roles).not.toHaveTextContent("Owner");
    expect(roles).not.toHaveTextContent("Admin");
  });

  it("sends no client secret when the field was left empty, so the stored one survives", async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<SsoPanel connection={buildConnection()} allowed onSave={onSave} onRemove={vi.fn()} />);

    await userEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(onSave.mock.calls[0]?.[0]).toMatchObject({
      clientSecret: null,
      issuer: "https://login.example.com",
    });
  });

  it("shows the redirect URI the provider has to be configured with", () => {
    render(<SsoPanel connection={buildConnection()} allowed onSave={vi.fn()} onRemove={vi.fn()} />);

    expect(screen.getByText("https://studio.example.com/sign-in/sso/callback")).toBeInTheDocument();
  });

  it("splits a comma separated domain list", async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<SsoPanel connection={buildConnection()} allowed onSave={onSave} onRemove={vi.fn()} />);

    const domains = screen.getByLabelText(/Email domains/);
    await userEvent.clear(domains);
    await userEvent.type(domains, "one.example.com, two.example.com");
    await userEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(onSave.mock.calls[0]?.[0].domains).toEqual(["one.example.com", "two.example.com"]);
  });
});
