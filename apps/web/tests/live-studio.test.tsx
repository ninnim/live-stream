import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { installWebAudioStub } from "./support/fake-web-audio";
import { FakeRTCPeerConnection, installWebRtcStubs } from "./support/fake-webrtc";
import { LiveStudio } from "@/components/studio/LiveStudio";
import type { LiveSession, LiveSessionStatus, LiveSessionStatusPayload } from "@/lib/types";

// The realtime channel needs a live server; the studio's REST path is the source of truth, and it
// is exercised for real here.
vi.mock("@/lib/realtime/live-hub", () => ({
  LiveHubClient: class {
    connect = vi.fn().mockResolvedValue(undefined);
    disconnect = vi.fn().mockResolvedValue(undefined);
    isConnected = false;
  },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn() }),
}));

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

// The studio also renders its device panel. Stubbed empty for the same reason as destinations:
// these tests are about the broadcast lifecycle, and a real request here would churn state
// underneath the interactions being asserted.
vi.mock("@/lib/api/devices", () => ({
  sourceApi: {
    list: vi.fn().mockResolvedValue([]),
    invite: vi.fn(),
    rename: vi.fn(),
    revoke: vi.fn(),
    preview: vi.fn(),
  },
  deviceApi: { claim: vi.fn(), session: vi.fn(), issueCredential: vi.fn() },
  deviceTokenStore: { get: vi.fn(), save: vi.fn(), clear: vi.fn() },
}));

// The studio also renders its AI panel. Reported as unconfigured, which is the default for a
// deployment with no provider — and keeps these tests about the broadcast lifecycle.
vi.mock("@/lib/api/ai", () => ({
  aiApi: {
    capabilities: vi.fn().mockResolvedValue({ configured: false, features: [] }),
    jobs: vi.fn().mockResolvedValue([]),
    request: vi.fn(),
    cancel: vi.fn(),
  },
}));

// The studio also renders its distribution panel. These tests are about the broadcast lifecycle,
// so the destination API is stubbed empty rather than left to make real network calls.
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

const SESSION_ID = "11111111-1111-1111-1111-111111111111";

