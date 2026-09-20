import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { DestinationPanel } from "@/components/studio/DestinationPanel";
import type { UseDestinationsResult } from "@/hooks/useDestinations";
import type { Destination, ProviderDescriptor } from "@/lib/types";

function buildDestination(overrides: Partial<Destination> = {}): Destination {
  return {
    id: "d1",
    liveSessionId: "s1",
    provider: "YouTube",
    displayName: "Main channel",
    credentialMode: "StreamKey",
    ingestUrl: "rtmp://a.rtmp.youtube.com/live2",
    hasStreamKey: true,
    providerAccountId: null,
    providerAccountName: null,
    enabled: true,
    status: "Idle",
    lastErrorCode: null,
    lastErrorMessage: null,
    watchUrl: null,
    attemptCount: 0,
    nextRetryAt: null,
    startedAt: null,
    stoppedAt: null,
    bytesSent: 0,
    uptimeSeconds: null,
    allowedTransitions: [],
    updatedAt: new Date().toISOString(),
    version: 0,
    ...overrides,
  };
}

const PROVIDERS: ProviderDescriptor[] = [
  {
    provider: "YouTube",
    displayName: "YouTube",
    supportsStreamKey: true,
    supportsLinkedAccount: true,
    linkedAccountConfigured: false,
    defaultIngestUrl: "rtmp://a.rtmp.youtube.com/live2",
    streamKeyHelp: "YouTube Studio, then Go Live, then Stream.",
    helpUrl: "https://studio.youtube.com/channel/live",
  },
  {
    provider: "CustomRtmp",
    displayName: "Custom RTMP",
    supportsStreamKey: true,
    supportsLinkedAccount: false,
    linkedAccountConfigured: false,
    defaultIngestUrl: null,
    streamKeyHelp: "Enter the RTMP server URL and stream key.",
    helpUrl: null,
  },
];

