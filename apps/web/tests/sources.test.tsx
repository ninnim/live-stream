import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { SourcePanel } from "@/components/studio/SourcePanel";
import type { UseSourcesResult } from "@/hooks/useSources";
import type { SessionSource, SourceInvitation, SourceRole, SourceStatus } from "@/lib/types";

// QR rendering needs a canvas jsdom does not provide, and the code and link work without it.
vi.mock("qrcode", () => ({
  default: { toDataURL: vi.fn().mockResolvedValue("data:image/png;base64,fake") },
}));

function buildSource(overrides: Partial<SessionSource> = {}): SessionSource {
  return {
    id: "s1",
    liveSessionId: "session-1",
    role: "Camera",
    displayName: "Second camera",
    status: "Paired",
    isProgram: false,
    contributesMedia: true,
    ingestConnected: false,
    bitrateKbps: null,
    bytesReceived: 0,
    lastSeenAt: null,
    pairedAt: new Date().toISOString(),
    revokedAt: null,
    permissions: ["PublishMedia", "ViewSession"],
    allowedTransitions: [],
    updatedAt: new Date().toISOString(),
    version: 0,
    ...overrides,
  };
}

const HOST = buildSource({
  id: "host",
  role: "Host",
  displayName: "Studio",
  status: "Paired",
  isProgram: true,
});

function buildInvitation(overrides: Partial<SourceInvitation> = {}): SourceInvitation {
  return {
    source: buildSource({ status: "Invited" }),
    pairingCode: "ABCD-EFGH",
    joinUrl: "http://studio.test/join/ABCDEFGH",
    expiresAt: new Date(Date.now() + 600_000).toISOString(),
    expiresInSeconds: 600,
    ...overrides,
  };
}