function buildSession(overrides: Partial<LiveSession> = {}): LiveSession {
  return {
    id: SESSION_ID,
    workspaceId: "workspace-1",
    title: "Product launch",
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

/**
 * A session that is live *and healthy*.
 *
 * The distinction matters: a LIVE session reporting `ingestConnected: false` is the reloaded-studio
 * case, and the studio correctly responds by re-acquiring media and republishing. A test that means
 * "we are broadcasting normally" must say so, or it races that recovery.
 */
function buildLiveSession(): LiveSession {
  const session = buildSession({ status: "LIVE" });

  return {
    ...session,
    health: { ...session.health, status: "GOOD", ingestConnected: true, bitrateKbps: 2500, viewerCount: 4 },
  };
}

function buildStatus(status: LiveSessionStatus, overrides: Partial<LiveSessionStatusPayload> = {}) {
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

/**
 * A capture track.
 *
 * `addEventListener` is not decoration: the studio listens for `ended` to notice a camera being
 * unplugged, and a fixture without it would fail on a code path every real track supports.
 */
interface TestTrack extends MediaStreamTrack {
  /** Test hook: fire a track event, which is how a browser reports hardware going away. */
  emit(type: string): void;
}

function fakeTrack(kind: "video" | "audio", deviceId = `${kind}-1`): TestTrack {
  const listeners = new Map<string, Set<EventListenerOrEventListenerObject>>();

  return {
    kind,
    enabled: true,
    contentHint: "",
    applyConstraints: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn(),
    getSettings: () => ({ deviceId, width: 1280, height: 720, frameRate: 30 }),
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
  } as unknown as TestTrack;
}

function trackStream(...tracks: MediaStreamTrack[]) {
  return {
    getTracks: () => tracks,
    getVideoTracks: () => tracks.filter((track) => track.kind === "video"),
    getAudioTracks: () => tracks.filter((track) => track.kind === "audio"),
  } as unknown as MediaStream;
}

function singleTrackStream(track: MediaStreamTrack) {
  return trackStream(track);
}

function installGrantedMediaDevices(extraDevices: MediaDeviceInfo[] = []) {
  // The studio asks for video and audio separately so that changing camera never re-opens the
  // microphone, so this has to answer per-kind rather than always handing back both.
  const getUserMedia = vi.fn((constraints: MediaStreamConstraints) => {
    const kind = constraints.video ? "video" : "audio";
    const requested = (constraints[kind] as MediaTrackConstraints | undefined)?.deviceId as
      | { exact?: string }
      | undefined;

    return Promise.resolve(singleTrackStream(fakeTrack(kind, requested?.exact ?? `${kind}-1`)));
  });

  Object.defineProperty(navigator, "mediaDevices", {
    configurable: true,
    value: {
      getUserMedia,
      enumerateDevices: vi.fn().mockResolvedValue([
        { kind: "videoinput", deviceId: "video-1", label: "Built-in camera", groupId: "g1" },
        { kind: "audioinput", deviceId: "audio-1", label: "Built-in microphone", groupId: "g1" },
        ...extraDevices,
      ]),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    },
  });

  installWebRtcStubs();
  return getUserMedia;
}

/**
 * A machine with no webcam — the ordinary case this fixture exists for. The microphone is
 * present, and a screen can be shared.
 */
function installNoCameraMediaDevices() {
  // Held so a test can end it the way the browser's own "Stop sharing" bar does.
  const screenTrack = fakeTrack("video", "screen-1");

  const getUserMedia = vi.fn((constraints: MediaStreamConstraints) => {
    if (constraints.video) {
      return Promise.reject(Object.assign(new Error("no camera"), { name: "NotFoundError" }));
    }

    return Promise.resolve(singleTrackStream(fakeTrack("audio")));
  });

  Object.defineProperty(navigator, "mediaDevices", {
    configurable: true,
    value: {
      getUserMedia,
      getDisplayMedia: vi.fn().mockResolvedValue(singleTrackStream(screenTrack)),
      enumerateDevices: vi
        .fn()
        .mockResolvedValue([{ kind: "audioinput", deviceId: "audio-1", label: "Headset", groupId: "g1" }]),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    },
  });

  installWebRtcStubs();
  return { getUserMedia, screenTrack };
}

/**
 * A microphone that is present but momentarily unavailable — the reloaded-studio case, where
 * the previous page has not finished letting go of the device.
 */
function installBusyMicrophoneMediaDevices() {
  let attempts = 0;

  const getUserMedia = vi.fn((constraints: MediaStreamConstraints) => {
    if (constraints.video) {
      return Promise.resolve(singleTrackStream(fakeTrack("video")));
    }

    attempts += 1;
    return attempts === 1
      ? Promise.reject(Object.assign(new Error("busy"), { name: "NotReadableError" }))
      : Promise.resolve(singleTrackStream(fakeTrack("audio")));
  });

  Object.defineProperty(navigator, "mediaDevices", {
    configurable: true,
    value: {
      getUserMedia,
      enumerateDevices: vi.fn().mockResolvedValue([
        { kind: "videoinput", deviceId: "video-1", label: "Built-in camera", groupId: "g1" },
        { kind: "audioinput", deviceId: "audio-1", label: "Built-in microphone", groupId: "g1" },
      ]),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    },
  });

  installWebRtcStubs();
  return () => attempts;
}

/** A machine with neither device: no webcam, no microphone. Screen sharing is the only way in. */
function installNoDeviceMediaDevices() {
  Object.defineProperty(navigator, "mediaDevices", {
    configurable: true,
    value: {
      getUserMedia: vi
        .fn()
        .mockRejectedValue(Object.assign(new Error("none"), { name: "NotFoundError" })),
      getDisplayMedia: vi.fn().mockResolvedValue(singleTrackStream(fakeTrack("video", "screen-1"))),
      enumerateDevices: vi.fn().mockResolvedValue([]),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    },
  });

  installWebRtcStubs();
}

function installDeniedMediaDevices() {
  Object.defineProperty(navigator, "mediaDevices", {
    configurable: true,
    value: {
      getUserMedia: vi
        .fn()
        .mockRejectedValue(Object.assign(new Error("denied"), { name: "NotAllowedError" })),
      // Hardware present, prompt blocked — the case this fixture is meant to represent.
      enumerateDevices: vi.fn().mockResolvedValue([
        { kind: "videoinput", deviceId: "video-1", label: "Built-in camera" },
        { kind: "audioinput", deviceId: "audio-1", label: "Built-in microphone" },
      ]),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    },
  });

  installWebRtcStubs();
}

beforeEach(() => {
  vi.clearAllMocks();
  apiMock.get.mockResolvedValue(buildSession());
  apiMock.status.mockResolvedValue(buildStatus("DRAFT"));
  apiMock.prepare.mockResolvedValue(buildSession({ status: "READY" }));
  apiMock.issueCredential.mockResolvedValue({
    protocol: "WHIP",
    ingestUrl: "https://media.test/ls_abc/whip",
    token: "short-lived-token",
    expiresAt: new Date(Date.now() + 300_000).toISOString(),
    expiresInSeconds: 300,
  });
  apiMock.reportSignal.mockResolvedValue(undefined);
});

describe("LiveStudio — permissions", () => {
  it("asks for camera and microphone access before showing controls", async () => {
    installGrantedMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);

    expect(
      await screen.findByRole("button", { name: /use camera and microphone/i }),
    ).toBeInTheDocument();
  });

  it("explains what to do when permission is refused", async () => {
    installDeniedMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(/blocked/i);
    // The banner must say how to recover, not just that it failed.
    expect(alert).toHaveTextContent(/allow camera access/i);
  });

  it("shows the device pickers once access is granted", async () => {
    installGrantedMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    expect(await screen.findByLabelText("Camera")).toBeInTheDocument();
    expect(await screen.findByLabelText("Microphone")).toBeInTheDocument();
  });
});

describe("LiveStudio — device controls", () => {
  it("toggles the camera without stopping the track", async () => {
    const getUserMedia = installGrantedMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    const toggle = await screen.findByRole("button", { name: /camera/i, pressed: true });
    await userEvent.click(toggle);

    const videoStream = (await getUserMedia.mock.results[0]?.value) as MediaStream;
    const videoTrack = videoStream.getVideoTracks()[0];

    // Disabling rather than stopping keeps the transport alive, so a mute is not read as a drop.
    expect(videoTrack?.enabled).toBe(false);
    expect(videoTrack?.stop).not.toHaveBeenCalled();
  });

  it("lists the available cameras", async () => {
    installGrantedMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    const select = (await screen.findByLabelText("Camera")) as HTMLSelectElement;
    expect(select.options).toHaveLength(1);
    expect(select.options[0]?.textContent).toBe("Built-in camera");
  });

  it("groups a camera plugged in by cable apart from the built-in one", async () => {
    // Someone who has just connected a capture card is looking for it; burying it in an unsorted
    // list is the difference between the feature existing and being found.
    installGrantedMediaDevices([
      {
        kind: "videoinput",
        deviceId: "video-2",
        label: "Cam Link 4K (0fd9:0066)",
        groupId: "g2",
      } as MediaDeviceInfo,
    ]);

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    const select = (await screen.findByLabelText("Camera")) as HTMLSelectElement;
    const groups = Array.from(select.querySelectorAll("optgroup")).map((group) => group.label);

    expect(groups).toEqual(["Connected by cable", "Built-in"]);
  });

  it("puts a screen on air without restarting the stream", async () => {
    const screenTrack = fakeTrack("video", "screen-1");
    installGrantedMediaDevices();
    (navigator.mediaDevices as unknown as { getDisplayMedia: unknown }).getDisplayMedia = vi
      .fn()
      .mockResolvedValue(singleTrackStream(screenTrack));

    apiMock.start.mockResolvedValue(
      buildStatus("LIVE", { startedAt: new Date().toISOString(), version: 2 }),
    );

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start live/i }));
    await waitFor(() => expect(apiMock.start).toHaveBeenCalled());

    const credentialsBefore = apiMock.issueCredential.mock.calls.length;
    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));

    const videoSender = FakeRTCPeerConnection.senders.find((sender) => sender.track?.kind === "video");
    await waitFor(() => expect(videoSender?.replaceTrack).toHaveBeenCalledWith(screenTrack));
    expect(apiMock.issueCredential.mock.calls).toHaveLength(credentialsBefore);

    // Screen content is graded for legibility rather than motion: blurred text is unreadable, a
    // slide arriving a frame late is not.
    expect(screenTrack.contentHint).toBe("detail");
    expect(await screen.findByRole("button", { name: /stop sharing/i })).toBeInTheDocument();
  });

  it("returns to camera when the browser's own stop-sharing bar ends the track", async () => {
    // The studio has no way to observe that bar being pressed except the track ending, so this is
    // the only path back — and leaving a dead track on air would freeze the broadcast.
    const screenTrack = fakeTrack("video", "screen-1");
    installGrantedMediaDevices();
    (navigator.mediaDevices as unknown as { getDisplayMedia: unknown }).getDisplayMedia = vi
      .fn()
      .mockResolvedValue(singleTrackStream(screenTrack));

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));
    await screen.findByRole("button", { name: /stop sharing/i });

    screenTrack.emit("ended");

    expect(await screen.findByRole("button", { name: /share screen/i })).toBeInTheDocument();
    expect(await screen.findByRole("status")).toHaveTextContent(/back on camera/i);

    // The finished track must leave the preview stream with it, or the studio ends up holding two
    // video tracks on a stream meant to carry one.
    await waitFor(() => expect(screenTrack.stop).toHaveBeenCalled());
  });

  it("changes camera mid-broadcast without restarting the stream", async () => {
    // The headline behaviour of this panel. A device change that tore the transport down would
    // show the audience a dropout, which is why the picker used to be disabled while live at all.
    installGrantedMediaDevices([
      {
        kind: "videoinput",
        deviceId: "video-2",
        label: "Cam Link 4K (0fd9:0066)",
        groupId: "g2",
      } as MediaDeviceInfo,
    ]);
    apiMock.start.mockResolvedValue(
      buildStatus("LIVE", { startedAt: new Date().toISOString(), version: 2 }),
    );

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start live/i }));
    await waitFor(() => expect(apiMock.start).toHaveBeenCalled());

    const credentialsBefore = apiMock.issueCredential.mock.calls.length;
    await userEvent.selectOptions(await screen.findByLabelText("Camera"), "video-2");

    const videoSender = FakeRTCPeerConnection.senders.find((sender) => sender.track?.kind === "video");
    await waitFor(() => expect(videoSender?.replaceTrack).toHaveBeenCalled());

    // No new credential means no new connection: the swap happened inside the transport already
    // open, which is exactly what `replaceTrack` buys.
    expect(apiMock.issueCredential.mock.calls).toHaveLength(credentialsBefore);
    expect(videoSender?.replaceTrack.mock.calls[0]?.[0]?.getSettings().deviceId).toBe("video-2");
  });
});

