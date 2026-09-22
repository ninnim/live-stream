import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { EncoderPanel } from "@/components/studio/EncoderPanel";
import { useStreamKey } from "@/hooks/useStreamKey";
import { ApiError } from "@/lib/api/client";

const { apiMock } = vi.hoisted(() => ({
  apiMock: {
    prepare: vi.fn(),
    issueStreamKey: vi.fn(),
    revokeStreamKey: vi.fn(),
  },
}));

vi.mock("@/lib/api/live-sessions", () => ({
  liveSessionApi: apiMock,
  authApi: { me: vi.fn(), login: vi.fn(), register: vi.fn(), logout: vi.fn() },
}));

const SESSION_ID = "33333333-3333-3333-3333-333333333333";

function buildKey(pass = "the-secret-key") {
  return {
    protocol: "RTMP",
    serverUrl: "rtmp://media.test:1935",
    streamKey: `ls_abc?user=broadcaster&pass=${pass}`,
    fullUrl: `rtmp://media.test:1935/ls_abc?user=broadcaster&pass=${pass}`,
    srtUrl: null,
    expiresAt: new Date(Date.now() + 43_200_000).toISOString(),
    expiresInSeconds: 43_200,
  };
}

/** Renders the panel over the real hook, so the two are exercised together. */
function Harness({ ended = false }: { ended?: boolean }) {
  const streamKey = useStreamKey(SESSION_ID);
  return <EncoderPanel streamKey={streamKey} sessionIsEnded={ended} />;
}

beforeEach(() => {
  vi.clearAllMocks();
  apiMock.prepare.mockResolvedValue(undefined);
  apiMock.issueStreamKey.mockResolvedValue(buildKey());
  apiMock.revokeStreamKey.mockResolvedValue(undefined);

  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: { writeText: vi.fn().mockResolvedValue(undefined) },
  });
});

describe("EncoderPanel", () => {
  it("prepares the session before issuing a key", async () => {
    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /create a stream key/i }));

    // A key opens nothing until the session has a media path. Without this the encoder is refused
    // on its first attempt, which reads to everybody as a wrong key.
    await waitFor(() => expect(apiMock.issueStreamKey).toHaveBeenCalledWith(SESSION_ID));
    expect(apiMock.prepare).toHaveBeenCalledBefore(apiMock.issueStreamKey);
  });

  it("issues a key even when preparing fails", async () => {
    // Prepare legitimately refuses for a session that is already live — which is a session that
    // already has a path, so the key works anyway.
    apiMock.prepare.mockRejectedValue(new ApiError("already live", 409, null, null));

    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /create a stream key/i }));

    expect(await screen.findByTestId("value-server")).toHaveTextContent("rtmp://media.test:1935");
  });

  it("masks the key until it is asked for", async () => {
    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /create a stream key/i }));

    // This panel gets opened by somebody who is often already on camera, so a key on screen for a
    // second is a key that has been given away.
    const value = await screen.findByTestId("value-stream-key");
    expect(value).not.toHaveTextContent("the-secret-key");

    await userEvent.click(screen.getByRole("button", { name: "Show" }));
    expect(value).toHaveTextContent("the-secret-key");
  });

  it("copies without revealing", async () => {
    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /create a stream key/i }));
    await screen.findByTestId("value-stream-key");

    await userEvent.click(screen.getByTestId("copy-stream-key"));

    // Almost nobody wants to read a key; they want it in the encoder.
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
      "ls_abc?user=broadcaster&pass=the-secret-key",
    );
    expect(screen.getByTestId("value-stream-key")).not.toHaveTextContent("the-secret-key");
  });

  it("reveals the value when the clipboard is refused", async () => {
    // A copy button that silently fails is worse than one that shows the value to select by hand.
    (navigator.clipboard.writeText as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("denied"));

    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /create a stream key/i }));
    await screen.findByTestId("value-stream-key");

    await userEvent.click(screen.getByTestId("copy-stream-key"));

    await waitFor(() =>
      expect(screen.getByTestId("value-stream-key")).toHaveTextContent("the-secret-key"),
    );
  });

  it("rotates, replacing what is shown", async () => {
    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /create a stream key/i }));
    await screen.findByTestId("value-stream-key");

    apiMock.issueStreamKey.mockResolvedValue(buildKey("a-replacement-key"));
    await userEvent.click(screen.getByRole("button", { name: /rotate key/i }));
    await userEvent.click(screen.getByRole("button", { name: "Show" }));

    await waitFor(() =>
      expect(screen.getByTestId("value-stream-key")).toHaveTextContent("a-replacement-key"),
    );
  });

  it("revokes and stops showing anything", async () => {
    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /create a stream key/i }));
    await screen.findByTestId("value-stream-key");

    await userEvent.click(screen.getByRole("button", { name: /revoke/i }));

    await waitFor(() => expect(apiMock.revokeStreamKey).toHaveBeenCalledWith(SESSION_ID));
    expect(screen.queryByTestId("value-stream-key")).not.toBeInTheDocument();
  });

  it("says so when the deployment does not accept encoders", async () => {
    // Off is the default for a deployment that does not want an open ingest port. The panel has to
    // stop offering rather than fail repeatedly.
    apiMock.issueStreamKey.mockRejectedValue(new ApiError("not enabled", 400, null, null));

    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /create a stream key/i }));

    expect(await screen.findByText(/does not accept external encoders/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /create a stream key/i })).not.toBeInTheDocument();
  });

  it("offers nothing once the session has ended", () => {
    render(<Harness ended />);

    expect(screen.getByText(/has ended/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /create a stream key/i })).not.toBeInTheDocument();
  });
});