function buildController(overrides: Partial<UseSourcesResult> = {}): UseSourcesResult {
  return {
    sources: [],
    loading: false,
    error: null,
    busyId: null,
    invitation: null,
    refresh: vi.fn().mockResolvedValue(undefined),
    applyRealtime: vi.fn(),
    invite: vi.fn().mockResolvedValue(undefined),
    dismissInvitation: vi.fn(),
    revoke: vi.fn().mockResolvedValue(undefined),
    rename: vi.fn().mockResolvedValue(undefined),
    setProgram: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

function renderPanel(
  sources: SessionSource[],
  options: { ended?: boolean; controller?: Partial<UseSourcesResult> } = {},
) {
  const controller = buildController({ sources, ...options.controller });

  render(
    <SourcePanel sources={sources} sessionIsEnded={options.ended ?? false} controller={controller} />,
  );

  return controller;
}

describe("SourcePanel", () => {
  it("invites the operator to add a device when only the studio is present", () => {
    renderPanel([HOST]);

    expect(screen.getByText(/Add a phone as a second camera/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add device" })).toBeInTheDocument();
  });

  it("shows each device with its own presence", () => {
    renderPanel([
      HOST,
      buildSource({ id: "s1", displayName: "Phone", status: "Connected", ingestConnected: true, bitrateKbps: 2500 }),
      buildSource({ id: "s2", displayName: "Laptop", status: "Disconnected" }),
    ]);

    const phone = screen.getByText("Phone").closest("li")!;
    const laptop = screen.getByText("Laptop").closest("li")!;

    expect(within(phone).getByText("Sending")).toBeInTheDocument();
    expect(within(laptop).getByText("No signal")).toBeInTheDocument();
  });

  it("counts the devices that are actually sending", () => {
    renderPanel([
      HOST,
      buildSource({ id: "s1", displayName: "A", status: "Connected" }),
      buildSource({ id: "s2", displayName: "B", status: "Connected" }),
      buildSource({ id: "s3", displayName: "C", status: "Invited" }),
    ]);

    expect(screen.getByText(/3 devices · 2 sending/)).toBeInTheDocument();
  });

  /**
   * The studio source is the broadcast itself. Offering "Remove" beside it would suggest the
   * broadcast can be removed from its own session.
   */
  it("does not offer to remove the studio source", () => {
    renderPanel([HOST]);

    expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();
  });

  it("removes a device on request", async () => {
    const user = userEvent.setup();
    const controller = renderPanel([HOST, buildSource({ id: "s1", displayName: "Phone" })]);

    await user.click(screen.getByRole("button", { name: "Remove" }));

    expect(controller.revoke).toHaveBeenCalledWith("s1");
  });

  it("hides management controls once the session has ended", () => {
    renderPanel([HOST, buildSource({ id: "s1", displayName: "Phone" })], { ended: true });

    expect(screen.queryByRole("button", { name: "Add device" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();
  });

  it("marks the source that is on air", () => {
    renderPanel([HOST]);

    const studio = screen.getByText("Studio").closest("li")!;
    expect(within(studio).getByText("On air")).toBeInTheDocument();
  });

  describe("the invitation", () => {
    it("creates a pairing code for the chosen role", async () => {
      const user = userEvent.setup();
      const controller = renderPanel([HOST]);

      await user.click(screen.getByRole("button", { name: "Add device" }));
      await user.selectOptions(screen.getByLabelText("Role"), "Screen");
      await user.click(screen.getByRole("button", { name: "Create pairing code" }));

      expect(controller.invite).toHaveBeenCalledWith("Screen", "Screen share");
    });

    /**
     * The code cannot be retrieved a second time, so it stays on screen until dismissed rather
     * than flashing past in a toast.
     */
    it("shows the code, the link and a QR image until dismissed", async () => {
      renderPanel([HOST], { controller: { invitation: buildInvitation() } });

      expect(screen.getByText("ABCD-EFGH")).toBeInTheDocument();
      expect(screen.getByText("http://studio.test/join/ABCDEFGH")).toBeInTheDocument();
      expect(await screen.findByRole("img", { name: /QR code to join/ })).toBeInTheDocument();
    });

    it("says the code is single use and when it expires", () => {
      renderPanel([HOST], { controller: { invitation: buildInvitation() } });

      expect(screen.getByText(/single use/)).toBeInTheDocument();
      expect(screen.getByText(/Expires in/)).toBeInTheDocument();
    });

    /** A code that has quietly expired must say so rather than being typed in vain. */
    it("reports an expired code instead of showing a QR for it", async () => {
      renderPanel([HOST], {
        controller: {
          invitation: buildInvitation({
            expiresAt: new Date(Date.now() - 1000).toISOString(),
            expiresInSeconds: 0,
          }),
        },
      });

      await waitFor(() => expect(screen.getByText(/This code has expired/)).toBeInTheDocument());
      expect(screen.queryByRole("img", { name: /QR code/ })).not.toBeInTheDocument();
    });

    it("can be dismissed once the device has joined", async () => {
      const user = userEvent.setup();
      const controller = renderPanel([HOST], { controller: { invitation: buildInvitation() } });

      await user.click(screen.getByRole("button", { name: "Done" }));

      expect(controller.dismissInvitation).toHaveBeenCalled();
    });
  });

  describe("status wording", () => {
    /** Control-room vocabulary, not state-machine vocabulary. */
    const cases: [SourceStatus, string][] = [
      ["Invited", "Waiting to join"],
      ["Paired", "Joined"],
      ["Connected", "Sending"],
      ["Disconnected", "No signal"],
      ["Revoked", "Removed"],
    ];

    it.each(cases)("renders %s as %s", (status, label) => {
      renderPanel([buildSource({ id: "s1", displayName: "Phone", status })]);

      const row = screen.getByText("Phone").closest("li")!;
      expect(within(row).getByText(label)).toBeInTheDocument();
    });
  });

  describe("roles", () => {
    const cases: [SourceRole, string][] = [
      ["Camera", "Camera"],
      ["Screen", "Screen share"],
      ["Audio", "Audio"],
      ["Moderator", "Moderator"],
      ["Operator", "Operator"],
    ];

    it.each(cases)("labels a %s source as %s", (role, label) => {
      renderPanel([buildSource({ id: "s1", displayName: "Device", role })]);

      const row = screen.getByText("Device").closest("li")!;
      expect(within(row).getByText(new RegExp(label))).toBeInTheDocument();
    });
  });
});