describe("LiveStudio — start and stop", () => {
  it("prepares, publishes and starts when Start Live is pressed", async () => {
    installGrantedMediaDevices();
    apiMock.start.mockResolvedValue(
      buildStatus("LIVE", { startedAt: new Date().toISOString(), version: 2 }),
    );

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start live/i }));

    await waitFor(() => expect(apiMock.prepare).toHaveBeenCalledWith(SESSION_ID));
    await waitFor(() => expect(apiMock.issueCredential).toHaveBeenCalledWith(SESSION_ID));
    await waitFor(() => expect(apiMock.start).toHaveBeenCalledWith(SESSION_ID));
  });

  it("surfaces a friendly message when starting fails", async () => {
    installGrantedMediaDevices();
    apiMock.prepare.mockRejectedValue(new Error("gateway down"));

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start live/i }));

    const alerts = await screen.findAllByRole("alert");
    expect(alerts.some((a) => /could not start your broadcast/i.test(a.textContent ?? ""))).toBe(true);
  });

  it("offers Stop Live while broadcasting", async () => {
    installGrantedMediaDevices();
    apiMock.status.mockResolvedValue(
      buildStatus("LIVE", { startedAt: new Date().toISOString(), version: 3 }),
    );
    apiMock.get.mockResolvedValue(buildLiveSession());

    render(<LiveStudio sessionId={SESSION_ID} />);

    expect(await screen.findByRole("button", { name: /stop live/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /start live/i })).not.toBeInTheDocument();
  });

  it("stops the broadcast when Stop Live is pressed", async () => {
    installGrantedMediaDevices();
    apiMock.status.mockResolvedValue(
      buildStatus("LIVE", { startedAt: new Date().toISOString(), version: 3 }),
    );
    apiMock.get.mockResolvedValue(buildLiveSession());
    apiMock.stop.mockResolvedValue(buildStatus("ENDED", { version: 4 }));

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /stop live/i }));

    await waitFor(() => expect(apiMock.stop).toHaveBeenCalledWith(SESSION_ID));
  });
});

