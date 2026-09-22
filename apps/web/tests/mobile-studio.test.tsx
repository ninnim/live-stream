import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { installWebRtcStubs } from "./support/fake-webrtc";
import { MobileStudio } from "@/components/mobile/MobileStudio";
import type { LiveSession, LiveSessionStatus, LiveSessionStatusPayload } from "@/lib/types";

// The realtime channel needs a live server. The REST path is the source of truth for everything
// asserted here, and it is exercised for real.
vi.mock("@/lib/realtime/live-hub", () => ({
  LiveHubClient: class {
    connect = vi.fn().mockResolvedValue(undefined);
    disconnect = vi.fn().mockResolvedValue(undefined);
    isConnected = false;
  },
}));

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn() }) }));

const { apiMock } = vi.hoisted(() => ({
  apiMock: {
    get: vi.fn(),
    status: vi.fn(),
    prepare: vi.fn(),
    start: vi.fn(),
    stop: vi.fn(),
    issueCredential: vi.fn(),
    reportSignal: vi.fn(),
  },
}));

vi.mock("@/lib/api/live-sessions", () => ({
  liveSessionApi: apiMock,
  authApi: { me: vi.fn(), login: vi.fn(), register: vi.fn(), logout: vi.fn() },
}));

vi.mock("@/lib/api/destinations", () => ({
  destinationApi: {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
    start: vi.fn(),
    stop: vi.fn(),
    get: vi.fn(),
    events: vi.fn(),
  },
  distributionApi: {
    providers: vi.fn().mockResolvedValue([]),
    accounts: vi.fn(),
    beginAuthorization: vi.fn(),
    completeAuthorization: vi.fn(),
    disconnect: vi.fn(),
  },
}));

const SESSION_ID = "22222222-2222-2222-2222-222222222222";

function buildSession(overrides: Partial<LiveSession> = {}): LiveSession {
  return {
    id: SESSION_ID,
    workspaceId: "workspace-1",
    title: "Walking tour",
    description: null,
    status: "DRAFT",
    visibility: "PUBLIC",
    recordingEnabled: true,
    startedAt: null,
    endedAt: null,
    lastErrorCode: null,
    lastErrorMessage: null,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    version: 0,
    health: {
      status: "UNKNOWN",
      ingestConnected: false,
      bitrateKbps: null,
      videoFps: null,
      viewerCount: 0,
      reconnectCount: 0,
      lastIngestAt: null,
      recoveryWindowSecondsRemaining: null,
      observedAt: null,
      lastErrorCode: null,
    },
    playback: { hlsUrl: "https://media.test/ls_abc/index.m3u8", webRtcUrl: "", isLive: false },
    ...overrides,
  };
}

function buildStatus(
  status: LiveSessionStatus,
  overrides: Partial<LiveSessionStatusPayload> = {},
): LiveSessionStatusPayload {
  return {
    id: SESSION_ID,
    status,
    allowedTransitions: [],
    isBroadcasting: status === "LIVE" || status === "DEGRADED" || status === "RECONNECTING",
    isTerminal: status === "ENDED" || status === "FAILED",
    startedAt: null,
    endedAt: null,
    durationSeconds: null,
    lastErrorCode: null,
    lastErrorMessage: null,
    version: 1,
    observedAt: new Date().toISOString(),
    ...overrides,
  } satisfies LiveSessionStatusPayload;
}

interface TestTrack extends MediaStreamTrack {
  emit(type: string): void;
  /** Test hook: what the operating system does to a track during a phone call, on iOS. */
  setMuted(muted: boolean): void;
}

function fakeTrack(kind: "video" | "audio", facingMode?: string): TestTrack {
  const listeners = new Map<string, Set<EventListenerOrEventListenerObject>>();

  const track = {
    kind,
    enabled: true,
    muted: false,
    readyState: "live",
    contentHint: "",
    stop: vi.fn(function stop(this: { readyState: string }) {
      this.readyState = "ended";
    }),
    getSettings: () => ({ deviceId: `${kind}-1`, width: 1280, height: 720, frameRate: 30, facingMode }),
    addEventListener: (type: string, handler: EventListenerOrEventListenerObject) => {
      const existing = listeners.get(type) ?? new Set();
      existing.add(handler);
      listeners.set(type, existing);
    },
    removeEventListener: (type: string, handler: EventListenerOrEventListenerObject) => {
      listeners.get(type)?.delete(handler);
    },
    emit: (type: string) => {
      for (const handler of listeners.get(type) ?? []) {
        if (typeof handler === "function") handler(new Event(type));
        else handler.handleEvent(new Event(type));
      }
    },
    setMuted(muted: boolean) {
      (this as unknown as { muted: boolean }).muted = muted;
    },
  };

  return track as unknown as TestTrack;
}