function buildController(overrides: Partial<UseDestinationsResult> = {}): UseDestinationsResult {
  return {
    destinations: [],
    providers: PROVIDERS,
    loading: false,
    error: null,
    busyId: null,
    refresh: vi.fn().mockResolvedValue(undefined),
    applyRealtime: vi.fn(),
    add: vi.fn().mockResolvedValue(undefined),
    remove: vi.fn().mockResolvedValue(undefined),
    setEnabled: vi.fn().mockResolvedValue(undefined),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

function renderPanel(
  destinations: Destination[],
  options: { broadcasting?: boolean; ended?: boolean; controller?: Partial<UseDestinationsResult> } = {},
) {
  const controller = buildController({ destinations, ...options.controller });

  render(
    <DestinationPanel
      destinations={destinations}
      sessionIsBroadcasting={options.broadcasting ?? false}
      sessionIsEnded={options.ended ?? false}
      controller={controller}
    />,
  );

  return controller;
}

describe("DestinationPanel", () => {
  it("invites the creator to add a platform when none are configured", () => {
    renderPanel([]);

    expect(screen.getByText(/Send this broadcast to YouTube/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add destination" })).toBeInTheDocument();
  });

  it("shows each destination with its own status", () => {
    // Names are deliberately distinct from the platform labels, which also appear in each row.
    renderPanel([
      buildDestination({ id: "d1", displayName: "Main channel", status: "Live", uptimeSeconds: 65, bytesSent: 2048 }),
      buildDestination({ id: "d2", displayName: "Company page", provider: "Facebook", status: "Retrying" }),
    ]);

    const youtube = screen.getByText("Main channel").closest("li")!;
    const facebook = screen.getByText("Company page").closest("li")!;

    expect(within(youtube).getByText("Live")).toBeInTheDocument();
    expect(within(facebook).getByText("Reconnecting")).toBeInTheDocument();
  });

  /**
   * The visible form of the isolation guarantee. A destination that fails must say so inside its
   * own row and explicitly reassure the broadcaster that the stream itself is fine.
   */
  it("states plainly that a failed destination does not affect the broadcast", () => {
    renderPanel([
      buildDestination({
        status: "Error",
        lastErrorCode: "LIVE_016_DESTINATION_REJECTED",
        lastErrorMessage: "The platform rejected the stream. Check the stream key.",
      }),
    ]);

    expect(screen.getByText(/Your broadcast is unaffected/)).toBeInTheDocument();
    expect(screen.getByText("Failed")).toBeInTheDocument();
  });

  it("counts live and failed destinations in the summary", () => {
    renderPanel([
      buildDestination({ id: "d1", displayName: "A", status: "Live" }),
      buildDestination({ id: "d2", displayName: "B", status: "Live" }),
      buildDestination({ id: "d3", displayName: "C", status: "Error" }),
    ]);

    expect(screen.getByText(/3 destinations · 2 live · 1 failed/)).toBeInTheDocument();
  });

  it("offers a retry for a failed destination while the session is broadcasting", async () => {
    const user = userEvent.setup();
    const controller = renderPanel([buildDestination({ status: "Error", lastErrorMessage: "Rejected." })], {
      broadcasting: true,
    });

    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(controller.start).toHaveBeenCalledWith("d1");
  });

  it("offers stop for a running destination and not remove", async () => {
    const user = userEvent.setup();
    const controller = renderPanel([buildDestination({ status: "Live" })], { broadcasting: true });

    expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Stop" }));
    expect(controller.stop).toHaveBeenCalledWith("d1");
  });

  it("shows the retry countdown while reconnecting", () => {
    const nextRetryAt = new Date(Date.now() + 8000).toISOString();
    renderPanel([buildDestination({ status: "Retrying", attemptCount: 2, nextRetryAt })]);

    expect(screen.getByText(/Reconnecting in \d+s \(attempt 2\)/)).toBeInTheDocument();
  });

  it("links to the broadcast on the platform once one exists", () => {
    renderPanel([buildDestination({ status: "Live", watchUrl: "https://youtube.com/watch?v=abc" })]);

    const link = screen.getByRole("link", { name: /View on YouTube/ });
    expect(link).toHaveAttribute("href", "https://youtube.com/watch?v=abc");
    // Opening a third-party page must not hand it a window reference back.
    expect(link).toHaveAttribute("rel", expect.stringContaining("noopener"));
  });

  it("hides management controls once the session has ended", () => {
    renderPanel([buildDestination({ status: "Stopped" })], { ended: true });

    expect(screen.queryByRole("button", { name: "Add destination" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();
  });

  describe("the add form", () => {
    it("submits a platform with its prefilled endpoint", async () => {
      const user = userEvent.setup();
      const controller = renderPanel([]);

      await user.click(screen.getByRole("button", { name: "Add destination" }));
      await user.type(screen.getByLabelText("Name"), "My channel");
      await user.type(screen.getByLabelText("Stream key"), "abcd-1234");
      // With the form open the toggle reads "Cancel", so this matches only the submit button.
      await user.click(screen.getByRole("button", { name: "Add destination" }));

      expect(controller.add).toHaveBeenCalledWith({
        provider: "YouTube",
        displayName: "My channel",
        ingestUrl: "rtmp://a.rtmp.youtube.com/live2",
        streamKey: "abcd-1234",
      });
    });

    /** Only providers without a fixed endpoint should ask the operator for one. */
    it("asks for a server URL only when the platform has no default", async () => {
      const user = userEvent.setup();
      renderPanel([]);

      await user.click(screen.getByRole("button", { name: "Add destination" }));
      expect(screen.queryByLabelText("Server URL")).not.toBeInTheDocument();

      await user.selectOptions(screen.getByLabelText("Platform"), "CustomRtmp");
      expect(screen.getByLabelText("Server URL")).toBeInTheDocument();
    });

    /** The key is a secret in transit through the DOM; it must never be a plain text input. */
    it("masks the stream key field", async () => {
      const user = userEvent.setup();
      renderPanel([]);

      await user.click(screen.getByRole("button", { name: "Add destination" }));

      expect(screen.getByLabelText("Stream key")).toHaveAttribute("type", "password");
      expect(screen.getByLabelText("Stream key")).toHaveAttribute("autocomplete", "off");
    });

    it("will not submit without a name and a key", async () => {
      const user = userEvent.setup();
      renderPanel([]);

      await user.click(screen.getByRole("button", { name: "Add destination" }));

      const submit = screen.getByRole("button", { name: "Add destination" });
      expect(submit).toBeDisabled();

      await user.type(screen.getByLabelText("Name"), "My channel");
      expect(submit).toBeDisabled();

      await user.type(screen.getByLabelText("Stream key"), "key");
      expect(submit).toBeEnabled();
    });
  });
});