describe("LiveStudio — live state presentation", () => {
  it("shows the LIVE indicator and health read-out while broadcasting", async () => {
    installGrantedMediaDevices();
    apiMock.status.mockResolvedValue(
      buildStatus("LIVE", { startedAt: new Date().toISOString(), version: 3 }),
    );
    apiMock.get.mockResolvedValue(
      buildSession({
        status: "LIVE",
        health: {
          status: "GOOD",
          ingestConnected: true,
          bitrateKbps: 2400,
          videoFps: 30,
          viewerCount: 124,
          reconnectCount: 0,
          lastIngestAt: new Date().toISOString(),
          recoveryWindowSecondsRemaining: null,
          observedAt: new Date().toISOString(),
          lastErrorCode: null,
        },
      }),
    );

    render(<LiveStudio sessionId={SESSION_ID} />);

    expect(await screen.findAllByText("LIVE")).not.toHaveLength(0);
    expect(await screen.findByText("124")).toBeInTheDocument();
    expect(await screen.findByText("Good")).toBeInTheDocument();
    expect(await screen.findByText("2.4 Mbps")).toBeInTheDocument();
  });

  it("does not claim LIVE while the server is still starting", async () => {
    // The studio must reflect the server's state, never assume it (docs/04 safety behaviour).
    installGrantedMediaDevices();
    apiMock.status.mockResolvedValue(buildStatus("STARTING", { version: 2 }));
    apiMock.get.mockResolvedValue(buildSession({ status: "STARTING" }));

    render(<LiveStudio sessionId={SESSION_ID} />);

    expect(await screen.findByText(/waiting for the streaming server/i)).toBeInTheDocument();
    expect(screen.queryByText("LIVE")).not.toBeInTheDocument();
  });

  it("explains that the session is held open while reconnecting", async () => {
    installGrantedMediaDevices();
    apiMock.status.mockResolvedValue(
      buildStatus("RECONNECTING", { startedAt: new Date().toISOString(), version: 4 }),
    );
    apiMock.get.mockResolvedValue(
      buildSession({
        status: "RECONNECTING",
        health: {
          status: "POOR",
          ingestConnected: false,
          bitrateKbps: null,
          videoFps: null,
          viewerCount: 0,
          reconnectCount: 1,
          lastIngestAt: null,
          recoveryWindowSecondsRemaining: 95,
          observedAt: new Date().toISOString(),
          lastErrorCode: "LIVE_005_SOURCE_DISCONNECTED",
        },
      }),
    );

    render(<LiveStudio sessionId={SESSION_ID} />);

    const status = await screen.findByRole("status");
    expect(status).toHaveTextContent(/reconnecting/i);
    expect(status).toHaveTextContent(/held open/i);
    expect(status).toHaveTextContent(/95s/);
  });

  it("reports a failed session with its reason", async () => {
    installGrantedMediaDevices();
    apiMock.status.mockResolvedValue(buildStatus("FAILED", { version: 5 }));
    apiMock.get.mockResolvedValue(
      buildSession({
        status: "FAILED",
        lastErrorCode: "LIVE_005_SOURCE_DISCONNECTED",
        lastErrorMessage: "The broadcaster did not reconnect within the recovery window.",
      }),
    );

    render(<LiveStudio sessionId={SESSION_ID} />);

    const alerts = await screen.findAllByRole("alert");
    expect(
      alerts.some((a) => /did not reconnect within the recovery window/i.test(a.textContent ?? "")),
    ).toBe(true);
  });

  it("offers the playback page once a session has ended", async () => {
    installGrantedMediaDevices();
    apiMock.status.mockResolvedValue(buildStatus("ENDED", { version: 6 }));
    apiMock.get.mockResolvedValue(buildSession({ status: "ENDED" }));

    render(<LiveStudio sessionId={SESSION_ID} />);

    expect(await screen.findByRole("link", { name: /view playback page/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /start live/i })).not.toBeInTheDocument();
  });
});

/**
 * A camera and a microphone are both optional (docs/04-native-broadcasting.md).
 *
 * The rule these defend: **one working device is enough to broadcast.** A machine with no webcam is
 * an ordinary machine, and the studio has to open on it — a screen and a voice are a broadcast.
 */