function trackStream(...tracks: MediaStreamTrack[]) {
  return {
    getTracks: () => tracks,
    getVideoTracks: () => tracks.filter((track) => track.kind === "video"),
    getAudioTracks: () => tracks.filter((track) => track.kind === "audio"),
  } as unknown as MediaStream;
}

/**
 * A phone: two cameras, a microphone, and a `getUserMedia` that honours `facingMode` the way a
 * phone does — by answering with the camera that faces that way.
 */
function installPhone({ cameras = 2 }: { cameras?: number } = {}) {
  const opened: TestTrack[] = [];

  const getUserMedia = vi.fn((constraints: MediaStreamConstraints) => {
    if (constraints.video) {
      const wanted = (constraints.video as MediaTrackConstraints).facingMode as
        | { ideal?: string }
        | undefined;

      // One camera means every request lands on the front one, whatever was asked for. That is
      // the case the preview's mirroring has to read from the track rather than the request.
      const facing = cameras > 1 ? (wanted?.ideal ?? "user") : "user";
      const track = fakeTrack("video", facing);
      opened.push(track);
      return Promise.resolve(trackStream(track));
    }

    const track = fakeTrack("audio");
    opened.push(track);
    return Promise.resolve(trackStream(track));
  });

  Object.defineProperty(navigator, "mediaDevices", {
    configurable: true,
    value: {
      getUserMedia,
      enumerateDevices: vi.fn().mockResolvedValue([
        ...(cameras > 1
          ? [{ kind: "videoinput", deviceId: "front", label: "Front", groupId: "g1" }, { kind: "videoinput", deviceId: "back", label: "Back", groupId: "g2" }]
          : [{ kind: "videoinput", deviceId: "front", label: "Front", groupId: "g1" }]),
        { kind: "audioinput", deviceId: "mic", label: "Microphone", groupId: "g1" },
      ]),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    },
  });

  installWebRtcStubs();
  return { getUserMedia, opened };
}

/** Grants access and waits for the controls to settle. */
async function enterStudio() {
  render(<MobileStudio sessionId={SESSION_ID} />);
  await userEvent.click(await screen.findByTestId("mobile-allow"));
  await waitFor(() => expect(screen.getByTestId("mobile-go-live")).toBeEnabled());
}

beforeEach(() => {
  vi.clearAllMocks();
  apiMock.get.mockResolvedValue(buildSession());
  apiMock.status.mockResolvedValue(buildStatus("DRAFT"));
  apiMock.prepare.mockResolvedValue(buildSession({ status: "READY" }));
  apiMock.start.mockResolvedValue(
    buildStatus("LIVE", { startedAt: new Date().toISOString(), isBroadcasting: true }),
  );
  apiMock.stop.mockResolvedValue(buildStatus("ENDED", { endedAt: new Date().toISOString() }));
  apiMock.issueCredential.mockResolvedValue({
    protocol: "WHIP",
    ingestUrl: "https://media.test/ls_abc/whip",
    token: "short-lived-token",
    expiresAt: new Date(Date.now() + 300_000).toISOString(),
    expiresInSeconds: 300,
  });
  apiMock.reportSignal.mockResolvedValue(undefined);
});

describe("MobileStudio — going live", () => {
  it("asks for the camera before offering anything else", async () => {
    installPhone();

    render(<MobileStudio sessionId={SESSION_ID} />);

    expect(await screen.findByTestId("mobile-allow")).toBeInTheDocument();
    // Nothing can be broadcast before there is something to broadcast.
    expect(screen.getByTestId("mobile-go-live")).toBeDisabled();
  });

  it("captures at 30fps rather than the studio's 60", async () => {
    // The whole battery and uplink argument for the mobile rungs, asserted at the one place it is
    // decided: the constraints handed to the browser.
    const { getUserMedia } = installPhone();

    await enterStudio();

    const video = getUserMedia.mock.calls.find(([c]) => c.video)?.[0].video as MediaTrackConstraints;
    expect(video.frameRate).toEqual({ ideal: 30 });
  });

  it("starts the front camera, and marks it as motion for the encoder", async () => {
    const { getUserMedia, opened } = installPhone();

    await enterStudio();

    const video = getUserMedia.mock.calls.find(([c]) => c.video)?.[0].video as MediaTrackConstraints;
    expect(video.facingMode).toEqual({ ideal: "user" });
    // Under load, shed resolution and keep the picture moving — a handheld shot is always motion.
    expect(opened.find((track) => track.kind === "video")?.contentHint).toBe("motion");
  });

  it("publishes and then asks the server to go live", async () => {
    installPhone();

    await enterStudio();
    await userEvent.click(screen.getByTestId("mobile-go-live"));

    await waitFor(() => expect(apiMock.start).toHaveBeenCalledWith(SESSION_ID));

    // Order matters: media is connected before start is requested, so the session is never shown
    // as LIVE before anything is flowing.
    expect(apiMock.prepare).toHaveBeenCalledBefore(apiMock.start);
    expect(apiMock.issueCredential).toHaveBeenCalledBefore(apiMock.start);
  });

  it("shows the live timer once the server says so", async () => {
    installPhone();

    await enterStudio();
    await userEvent.click(screen.getByTestId("mobile-go-live"));

    expect(await screen.findByTestId("mobile-timer")).toBeInTheDocument();
    expect(await screen.findByTestId("mobile-stop")).toBeInTheDocument();
  });

  it("stops on one tap, with no confirmation", async () => {
    installPhone();

    await enterStudio();
    await userEvent.click(screen.getByTestId("mobile-go-live"));
    await userEvent.click(await screen.findByTestId("mobile-stop"));

    await waitFor(() => expect(apiMock.stop).toHaveBeenCalledWith(SESSION_ID));
  });
});

describe("MobileStudio — camera controls", () => {
  it("flips to the rear camera without dropping the broadcast", async () => {
    const { getUserMedia, opened } = installPhone();

    await enterStudio();
    await userEvent.click(screen.getByTestId("mobile-go-live"));
    await waitFor(() => expect(apiMock.start).toHaveBeenCalled());

    await userEvent.click(await screen.findByTestId("mobile-flip"));

    await waitFor(() => {
      const video = getUserMedia.mock.calls.filter(([c]) => c.video).at(-1)?.[0]
        .video as MediaTrackConstraints;
      expect(video.facingMode).toEqual({ ideal: "environment" });
    });

    // The transport is untouched — no new credential, so no new connection. Turning the phone
    // around is not a reason to drop off the show.
    expect(apiMock.issueCredential).toHaveBeenCalledTimes(1);

    // And the camera that was on air is released, rather than left running beside its replacement.
    await waitFor(() => expect(opened[0]?.stop).toHaveBeenCalled());
  });

  it("hides the flip control on a phone with one camera", async () => {
    installPhone({ cameras: 1 });

    await enterStudio();

    expect(screen.queryByTestId("mobile-flip")).not.toBeInTheDocument();
    expect(screen.getByTestId("mobile-camera")).toBeInTheDocument();
  });

  it("mutes without stopping the microphone", async () => {
    const { opened } = installPhone();

    await enterStudio();
    await userEvent.click(screen.getByTestId("mobile-mic"));

    const microphone = opened.find((track) => track.kind === "audio");
    // Disabling keeps the sender alive, so a mute is never read downstream as a device drop.
    expect(microphone?.enabled).toBe(false);
    expect(microphone?.stop).not.toHaveBeenCalled();
  });
});