describe("LiveStudio without a camera", () => {
  beforeEach(() => {
    apiMock.status.mockResolvedValue(buildStatus("DRAFT"));
    apiMock.get.mockResolvedValue(buildSession());
  });

  it("opens on the microphone alone and says why there is no picture", async () => {
    installNoCameraMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);

    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    // In the studio, not stuck on the prompt.
    expect(await screen.findByRole("button", { name: "Start Live" })).toBeEnabled();
    // Matched on the prompt's own sentence: the notice above it says "no camera on this
    // machine" too, and an ambiguous match would pass whichever one happened to render.
    expect(await screen.findByText(/give your broadcast a picture/i)).toBeInTheDocument();
  });

  it("does not present a missing camera as an error", async () => {
    // Nothing is broken. Reporting it as a fault sends somebody looking for a hardware problem
    // they do not have.
    installNoCameraMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    await screen.findByText(/give your broadcast a picture/i);
    const alerts = screen.queryAllByRole("alert");
    expect(alerts.some((alert) => /camera/i.test(alert.textContent ?? ""))).toBe(false);
  });

  it("shows the camera controls as unavailable rather than pretending they work", async () => {
    installNoCameraMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    await screen.findByText(/give your broadcast a picture/i);
    expect(screen.getByRole("combobox", { name: "Camera" })).toBeDisabled();
    expect(screen.getByRole("button", { name: /None\s*Camera/i })).toBeDisabled();
  });

  it("enters the studio by sharing a screen, without ever opening a camera", async () => {
    installNoDeviceMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);

    await userEvent.click(await screen.findByRole("button", { name: /share a screen instead/i }));

    expect(await screen.findByRole("button", { name: "Start Live" })).toBeEnabled();
  });

  it("refuses to go live with nothing to send", async () => {
    // A session started with no tracks would sit in STARTING until the server timed it out, which
    // reads as a platform fault rather than as a machine with no devices.
    installNoDeviceMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);

    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(/No camera or microphone was found/i);

    // The control stays visible and disabled rather than vanishing: the operator needs to see
    // that going live is the thing being refused, and why.
    expect(screen.getByRole("button", { name: "Start Live" })).toBeDisabled();
  });

  it("keeps the broadcast going when a shared screen ends with no camera behind it", async () => {
    const { screenTrack } = installNoCameraMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    await screen.findByText(/give your broadcast a picture/i);
    await userEvent.click(screen.getByRole("button", { name: "Share screen" }));

    // Sharing gave the broadcast a picture, so the prompt goes away.
    await waitFor(() =>
      expect(screen.queryByText(/give your broadcast a picture/i)).not.toBeInTheDocument(),
    );

    // The browser's own "Stop sharing" bar ends the track. With no camera to fall back to, the
    // studio drops the video and carries on with sound rather than reporting a failure.
    screenTrack.emit("ended");

    // Matched on the prompt's own sentence: the notice above it says "no camera on this
    // machine" too, and an ambiguous match would pass whichever one happened to render.
    expect(await screen.findByText(/give your broadcast a picture/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Start Live" })).toBeEnabled();
    // Scoped to media: this studio also renders panels whose own APIs are stubbed out, and a
    // blanket no-alert assertion would be about those rather than about the screen ending.
    const alerts = screen.queryAllByRole("alert");
    expect(alerts.some((alert) => /camera|screen/i.test(alert.textContent ?? ""))).toBe(false);
  });

  it("retries a device the machine has but that would not open", async () => {
    // Regression guard. A reloaded studio races its own previous page for the microphone, and
    // Chromium answers the first request with "device in use". Accepting that as "no microphone"
    // silently drops the sound for the rest of the broadcast — and, because the resume path then
    // republishes without audio, cost a reloaded session its recovery entirely.
    const attempts = installBusyMicrophoneMediaDevices();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    await waitFor(() => expect(attempts()).toBe(2));

    expect(await screen.findByRole("button", { name: "Start Live" })).toBeEnabled();
    expect(screen.queryByText(/No microphone found/i)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /On\s*Microphone/i })).toBeEnabled();
  });
});

/**
 * Screen capture is graded for what it carries (docs/decisions/0016-optional-capture-devices.md).
 *
 * 30 fps kept sharp for slides, 60 fps kept smooth for anything that moves. The two trades are
 * genuinely opposed, so the operator chooses rather than the platform guessing.
 */
describe("LiveStudio screen capture", () => {
  beforeEach(() => {
    apiMock.status.mockResolvedValue(buildStatus("DRAFT"));
    apiMock.get.mockResolvedValue(buildSession());
  });

  it("shares at 60fps once the screen is set to gameplay", async () => {
    installGrantedMediaDevices();
    const getDisplayMedia = vi.fn().mockResolvedValue(singleTrackStream(fakeTrack("video", "screen-1")));
    (navigator.mediaDevices as unknown as { getDisplayMedia: unknown }).getDisplayMedia = getDisplayMedia;

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: "Share screen" }));

    // Sharing starts on the safer guess, and the control appears in place of the camera rungs.
    await screen.findByRole("button", { name: /stop sharing/i });
    expect(getDisplayMedia).toHaveBeenLastCalledWith(
      expect.objectContaining({ video: { frameRate: { ideal: 30 } } }),
    );

    await userEvent.selectOptions(screen.getByLabelText(/Screen content/i), "gameplay");

    await waitFor(() =>
      expect(screen.getByLabelText(/Screen content/i)).toHaveValue("gameplay"),
    );
  });

  it("re-constrains a live share instead of putting the picker back in front of you", async () => {
    // Re-capturing to change frame rate would interrupt a broadcast with a second permission
    // dialog. Chrome honours applyConstraints on a display track, so the track is re-constrained.
    installGrantedMediaDevices();
    const screenTrack = fakeTrack("video", "screen-1");
    const getDisplayMedia = vi.fn().mockResolvedValue(singleTrackStream(screenTrack));
    (navigator.mediaDevices as unknown as { getDisplayMedia: unknown }).getDisplayMedia = getDisplayMedia;

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: "Share screen" }));
    await screen.findByRole("button", { name: /stop sharing/i });

    await userEvent.selectOptions(screen.getByLabelText(/Screen content/i), "gameplay");

    await waitFor(() =>
      expect(screenTrack.applyConstraints).toHaveBeenCalledWith({ frameRate: { ideal: 60 } }),
    );
    expect(getDisplayMedia).toHaveBeenCalledTimes(1);
    expect(screenTrack.contentHint).toBe("motion");
  });

  it("does not drag a live share back to the webcam when quality changes", async () => {
    // Regression guard. The quality rungs are camera resolutions and a shared display has none, so
    // re-opening the camera here would take the screen off air mid-broadcast.
    const getUserMedia = installGrantedMediaDevices();
    (navigator.mediaDevices as unknown as { getDisplayMedia: unknown }).getDisplayMedia = vi
      .fn()
      .mockResolvedValue(singleTrackStream(fakeTrack("video", "screen-1")));

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: "Share screen" }));
    await screen.findByRole("button", { name: /stop sharing/i });

    const callsBefore = getUserMedia.mock.calls.length;

    // The rungs are not even offered while sharing — the slot carries the screen setting instead.
    expect(screen.queryByLabelText("Quality")).not.toBeInTheDocument();
    expect(getUserMedia.mock.calls.length).toBe(callsBefore);
    expect(screen.getByRole("button", { name: /stop sharing/i })).toBeInTheDocument();
  });
});

/**
 * Game sound and voice both reach the broadcast (docs/decisions/0017-broadcast-quality.md).
 *
 * The publisher sends exactly one audio track, so the two have to be summed before they get there.
 * These tests are about what actually goes out, which is the part that was silently wrong: sharing
 * a screen used to discard its sound entirely.
 */
describe("LiveStudio capture audio", () => {
  beforeEach(() => {
    apiMock.status.mockResolvedValue(buildStatus("DRAFT"));
    apiMock.get.mockResolvedValue(buildSession());
  });

  /** A share that carries the screen's sound, which is what "Share system audio" produces. */
  function shareWithAudio() {
    const video = fakeTrack("video", "screen-1");
    const audio = fakeTrack("audio", "screen-audio");
    installGrantedMediaDevices();
    (navigator.mediaDevices as unknown as { getDisplayMedia: unknown }).getDisplayMedia = vi
      .fn()
      .mockResolvedValue(trackStream(video, audio));

    return { video, audio };
  }

  it("publishes the game and the voice summed into one track", async () => {
    const webAudio = installWebAudioStub();
    const { audio } = shareWithAudio();
    apiMock.start.mockResolvedValue(
      buildStatus("LIVE", { startedAt: new Date().toISOString(), version: 2 }),
    );

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start live/i }));
    await waitFor(() => expect(apiMock.start).toHaveBeenCalled());

    const audioSender = FakeRTCPeerConnection.senders.find((sender) => sender.track?.kind === "audio");
    const microphone = audioSender?.track;

    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));
    await screen.findByRole("button", { name: /stop sharing/i });

    // The mix goes out in place of the bare microphone, through the transport already open — a
    // second permission dialog or a reconnection here would be a dropout for everyone watching.
    await waitFor(() => expect(audioSender?.replaceTrack).toHaveBeenCalledWith(webAudio.mixedTrack));
    expect(webAudio.mixedTrack).not.toBe(microphone);
    expect(webAudio.mixedTrack).not.toBe(audio);
    expect(apiMock.issueCredential.mock.calls).toHaveLength(1);
  });

  it("starts the game below the voice", async () => {
    // Mixed at unity a game buries the person playing it, and the broadcaster cannot hear that
    // it is happening. The default should be the balance they would have chosen anyway.
    const webAudio = installWebAudioStub();
    shareWithAudio();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));
    await screen.findByRole("button", { name: /stop sharing/i });

    await waitFor(() => expect(webAudio.gains).toHaveLength(2));

    const [microphone, game] = webAudio.gains;
    expect(game!.gain.value).toBeLessThan(microphone!.gain.value);
  });

  it("rebalances a live mix without swapping the track being published", async () => {
    const webAudio = installWebAudioStub();
    shareWithAudio();
    apiMock.start.mockResolvedValue(
      buildStatus("LIVE", { startedAt: new Date().toISOString(), version: 2 }),
    );

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start live/i }));
    await waitFor(() => expect(apiMock.start).toHaveBeenCalled());
    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));

    const fader = await screen.findByRole("slider", { name: "Screen sound" });
    const audioSender = FakeRTCPeerConnection.senders.find((sender) => sender.track?.kind === "audio");
    await waitFor(() => expect(audioSender?.replaceTrack).toHaveBeenCalledWith(webAudio.mixedTrack));
    const swapsBefore = audioSender?.replaceTrack.mock.calls.length ?? 0;

    fireEvent.change(fader, { target: { value: "20" } });

    // A gain change inside a graph whose output track stays put. Re-publishing per slider tick
    // would be audible, and the slider moves a lot.
    await waitFor(() => expect(webAudio.gains.at(-1)?.gain.value).toBeCloseTo(0.2));
    expect(audioSender?.replaceTrack.mock.calls).toHaveLength(swapsBefore);
  });

  it("says how to include the screen's sound when the sharer left it out", async () => {
    // Most sharers do not tick the box, and the sound going missing is otherwise invisible until
    // somebody watching says so.
    installGrantedMediaDevices();
    (navigator.mediaDevices as unknown as { getDisplayMedia: unknown }).getDisplayMedia = vi
      .fn()
      .mockResolvedValue(singleTrackStream(fakeTrack("video", "screen-1")));

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));

    expect(await screen.findByRole("status")).toHaveTextContent(/share again and tick/i);
    expect(screen.queryByRole("slider", { name: "Screen sound" })).not.toBeInTheDocument();
  });

  it("goes back to the microphone alone when the share ends", async () => {
    const webAudio = installWebAudioStub();
    const { video, audio } = shareWithAudio();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));
    await screen.findByRole("slider", { name: "Screen sound" });

    // The browser's own sharing bar, which is how most shares actually end.
    video.emit("ended");

    await waitFor(() => expect(screen.queryByRole("slider", { name: "Screen sound" })).not.toBeInTheDocument());

    // Both must happen: a share that stops being heard but keeps the tab's audio captured leaves
    // the browser's "sharing" indicator lit, and the graph running.
    await waitFor(() => expect(audio.stop).toHaveBeenCalled());
    expect(webAudio.closed).toBeGreaterThan(0);
  });

  it("keeps the game audible when the microphone is muted", async () => {
    // Stepping away from the mic should silence the speaker, not the broadcast.
    const webAudio = installWebAudioStub();
    shareWithAudio();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));
    await screen.findByRole("slider", { name: "Screen sound" });

    await userEvent.click(screen.getByRole("button", { name: /On\s*Microphone/i }));

    expect(await screen.findByRole("button", { name: /Off\s*Microphone/i })).toBeInTheDocument();

    // Muting disables the microphone track, which feeds the mixer silence. The mix itself keeps
    // running and the published track is never swapped.
    expect(webAudio.closed).toBe(0);
  });

  it("keeps the game on air when the last microphone is unplugged", async () => {
    installWebAudioStub();
    const { audio } = shareWithAudio();

    // Unplugging is two signals, in this order: the device list changes, then the track ends.
    // Both are needed — the studio reads the list to decide whether there is anything to fall
    // back to, and reads it before the fallback rather than after.
    const media = navigator.mediaDevices as unknown as {
      enumerateDevices: ReturnType<typeof vi.fn>;
      addEventListener: ReturnType<typeof vi.fn>;
    };
    const microphones: MediaStreamTrack[] = [];
    const getUserMedia = media as unknown as { getUserMedia: ReturnType<typeof vi.fn> };
    getUserMedia.getUserMedia.mockImplementation((constraints: MediaStreamConstraints) => {
      const kind = constraints.video ? "video" : "audio";
      const track = fakeTrack(kind);
      if (kind === "audio") microphones.push(track);
      return Promise.resolve(singleTrackStream(track));
    });

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /share screen/i }));
    await screen.findByRole("slider", { name: "Screen sound" });

    media.enumerateDevices.mockResolvedValue([
      { kind: "videoinput", deviceId: "video-1", label: "Built-in camera", groupId: "g1" },
    ]);
    const onDeviceChange = media.addEventListener.mock.calls.find(
      ([type]) => type === "devicechange",
    )?.[1] as () => void;
    onDeviceChange();

    await waitFor(() => expect(microphones).toHaveLength(1));
    (microphones[0] as TestTrack).emit("ended");

    // Sound continues on the screen alone, and the notice says so rather than announcing a
    // silent broadcast that is not silent.
    expect(await screen.findByRole("status")).toHaveTextContent(/screen's sound is still going out/i);
    expect(audio.stop).not.toHaveBeenCalled();
    expect(screen.getByRole("slider", { name: "Screen sound" })).toBeInTheDocument();
  });
});