describe("MobileStudio — interruptions", () => {
  it("re-opens a camera the operating system took during a call", async () => {
    const { getUserMedia, opened } = installPhone();

    await enterStudio();
    const before = getUserMedia.mock.calls.filter(([c]) => c.video).length;

    // iOS does not end the track for a phone call; it mutes it. The broadcast keeps sending the
    // last frame it had, and nothing in the WebRTC stack notices — which is why this is repaired
    // by re-opening rather than by listening for `ended`.
    opened.find((track) => track.kind === "video")?.setMuted(true);

    await act(async () => {
      Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
      document.dispatchEvent(new Event("visibilitychange"));

      Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
      document.dispatchEvent(new Event("visibilitychange"));
    });

    await waitFor(() =>
      expect(getUserMedia.mock.calls.filter(([c]) => c.video).length).toBeGreaterThan(before),
    );
  });

  it("leaves a healthy camera alone when the page comes back", async () => {
    const { getUserMedia } = installPhone();

    await enterStudio();
    const before = getUserMedia.mock.calls.filter(([c]) => c.video).length;

    await act(async () => {
      Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
      document.dispatchEvent(new Event("visibilitychange"));
      // Re-opening a camera that never went away would flash the outgoing video black for nothing.
      await new Promise((resolve) => setTimeout(resolve, 20));
    });

    expect(getUserMedia.mock.calls.filter(([c]) => c.video).length).toBe(before);
  });
});

describe("MobileStudio — refused permission", () => {
  it("says what to do, and offers a way back without a reload", async () => {
    const getUserMedia = vi
      .fn()
      .mockRejectedValue(Object.assign(new Error("denied"), { name: "NotAllowedError" }));

    Object.defineProperty(navigator, "mediaDevices", {
      configurable: true,
      value: {
        getUserMedia,
        // A phone that refused permission still lists its hardware — with the labels hidden, but
        // the entries present. An empty list means something else entirely, and the platform
        // reports it as "no device" rather than as a refusal.
        enumerateDevices: vi.fn().mockResolvedValue([
          { kind: "videoinput", deviceId: "", label: "", groupId: "" },
          { kind: "audioinput", deviceId: "", label: "", groupId: "" },
        ]),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      },
    });
    installWebRtcStubs();

    render(<MobileStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByTestId("mobile-allow"));

    // The recovery for this happens in the operating system's settings, so the screen has to say
    // so — and then still be usable when they come back.
    expect(await screen.findByText(/blocked/i)).toBeInTheDocument();

    const retry = await screen.findByTestId("mobile-retry-access");
    await userEvent.click(retry);

    expect(getUserMedia.mock.calls.length).toBeGreaterThan(2);
  });
});

describe("MobileStudio — settings", () => {
  it("offers the quality rungs and explains the starting one", async () => {
    installPhone();

    await enterStudio();
    await userEvent.click(screen.getByTestId("mobile-details"));

    expect(await screen.findByTestId("mobile-sheet")).toBeInTheDocument();
    expect(screen.getByTestId("mobile-quality-720p")).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByTestId("mobile-quality-1080p")).toBeInTheDocument();
  });

  it("re-opens the camera at a chosen rung", async () => {
    const { getUserMedia } = installPhone();

    await enterStudio();
    await userEvent.click(screen.getByTestId("mobile-details"));
    await userEvent.click(await screen.findByTestId("mobile-quality-540p"));

    await waitFor(() => {
      const video = getUserMedia.mock.calls.filter(([c]) => c.video).at(-1)?.[0]
        .video as MediaTrackConstraints;
      expect(video.width).toEqual({ ideal: 960 });
      // Still 30: the rung changed, the reason for capping the frame rate did not.
      expect(video.frameRate).toEqual({ ideal: 30 });
    });
  });
});

describe("MobileStudio — ended session", () => {
  it("points at the playback page and stops the camera", async () => {
    const { opened } = installPhone();
    apiMock.status.mockResolvedValue(buildStatus("ENDED", { endedAt: new Date().toISOString() }));
    apiMock.get.mockResolvedValue(buildSession({ status: "ENDED" }));

    render(<MobileStudio sessionId={SESSION_ID} />);

    expect(await screen.findByRole("link", { name: /playback page/i })).toBeInTheDocument();
    // Nothing should still be holding the camera once the session is over.
    expect(opened.every((track) => track.stop)).toBe(true);
  });
});