/**
 * The microphone, made audible to the person using it.
 *
 * Every other audio control in this studio reports what was *asked for* — a device selected, a
 * track unmuted, a mode chosen. A microphone can be all three and stone silent, and before this the
 * broadcaster found out when somebody watching told them.
 */
describe("LiveStudio microphone", () => {
  beforeEach(() => {
    apiMock.status.mockResolvedValue(buildStatus("DRAFT"));
    apiMock.get.mockResolvedValue(buildSession());
  });

  async function openStudio() {
    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
  }

  function meter() {
    return screen.getByRole("meter", { name: /Your microphone level/i });
  }

  it("says a working microphone is good", async () => {
    const webAudio = installWebAudioStub();
    installGrantedMediaDevices();
    // A square wave at ±0.25 — about -12 dBFS, where a voice should sit.
    webAudio.signal = [0.25, -0.25];

    await openStudio();

    await waitFor(() => expect(meter()).toHaveAttribute("aria-valuetext", "Good"));
  });

  it("says so when nothing is reaching the microphone", async () => {
    // The failure this exists for: the device is selected, the track is unmuted, and the studio
    // would happily report "Microphone: On" while broadcasting silence.
    const webAudio = installWebAudioStub();
    installGrantedMediaDevices();
    webAudio.signal = [0];

    await openStudio();

    await waitFor(() => expect(meter()).toHaveAttribute("aria-valuetext", "No sound"));
    expect(await screen.findByText(/not muted on the device itself/i)).toBeInTheDocument();
  });

  it("says so when the microphone is too quiet to hear", async () => {
    const webAudio = installWebAudioStub();
    installGrantedMediaDevices();
    webAudio.signal = [0.008, -0.008];

    await openStudio();

    await waitFor(() => expect(meter()).toHaveAttribute("aria-valuetext", "Too quiet"));
    expect(await screen.findByText(/Move closer/i)).toBeInTheDocument();
  });

  it("says so when the microphone is clipping", async () => {
    const webAudio = installWebAudioStub();
    installGrantedMediaDevices();
    webAudio.signal = [1, -1];

    await openStudio();

    await waitFor(() => expect(meter()).toHaveAttribute("aria-valuetext", "Too loud"));
    expect(await screen.findByText(/will distort/i)).toBeInTheDocument();
  });

  it("offers no advice while the level is fine", async () => {
    // A meter that explains itself when nothing is wrong trains people to stop reading it.
    const webAudio = installWebAudioStub();
    installGrantedMediaDevices();
    webAudio.signal = [0.25, -0.25];

    await openStudio();
    await waitFor(() => expect(meter()).toHaveAttribute("aria-valuetext", "Good"));

    expect(screen.queryByText(/Move closer|will distort|not muted/i)).not.toBeInTheDocument();
  });

  it("reads not open on a machine with no microphone", async () => {
    // Distinct from silence. There is no device to check, so telling somebody to unmute one would
    // send them looking for a fault that does not exist.
    installWebAudioStub();
    installNoCameraMediaDevices();
    (navigator.mediaDevices as unknown as { getUserMedia: ReturnType<typeof vi.fn> }).getUserMedia =
      vi.fn().mockRejectedValue(Object.assign(new Error("none"), { name: "NotFoundError" }));

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /share a screen instead/i }));

    await waitFor(() => expect(meter()).toHaveAttribute("aria-valuetext", "Not open"));
    expect(screen.queryByText(/not muted on the device itself/i)).not.toBeInTheDocument();
  });

  it("defaults to cleaning up speech", async () => {
    // Almost every broadcast is a person talking, usually into a laptop in an untreated room.
    // They do not know their room is the problem; someone broadcasting music knows they are.
    const getUserMedia = installGrantedMediaDevices();
    installWebAudioStub();

    await openStudio();

    await expect(screen.findByLabelText(/Sound type/i)).resolves.toHaveValue("voice");

    const audioCall = getUserMedia.mock.calls.find(([constraints]) => constraints.audio);
    expect(audioCall?.[0].audio).toMatchObject({
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
    });
  });

  it("leaves music completely alone when told it is music", async () => {
    const getUserMedia = installGrantedMediaDevices();
    installWebAudioStub();

    await openStudio();
    await userEvent.selectOptions(await screen.findByLabelText(/Sound type/i), "music");

    await waitFor(() => {
      const last = getUserMedia.mock.calls.filter(([c]) => c.audio).at(-1);
      expect(last?.[0].audio).toMatchObject({
        echoCancellation: false,
        noiseSuppression: false,
        autoGainControl: false,
      });
    });

    // Never at the cost of what is captured: the mode decides what is done to the signal, not
    // what the device is asked for.
    const last = getUserMedia.mock.calls.filter(([c]) => c.audio).at(-1);
    expect(last?.[0].audio).toMatchObject({
      sampleRate: { ideal: 48000 },
      channelCount: { ideal: 2 },
    });
  });
});

/**
 * Recording to a file without broadcasting.
 *
 * Not every session is a show. These assert the part that makes that real: the recording controls
 * are reachable without ever pressing Start Live, and nothing about a recording touches the
 * broadcast.
 */
describe("LiveStudio recording", () => {
  beforeEach(() => {
    apiMock.status.mockResolvedValue(buildStatus("DRAFT"));
    apiMock.get.mockResolvedValue(buildSession());
  });

  /** A MediaRecorder for jsdom, which has none. Records what it was handed. */
  function installRecorder() {
    const started: { mimeType?: string }[] = [];

    class FakeMediaRecorder {
      static isTypeSupported = () => true;
      state = "inactive";
      ondataavailable: ((event: { data: Blob }) => void) | null = null;
      onerror: (() => void) | null = null;
      onstop: (() => void) | null = null;

      constructor(
        public stream: MediaStream,
        public options: { mimeType?: string },
      ) {
        started.push(options);
      }

      start() {
        this.state = "recording";
      }

      stop() {
        this.state = "inactive";
        this.onstop?.();
      }
    }

    vi.stubGlobal("MediaRecorder", FakeMediaRecorder);

    // No file picker: the in-memory path, which is what a browser without one does.
    vi.stubGlobal("showSaveFilePicker", undefined);
    vi.stubGlobal("URL", { ...URL, createObjectURL: () => "blob:x", revokeObjectURL: () => undefined });

    return started;
  }

  it("offers recording without going live", async () => {
    // The whole point. Making somebody start a broadcast to get a recording would be the wrong
    // shape entirely.
    installRecorder();
    installGrantedMediaDevices();
    installWebAudioStub();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));

    expect(await screen.findByRole("button", { name: /start recording/i })).toBeEnabled();
    expect(apiMock.start).not.toHaveBeenCalled();
  });

  it("records, and never touches the broadcast doing it", async () => {
    const started = installRecorder();
    installGrantedMediaDevices();
    installWebAudioStub();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start recording/i }));

    await waitFor(() => expect(started).toHaveLength(1));
    expect(await screen.findByRole("button", { name: /stop recording/i })).toBeInTheDocument();

    // No session was prepared, published or started. A recording is not a broadcast.
    expect(apiMock.prepare).not.toHaveBeenCalled();
    expect(apiMock.start).not.toHaveBeenCalled();
    expect(apiMock.issueCredential).not.toHaveBeenCalled();

    // And Start Live is still there, unaffected, for somebody who wants both.
    expect(screen.getByRole("button", { name: "Start Live" })).toBeEnabled();
  });

  it("says why it cannot record once the last source goes away", async () => {
    // The real shape of "nothing to record": a machine with no camera and no microphone entered by
    // sharing a screen, and then the share ended. The studio is still open and still granted — it
    // simply has nothing left to put in a file, and a disabled button that says so beats a dead one.
    installRecorder();
    const { screenTrack } = installNoCameraMediaDevices();
    (navigator.mediaDevices as unknown as { getUserMedia: ReturnType<typeof vi.fn> }).getUserMedia =
      vi.fn().mockRejectedValue(Object.assign(new Error("none"), { name: "NotFoundError" }));

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /share a screen instead/i }));

    // Sharing is enough to record.
    expect(await screen.findByRole("button", { name: /start recording/i })).toBeEnabled();

    screenTrack.emit("ended");

    await waitFor(() =>
      expect(screen.getByRole("button", { name: /start recording/i })).toBeDisabled(),
    );
    expect(screen.getByText(/nothing to record yet/i)).toBeInTheDocument();
  });

  it("warns that an in-memory recording cannot run forever", async () => {
    // Said before it becomes a problem: the failure is a tab running out of memory an hour into
    // something unrepeatable.
    installRecorder();
    installGrantedMediaDevices();
    installWebAudioStub();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start recording/i }));

    expect(await screen.findByText(/holds the recording in memory/i)).toBeInTheDocument();
  });

  it("stops when asked and keeps the file", async () => {
    installRecorder();
    installGrantedMediaDevices();
    installWebAudioStub();

    render(<LiveStudio sessionId={SESSION_ID} />);
    await userEvent.click(await screen.findByRole("button", { name: /use camera and microphone/i }));
    await userEvent.click(await screen.findByRole("button", { name: /start recording/i }));
    await screen.findByRole("button", { name: /stop recording/i });

    await userEvent.click(screen.getByRole("button", { name: /stop recording/i }));

    expect(await screen.findByRole("button", { name: /start recording/i })).toBeInTheDocument();
  });
});
