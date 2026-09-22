import { describe, expect, it, vi } from "vitest";
import {
  WhipPublisher,
  preferredVideoCodecs,
  reconnectDelayMs,
  withAudioQuality,
} from "@/lib/media/whip";

/**
 * A controllable RTCPeerConnection stand-in. jsdom has no WebRTC, and these tests need to drive
 * the exact transport transitions that reconnect behaviour depends on.
 */
class FakeSender {
  // One encoding, as a real sender reports once a track is attached. An empty array models a
  // sender before negotiation, where `setParameters` would reject anything set on it.
  parameters: RTCRtpSendParameters = { encodings: [{}] } as unknown as RTCRtpSendParameters;

  readonly replaceTrack = vi.fn(async (track: MediaStreamTrack | null) => {
    this.track = track;
  });

  readonly setParameters = vi.fn(async (parameters: RTCRtpSendParameters) => {
    this.parameters = parameters;
  });

  constructor(public track: MediaStreamTrack | null) {}

  getParameters(): RTCRtpSendParameters {
    return this.parameters;
  }
}

class FakePeerConnection {
  connectionState: RTCPeerConnectionState = "new";
  iceGatheringState: RTCIceGatheringState = "complete";
  localDescription: RTCSessionDescription | null = null;
  onconnectionstatechange: (() => void) | null = null;

  readonly addedTracks: MediaStreamTrack[] = [];
  readonly senders: FakeSender[] = [];
  closed = false;

  /** Stats the publisher will read. Replaced per test to drive quality assertions. */
  stats: Map<string, unknown> = new Map();

  addTrack(track: MediaStreamTrack): RTCRtpSender {
    this.addedTracks.push(track);
    const sender = new FakeSender(track);
    this.senders.push(sender);
    return sender as unknown as RTCRtpSender;
  }

  getStats(): Promise<RTCStatsReport> {
    return Promise.resolve(this.stats as unknown as RTCStatsReport);
  }

  createOffer(): Promise<RTCSessionDescriptionInit> {
    return Promise.resolve({ type: "offer", sdp: "v=0\r\nfake-offer" });
  }

  setLocalDescription(description: RTCSessionDescriptionInit): Promise<void> {
    this.localDescription = description as RTCSessionDescription;
    return Promise.resolve();
  }

  setRemoteDescription(): Promise<void> {
    return Promise.resolve();
  }

  addEventListener(): void {}

  removeEventListener(): void {}

  close(): void {
    this.closed = true;
  }

  /** Test hook: simulate a transport state change. */
  transitionTo(state: RTCPeerConnectionState): void {
    this.connectionState = state;
    this.onconnectionstatechange?.();
  }
}

function fakeCaptureTrack(kind: "video" | "audio", contentHint = ""): MediaStreamTrack {
  return { kind, enabled: true, contentHint, stop: vi.fn() } as unknown as MediaStreamTrack;
}

function fakeStream(tracks: MediaStreamTrack[] = [fakeCaptureTrack("video")]) {
  return {
    getTracks: () => tracks,
    getVideoTracks: () => tracks.filter((track) => track.kind === "video"),
    getAudioTracks: () => tracks.filter((track) => track.kind === "audio"),
  } as unknown as MediaStream;
}

/** Stats shaped like a real report, so `readOutboundVideo` runs its real extraction. */
function videoStats(overrides: Record<string, unknown> = {}): Map<string, unknown> {
  return new Map<string, unknown>([
    [
      "outbound",
      {
        type: "outbound-rtp",
        kind: "video",
        timestamp: 1000,
        bytesSent: 250_000,
        packetsSent: 200,
        framesPerSecond: 30,
        frameWidth: 1280,
        frameHeight: 720,
        qualityLimitationReason: "none",
        ...overrides,
      },
    ],
    ["remote", { type: "remote-inbound-rtp", kind: "video", packetsLost: 0, roundTripTime: 0.02 }],
  ]);
}

function okResponse(headers: Record<string, string> = {}) {
  return {
    ok: true,
    status: 201,
    headers: { get: (name: string) => headers[name] ?? null },
    text: () => Promise.resolve("v=0\r\nfake-answer"),
  } as unknown as Response;
}

interface Harness {
  publisher: WhipPublisher;
  peers: FakePeerConnection[];
  fetchMock: ReturnType<typeof vi.fn>;
  getCredential: ReturnType<typeof vi.fn>;
  states: string[];
  runTimers: () => Promise<void>;
}

function createHarness(
  options: { fetchImpl?: ReturnType<typeof vi.fn>; stream?: MediaStream } = {},
): Harness {
  const peers: FakePeerConnection[] = [];
  const states: string[] = [];
  const pendingTimers: (() => void)[] = [];

  const fetchMock = options.fetchImpl ?? vi.fn().mockResolvedValue(okResponse());
  const getCredential = vi
    .fn()
    .mockImplementation(() =>
      Promise.resolve({ ingestUrl: "https://media.test/ls_abc/whip", token: `token-${getCredential.mock.calls.length}` }),
    );

  const publisher = new WhipPublisher({
    stream: options.stream ?? fakeStream(),
    getCredential,
    maxReconnectAttempts: 3,
    fetchImpl: fetchMock as unknown as typeof fetch,
    createPeerConnection: () => {
      const peer = new FakePeerConnection();
      peers.push(peer);
      return peer as unknown as RTCPeerConnection;
    },
    setTimeoutImpl: (handler) => {
      pendingTimers.push(handler);
      return pendingTimers.length;
    },
    clearTimeoutImpl: () => undefined,
    events: { onStateChange: (state) => states.push(state) },
  });

  const runTimers = async (): Promise<void> => {
    const timers = pendingTimers.splice(0, pendingTimers.length);
    for (const timer of timers) {
      timer();
    }

    // The reconnect path awaits credential issuance, offer creation, ICE gathering and the WHIP
    // POST, so let the whole promise chain settle before asserting.
    for (let i = 0; i < 10; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
  };

  return { publisher, peers, fetchMock, getCredential, states, runTimers };
}

describe("reconnectDelayMs", () => {
  it("backs off exponentially", () => {
    const noJitter = () => 0.5;

    expect(reconnectDelayMs(1, noJitter)).toBe(1000);
    expect(reconnectDelayMs(2, noJitter)).toBe(2000);
    expect(reconnectDelayMs(3, noJitter)).toBe(4000);
  });

  it("caps the delay so recovery never stalls indefinitely", () => {
    expect(reconnectDelayMs(20, () => 0.5)).toBe(15_000);
  });

  it("applies jitter so broadcasters do not retry in lockstep", () => {
    expect(reconnectDelayMs(3, () => 0)).toBeLessThan(reconnectDelayMs(3, () => 1));
  });
});

describe("WhipPublisher", () => {
  it("publishes the offer with a scoped credential and applies the answer", async () => {
    const harness = createHarness();

    await harness.publisher.start();

    expect(harness.fetchMock).toHaveBeenCalledOnce();
    const [url, init] = harness.fetchMock.mock.calls[0] as [string, RequestInit];

    expect(url).toBe("https://media.test/ls_abc/whip");
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>)["Content-Type"]).toBe("application/sdp");
    // Basic, with the short-lived token as the password — see WhipPublisher for why.
    const auth = (init.headers as Record<string, string>).Authorization ?? "";
    expect(auth).toMatch(/^Basic /);
    expect(atob(auth.slice("Basic ".length))).toBe("broadcaster:token-1");
    expect(init.body).toContain("fake-offer");
  });

  it("attaches every track from the capture stream", async () => {
    const harness = createHarness();

    await harness.publisher.start();

    expect(harness.peers[0]?.addedTracks).toHaveLength(1);
  });

  it("reports connected once the transport is up", async () => {
    const harness = createHarness();
    await harness.publisher.start();

    harness.peers[0]?.transitionTo("connected");

    expect(harness.publisher.state).toBe("connected");
    expect(harness.states).toContain("connected");
  });

  it("reconnects when the transport fails", async () => {
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");

    harness.peers[0]?.transitionTo("failed");
    expect(harness.publisher.state).toBe("reconnecting");

    await harness.runTimers();

    // A second peer connection means a genuine retry happened.
    expect(harness.peers).toHaveLength(2);
  });

  it("requests a fresh credential for every reconnect", async () => {
    // Credentials are short-lived by design, so reusing the original would fail after expiry.
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");
    harness.peers[0]?.transitionTo("disconnected");

    await harness.runTimers();

    expect(harness.getCredential).toHaveBeenCalledTimes(2);
  });

  it("treats a transient disconnect as recoverable rather than fatal", async () => {
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");
    harness.peers[0]?.transitionTo("disconnected");

    await harness.runTimers();
    harness.peers[1]?.transitionTo("connected");

    expect(harness.publisher.state).toBe("connected");
    expect(harness.states).not.toContain("failed");
  });

  it("gives up after the attempt limit", async () => {
    const harness = createHarness({
      fetchImpl: vi
        .fn()
        .mockResolvedValueOnce(okResponse())
        .mockRejectedValue(new Error("network down")),
    });

    await harness.publisher.start();
    harness.peers[0]?.transitionTo("failed");

    for (let i = 0; i < 5; i += 1) {
      await harness.runTimers();
    }

    expect(harness.publisher.state).toBe("failed");
  });

  it("throws when the gateway rejects the first connection", async () => {
    const harness = createHarness({
      fetchImpl: vi.fn().mockResolvedValue({
        ok: false,
        status: 401,
        headers: { get: () => null },
        text: () => Promise.resolve(""),
      } as unknown as Response),
    });

    await expect(harness.publisher.start()).rejects.toThrow(/rejected the connection/);
  });

  it("closes the peer connection and deletes the WHIP session on stop", async () => {
    const harness = createHarness({
      fetchImpl: vi.fn().mockResolvedValue(okResponse({ Location: "/ls_abc/whip/session-1" })),
    });

    await harness.publisher.start();
    await harness.publisher.stop();

    expect(harness.peers[0]?.closed).toBe(true);
    expect(harness.publisher.state).toBe("closed");

    const deleteCall = harness.fetchMock.mock.calls.find(
      ([, init]) => (init as RequestInit | undefined)?.method === "DELETE",
    );
    expect(deleteCall?.[0]).toBe("https://media.test/ls_abc/whip/session-1");
  });

  it("stops reconnecting once stopped", async () => {
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");

    await harness.publisher.stop();
    const peerCountAfterStop = harness.peers.length;

    await harness.runTimers();

    expect(harness.peers).toHaveLength(peerCountAfterStop);
  });

  it("survives a stop when the gateway is unreachable", async () => {
    const harness = createHarness({
      fetchImpl: vi
        .fn()
        .mockResolvedValueOnce(okResponse({ Location: "/ls_abc/whip/session-1" }))
        .mockRejectedValueOnce(new Error("network down")),
    });

    await harness.publisher.start();

    await expect(harness.publisher.stop()).resolves.toBeUndefined();
    expect(harness.publisher.state).toBe("closed");
  });
});

describe("WhipPublisher device switching", () => {
  it("swaps a camera into the open transport without renegotiating", async () => {
    // This is the whole point: plugging in a capture card mid-show must not cost the audience a
    // dropout. A new peer connection, or a new credential, would mean the stream restarted.
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");

    const replacement = fakeCaptureTrack("video");
    await harness.publisher.replaceTrack(replacement);

    expect(harness.peers[0]?.senders[0]?.replaceTrack).toHaveBeenCalledWith(replacement);
    expect(harness.peers).toHaveLength(1);
    expect(harness.getCredential).toHaveBeenCalledTimes(1);
  });

  it("sends the replacement track after a later reconnect", async () => {
    // The publisher owns its tracks rather than re-reading the original stream, so a reconnect
    // after a device change must not silently revert to the camera that was unplugged.
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");

    const replacement = fakeCaptureTrack("video");
    await harness.publisher.replaceTrack(replacement);

    harness.peers[0]?.transitionTo("failed");
    await harness.runTimers();

    expect(harness.peers[1]?.addedTracks).toEqual([replacement]);
  });

  it("holds the track for a device chosen before publishing starts", async () => {
    const harness = createHarness();

    const replacement = fakeCaptureTrack("video");
    await harness.publisher.replaceTrack(replacement);
    await harness.publisher.start();

    expect(harness.peers[0]?.addedTracks).toEqual([replacement]);
  });

  it("reports a failed swap instead of letting the preview and the broadcast disagree", async () => {
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");

    const sender = harness.peers[0]?.senders[0];
    sender?.replaceTrack.mockRejectedValueOnce(new Error("InvalidStateError"));

    await expect(harness.publisher.replaceTrack(fakeCaptureTrack("video"))).rejects.toThrow(/camera/);
  });

  it("grades camera video for smooth motion", async () => {
    // Under load, shed resolution and keep the frame rate: a soft but fluid picture reads as live
    // television, a sharp juddering one reads as broken.
    const harness = createHarness();
    await harness.publisher.start();

    expect(harness.peers[0]?.senders[0]?.parameters.degradationPreference).toBe("maintain-framerate");
  });

  it("grades screen content for legibility instead", async () => {
    const peers: FakePeerConnection[] = [];
    const publisher = new WhipPublisher({
      stream: fakeStream([fakeCaptureTrack("video", "detail")]),
      getCredential: () => Promise.resolve({ ingestUrl: "https://media.test/ls_abc/whip", token: "t" }),
      fetchImpl: vi.fn().mockResolvedValue(okResponse()) as unknown as typeof fetch,
      createPeerConnection: () => {
        const peer = new FakePeerConnection();
        peers.push(peer);
        return peer as unknown as RTCPeerConnection;
      },
    });

    await publisher.start();

    expect(peers[0]?.senders[0]?.parameters.degradationPreference).toBe("maintain-resolution");
  });

  it("grades a shared game for smooth motion, like a camera", async () => {
    // A game share carries `motion`, not `detail`: 900p at a steady 60 beats a razor-sharp
    // stutter. Screen content is only graded for legibility when it is slides.
    const peers: FakePeerConnection[] = [];
    const publisher = new WhipPublisher({
      stream: fakeStream([fakeCaptureTrack("video", "motion")]),
      getCredential: () => Promise.resolve({ ingestUrl: "https://media.test/ls_abc/whip", token: "t" }),
      fetchImpl: vi.fn().mockResolvedValue(okResponse()) as unknown as typeof fetch,
      createPeerConnection: () => {
        const peer = new FakePeerConnection();
        peers.push(peer);
        return peer as unknown as RTCPeerConnection;
      },
    });

    await publisher.start();

    expect(peers[0]?.senders[0]?.parameters.degradationPreference).toBe("maintain-framerate");
  });

  it("publishes anyway when the browser refuses to be tuned", async () => {
    // `setParameters` support is uneven. Losing a tuning hint costs a little smoothness under load;
    // failing the publish over it would trade a working stream for a cosmetic one.
    const peers: FakePeerConnection[] = [];
    const publisher = new WhipPublisher({
      stream: fakeStream(),
      getCredential: () => Promise.resolve({ ingestUrl: "https://media.test/ls_abc/whip", token: "t" }),
      fetchImpl: vi.fn().mockResolvedValue(okResponse()) as unknown as typeof fetch,
      createPeerConnection: () => {
        const peer = new FakePeerConnection();
        const originalAddTrack = peer.addTrack.bind(peer);
        peer.addTrack = (track) => {
          const sender = originalAddTrack(track) as unknown as FakeSender;
          sender.setParameters.mockRejectedValue(new Error("not supported"));
          return sender as unknown as RTCRtpSender;
        };
        peers.push(peer);
        return peer as unknown as RTCPeerConnection;
      },
    });

    await expect(publisher.start()).resolves.toBeUndefined();
    expect(peers[0]?.addedTracks).toHaveLength(1);
  });
});

describe("WhipPublisher quality sampling", () => {
  it("reports nothing until the transport is connected", async () => {
    // Sampling a connecting or reconnecting transport reads counters from a peer connection that is
    // about to be discarded, which produces a figure that means nothing.
    const harness = createHarness();
    await harness.publisher.start();

    await expect(harness.publisher.sampleQuality()).resolves.toBeNull();
  });

  it("measures the rate between two readings", async () => {
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");

    const peer = harness.peers[0];
    if (!peer) throw new Error("no peer connection");

    peer.stats = videoStats({ timestamp: 0, bytesSent: 0 });
    const first = await harness.publisher.sampleQuality();
    expect(first?.headline).toContain("Measuring");

    peer.stats = videoStats({ timestamp: 2000, bytesSent: 500_000, packetsSent: 400 });
    const second = await harness.publisher.sampleQuality();

    expect(second?.bitrateKbps).toBe(2000);
    expect(second?.verdict).toBe("good");
  });

  it("does not carry counters across a reconnect", async () => {
    // A new peer connection restarts its counters from zero. Diffing against the old connection
    // would report a wildly negative rate, or a spike, at exactly the moment the operator is
    // looking at the indicator.
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");

    const first = harness.peers[0];
    if (!first) throw new Error("no peer connection");
    first.stats = videoStats({ timestamp: 10_000, bytesSent: 9_000_000 });
    await harness.publisher.sampleQuality();

    first.transitionTo("failed");
    await harness.runTimers();
    harness.peers[1]?.transitionTo("connected");

    const second = harness.peers[1];
    if (!second) throw new Error("no reconnected peer");
    second.stats = videoStats({ timestamp: 500, bytesSent: 50_000 });

    const measured = await harness.publisher.sampleQuality();
    expect(measured?.headline).toContain("Measuring");
  });

  it("keeps the broadcast alive when the browser refuses to report stats", async () => {
    const harness = createHarness();
    await harness.publisher.start();
    harness.peers[0]?.transitionTo("connected");

    const peer = harness.peers[0];
    if (!peer) throw new Error("no peer connection");
    peer.getStats = () => Promise.reject(new Error("not supported"));

    await expect(harness.publisher.sampleQuality()).resolves.toBeNull();
    expect(harness.publisher.state).toBe("connected");
  });
});

describe("WhipPublisher ICE configuration", () => {
  it("uses the ICE servers supplied with the credential", async () => {
    // Infrastructure addresses come from the API, so nothing about the deployment is baked into
    // the browser bundle.
    const peers: { config: RTCConfiguration }[] = [];

    const publisher = new WhipPublisher({
      stream: fakeStream(),
      getCredential: () =>
        Promise.resolve({
          ingestUrl: "https://media.test/ls_abc/whip",
          token: "t",
          iceServers: [
            { urls: ["stun:stun.example.test:3478"] },
            { urls: ["turn:turn.example.test:3478"], username: "u", credential: "c" },
          ],
        }),
      fetchImpl: vi.fn().mockResolvedValue(okResponse()) as unknown as typeof fetch,
      createPeerConnection: (config) => {
        peers.push({ config });
        return new FakePeerConnection() as unknown as RTCPeerConnection;
      },
    });

    await publisher.start();

    expect(peers[0]?.config.iceServers).toHaveLength(2);
    expect(peers[0]?.config.iceServers?.[1]).toMatchObject({ username: "u", credential: "c" });
  });

  it("falls back to the configured servers when the credential supplies none", async () => {
    const peers: { config: RTCConfiguration }[] = [];

    const publisher = new WhipPublisher({
      stream: fakeStream(),
      getCredential: () =>
        Promise.resolve({ ingestUrl: "https://media.test/ls_abc/whip", token: "t" }),
      iceServers: [{ urls: "stun:fallback.test:3478" }],
      fetchImpl: vi.fn().mockResolvedValue(okResponse()) as unknown as typeof fetch,
      createPeerConnection: (config) => {
        peers.push({ config });
        return new FakePeerConnection() as unknown as RTCPeerConnection;
      },
    });

    await publisher.start();

    expect(peers[0]?.config.iceServers).toEqual([{ urls: "stun:fallback.test:3478" }]);
  });

  it("refreshes ICE configuration on every reconnect", async () => {
    // A TURN credential is short-lived, so a retry must not reuse a stale one.
    const peers: { config: RTCConfiguration }[] = [];
    const pendingTimers: (() => void)[] = [];
    let issued = 0;

    const publisher = new WhipPublisher({
      stream: fakeStream(),
      getCredential: () => {
        issued += 1;
        return Promise.resolve({
          ingestUrl: "https://media.test/ls_abc/whip",
          token: `t${issued}`,
          iceServers: [{ urls: ["turn:turn.example.test:3478"], username: "u", credential: `c${issued}` }],
        });
      },
      fetchImpl: vi.fn().mockResolvedValue(okResponse()) as unknown as typeof fetch,
      createPeerConnection: (config) => {
        peers.push({ config });
        return new FakePeerConnection() as unknown as RTCPeerConnection;
      },
      setTimeoutImpl: (handler) => {
        pendingTimers.push(handler);
        return pendingTimers.length;
      },
      clearTimeoutImpl: () => undefined,
    });

    await publisher.start();
    const firstPeer = peers[0] as unknown as { config: RTCConfiguration };
    void firstPeer;

    // Drive a transport failure, then let the retry run.
    const peer = publisher as unknown as { peerConnection: FakePeerConnection | null };
    peer.peerConnection?.transitionTo("failed");

    pendingTimers.splice(0).forEach((run) => run());
    for (let i = 0; i < 10; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }

    expect(peers.length).toBeGreaterThan(1);
    expect(peers[1]?.config.iceServers?.[0]).toMatchObject({ credential: "c2" });
  });
});

/**
 * A camera and a microphone are both optional (docs/04-native-broadcasting.md).
 *
 * The publisher has to be honest about what it is sending: whatever exists at connect time, nothing
 * it does not have, and — the part that is easy to get wrong — a device acquired *after* going live
 * has to actually reach the audience rather than sit in a field nobody sends.
 */
describe("WhipPublisher with optional devices", () => {
  it("publishes a screen-only broadcast with no microphone", async () => {
    const video = fakeCaptureTrack("video");
    const { publisher, peers } = createHarness({ stream: fakeStream([video]) });

    await publisher.start();

    expect(peers[0]?.addedTracks).toEqual([video]);
  });

  it("publishes sound alone when the machine has no camera", async () => {
    const audio = fakeCaptureTrack("audio");
    const { publisher, peers } = createHarness({ stream: fakeStream([audio]) });

    await publisher.start();

    expect(peers[0]?.addedTracks).toEqual([audio]);
    expect(peers[0]?.addedTracks.some((track) => track.kind === "video")).toBe(false);
  });

  it("renegotiates when a device it never had is added mid-broadcast", async () => {
    // A screen-only broadcast that gains a microphone. There is no audio transceiver to swap into,
    // and WHIP cannot add one in place — so the alternative to reconnecting is a microphone the
    // studio shows as live and the audience never hears.
    const { publisher, peers } = createHarness({ stream: fakeStream([fakeCaptureTrack("video")]) });

    await publisher.start();
    peers[0]?.transitionTo("connected");

    await publisher.replaceTrack(fakeCaptureTrack("audio"));

    expect(peers).toHaveLength(2);
    expect(peers[1]?.addedTracks.map((track) => track.kind).sort()).toEqual(["audio", "video"]);
  });

  it("does not renegotiate for a device added before the broadcast started", async () => {
    // Nothing is publishing yet, so the track will simply be picked up when the connection opens.
    const { publisher, peers } = createHarness({ stream: fakeStream([fakeCaptureTrack("video")]) });

    await publisher.replaceTrack(fakeCaptureTrack("audio"));

    expect(peers).toHaveLength(0);
  });

  it("stops sending a kind whose last device went away, without dropping the connection", async () => {
    const audio = fakeCaptureTrack("audio");
    const { publisher, peers } = createHarness({
      stream: fakeStream([fakeCaptureTrack("video"), audio]),
    });

    await publisher.start();
    peers[0]?.transitionTo("connected");

    await publisher.removeTrack("audio");

    // The sender is kept with a null track rather than removed: that needs no renegotiation, and
    // it leaves the transceiver ready for a device that comes back.
    const audioSender = peers[0]?.senders.find((sender) => sender.track === null);
    expect(audioSender?.replaceTrack).toHaveBeenCalledWith(null);
    expect(peers).toHaveLength(1);
    expect(peers[0]?.closed).toBe(false);
  });

  it("survives a device being removed before anything is publishing", async () => {
    const { publisher } = createHarness({ stream: fakeStream([fakeCaptureTrack("video")]) });

    await expect(publisher.removeTrack("video")).resolves.toBeUndefined();
  });

  it("re-adds only what remains after a device was removed and the transport dropped", async () => {
    // The reconnect must not resurrect a device that is no longer there.
    const { publisher, peers, runTimers } = createHarness({
      stream: fakeStream([fakeCaptureTrack("video"), fakeCaptureTrack("audio")]),
    });

    await publisher.start();
    peers[0]?.transitionTo("connected");
    await publisher.removeTrack("audio");

    peers[0]?.transitionTo("failed");
    await runTimers();

    expect(peers).toHaveLength(2);
    expect(peers[1]?.addedTracks.map((track) => track.kind)).toEqual(["video"]);
  });
});

/**
 * Broadcast audio quality (docs/decisions/0017-broadcast-quality.md).
 *
 * Opus channel count and bitrate are decided by the `fmtp` line in the offer — there is no track
 * constraint or sender parameter for either. An untouched offer is therefore a decision to send
 * mono speech audio at about 32 kbps, which is right for a video call and wrong for a broadcast.
 */
describe("withAudioQuality", () => {
  const offer = [
    "v=0",
    "m=audio 9 UDP/TLS/RTP/SAVPF 111 63",
    "a=rtpmap:111 opus/48000/2",
    "a=fmtp:111 minptime=10;useinbandfec=1",
    "m=video 9 UDP/TLS/RTP/SAVPF 96",
    "a=rtpmap:96 VP8/90000",
  ].join("\r\n");

  it("asks for stereo at the requested bitrate", () => {
    const result = withAudioQuality(offer, 128_000);

    expect(result).toContain("stereo=1");
    expect(result).toContain("sprop-stereo=1");
    expect(result).toContain("maxaveragebitrate=128000");
  });

  it("keeps the parameters the browser already asked for", () => {
    // `minptime` is the browser's own packetisation choice. Replacing the whole line would discard
    // it, and anything else the browser adds in a future version.
    const result = withAudioQuality(offer, 128_000);

    expect(result).toContain("minptime=10");
  });

  it("replaces its own keys rather than appending a second copy", () => {
    const already = offer.replace("a=fmtp:111 minptime=10;useinbandfec=1", "a=fmtp:111 stereo=0");
    const result = withAudioQuality(already, 96_000);

    expect(result).not.toContain("stereo=0");
    expect([...result.matchAll(/stereo=1/g)]).toHaveLength(2); // stereo and sprop-stereo
  });

  it("adds an fmtp line when the offer has none", () => {
    const withoutFmtp = offer.replace("a=fmtp:111 minptime=10;useinbandfec=1\r\n", "");
    const result = withAudioQuality(withoutFmtp, 128_000);

    expect(result).toContain("a=fmtp:111 stereo=1");
    // Placed against the right payload, immediately after its rtpmap.
    expect(result.indexOf("a=rtpmap:111")).toBeLessThan(result.indexOf("a=fmtp:111"));
  });

  it("leaves the video section alone", () => {
    const result = withAudioQuality(offer, 128_000);

    expect(result).toContain("a=rtpmap:96 VP8/90000");
    expect(result).not.toMatch(/a=fmtp:96/);
  });

  it("returns an offer with no Opus exactly as it was", () => {
    // A browser that negotiated some other codec must not have its offer rewritten on a guess.
    const noOpus = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 8\r\na=rtpmap:8 PCMA/8000";

    expect(withAudioQuality(noOpus, 128_000)).toBe(noOpus);
  });

  it("handles an offer that lists Opus twice", () => {
    // Chrome offers a second Opus payload for redundancy on some platforms.
    const twice = offer.replace(
      "a=rtpmap:111 opus/48000/2",
      "a=rtpmap:111 opus/48000/2\r\na=rtpmap:112 opus/48000/2",
    );

    const result = withAudioQuality(twice, 128_000);

    expect(result).toContain("a=fmtp:112 stereo=1");
  });
});

describe("WhipPublisher video ceiling", () => {
  it("raises the sender's bitrate ceiling above the browser's own", async () => {
    const peers: FakePeerConnection[] = [];
    const publisher = new WhipPublisher({
      stream: fakeStream([fakeCaptureTrack("video", "motion")]),
      videoBitrate: 6_000_000,
      getCredential: () => Promise.resolve({ ingestUrl: "https://media.test/ls_abc/whip", token: "t" }),
      fetchImpl: vi.fn().mockResolvedValue(okResponse()) as unknown as typeof fetch,
      createPeerConnection: () => {
        const peer = new FakePeerConnection();
        peers.push(peer);
        return peer as unknown as RTCPeerConnection;
      },
    });

    await publisher.start();

    expect(peers[0]?.senders[0]?.parameters.encodings?.[0]?.maxBitrate).toBe(6_000_000);
  });

  it("publishes normally when no ceiling is configured", async () => {
    const { publisher, peers } = createHarness();

    await publisher.start();

    expect(peers[0]?.senders[0]?.parameters.encodings?.[0]?.maxBitrate).toBeUndefined();
  });

  it("leaves resolution and frame rate alone when nothing has asked to adapt", async () => {
    // Every desktop broadcast. The adaptive fields must not appear in the parameters of a
    // publisher that was never given limits — ADR 0011 §7 keeps that path manual.
    const { publisher, peers } = createHarness();

    await publisher.start();

    const encoding = peers[0]?.senders[0]?.parameters.encodings?.[0];
    expect(encoding?.scaleResolutionDownBy).toBeUndefined();
    expect(encoding?.maxFramerate).toBeUndefined();
  });
});

/**
 * Adaptive encoder control (docs/decisions/0021-mobile-broadcasting.md).
 *
 * The point of applying limits through `setParameters` rather than by re-opening the camera is that
 * nothing is renegotiated: the peer connection, the senders and the tracks all survive, so the
 * audience sees a softer picture rather than a gap. These tests assert exactly that.
 */
describe("WhipPublisher adaptive encoding", () => {
  it("retunes the running encoder without touching the connection", async () => {
    const { publisher, peers } = createHarness();

    await publisher.start();
    const peer = peers[0]!;

    await publisher.applyEncoding({ maxBitrate: 700_000, scaleResolutionDownBy: 2, maxFramerate: 24 });

    const encoding = peer.senders[0]?.parameters.encodings?.[0];
    expect(encoding?.maxBitrate).toBe(700_000);
    expect(encoding?.scaleResolutionDownBy).toBe(2);
    expect(encoding?.maxFramerate).toBe(24);

    // The evidence that this is not a renegotiation: one connection, one WHIP POST, nothing closed.
    expect(peers).toHaveLength(1);
    expect(peer.closed).toBe(false);
  });

  it("keeps the limits through a reconnect", async () => {
    const { publisher, peers, runTimers } = createHarness();

    await publisher.start();
    await publisher.applyEncoding({ maxBitrate: 350_000, scaleResolutionDownBy: 3 });

    // A reconnect happens because the network faltered, which is the moment the limits matter
    // most. Coming back at full quality into the link that just failed would fail it again.
    peers[0]!.transitionTo("failed");
    await runTimers();

    expect(peers).toHaveLength(2);
    expect(peers[1]?.senders[0]?.parameters.encodings?.[0]?.maxBitrate).toBe(350_000);
    expect(peers[1]?.senders[0]?.parameters.encodings?.[0]?.scaleResolutionDownBy).toBe(3);
  });

  it("returns to full size when the ladder climbs back", async () => {
    const { publisher, peers } = createHarness();

    await publisher.start();
    await publisher.applyEncoding({ maxBitrate: 700_000, scaleResolutionDownBy: 2, maxFramerate: 24 });
    await publisher.applyEncoding({ maxBitrate: 2_500_000, scaleResolutionDownBy: 1, maxFramerate: 30 });

    const encoding = peers[0]?.senders[0]?.parameters.encodings?.[0];
    expect(encoding?.scaleResolutionDownBy).toBe(1);
    expect(encoding?.maxFramerate).toBe(30);
  });

  it("clears a frame-rate cap that no longer applies", async () => {
    const { publisher, peers } = createHarness();

    await publisher.start();
    await publisher.applyEncoding({ maxFramerate: 20 });
    await publisher.applyEncoding({ maxBitrate: 2_500_000 });

    // Left in place, a cap from a rung nobody is on any more quietly holds the broadcast at 20fps.
    expect(peers[0]?.senders[0]?.parameters.encodings?.[0]?.maxFramerate).toBeUndefined();
  });

  it("does nothing, rather than throwing, when nothing is publishing", async () => {
    const { publisher } = createHarness();

    await expect(publisher.applyEncoding({ maxBitrate: 700_000 })).resolves.toBeUndefined();
  });

  it("applies limits that arrived before the connection existed", async () => {
    const { publisher, peers } = createHarness();

    await publisher.applyEncoding({ maxBitrate: 700_000 });
    await publisher.start();

    expect(peers[0]?.senders[0]?.parameters.encodings?.[0]?.maxBitrate).toBe(700_000);
  });
});

/**
 * Video codec preference (docs/decisions/0017-broadcast-quality.md).
 *
 * A browser left alone publishes VP8: the oldest codec available, encoded on the CPU because
 * practically no GPU has a VP8 encoder, and the one thing RTMP cannot carry without a transcode.
 */
describe("preferredVideoCodecs", () => {
  const chromeCodecs = [
    { mimeType: "video/VP8" },
    { mimeType: "video/rtx" },
    { mimeType: "video/VP9" },
    { mimeType: "video/H264" },
    { mimeType: "video/AV1" },
  ];

  it("puts H.264 first, then VP9, then VP8", () => {
    const result = preferredVideoCodecs(chromeCodecs).map((codec) => codec.mimeType);

    expect(result.slice(0, 3)).toEqual(["video/H264", "video/VP9", "video/VP8"]);
  });

  it("keeps every codec the browser offered", () => {
    // Dropping one would be a way to make a session fail to negotiate at all on a future browser.
    // Reordering only changes what is chosen when there is a choice.
    const result = preferredVideoCodecs(chromeCodecs);

    expect(result).toHaveLength(chromeCodecs.length);
    expect(result.map((c) => c.mimeType).sort()).toEqual(chromeCodecs.map((c) => c.mimeType).sort());
  });

  it("leaves unranked entries in their original order, after the ranked ones", () => {
    const result = preferredVideoCodecs(chromeCodecs).map((codec) => codec.mimeType);

    expect(result.slice(3)).toEqual(["video/rtx", "video/AV1"]);
  });

  it("matches the mime type without caring about case", () => {
    const result = preferredVideoCodecs([{ mimeType: "video/vp8" }, { mimeType: "video/h264" }]);

    expect(result[0]?.mimeType).toBe("video/h264");
  });

  it("promotes a hardware codec when H.264 would mean encoding in software", () => {
    // Some Intel and AMD parts accelerate VP9 and not H.264. Keeping H.264 first there would
    // hand the broadcaster's CPU a job their GPU was willing to do — and the relay, which has its
    // own hardware encoder, absorbs the transcode that costs.
    const result = preferredVideoCodecs(chromeCodecs, ["video/VP9"]).map((codec) => codec.mimeType);

    expect(result[0]).toBe("video/VP9");
  });

  it("leaves the order alone when H.264 is the accelerated one", () => {
    // Promoting anything above a hardware H.264 would cost a transcode for nothing.
    const result = preferredVideoCodecs(chromeCodecs, ["video/H264", "video/VP9"]).map(
      (codec) => codec.mimeType,
    );

    expect(result.slice(0, 3)).toEqual(["video/H264", "video/VP9", "video/VP8"]);
  });

  it("changes nothing when the machine's hardware was never established", () => {
    // The overwhelmingly common case, and the one that must behave exactly as before.
    const withoutEvidence = preferredVideoCodecs(chromeCodecs).map((codec) => codec.mimeType);
    const withEmptyEvidence = preferredVideoCodecs(chromeCodecs, []).map((codec) => codec.mimeType);

    expect(withEmptyEvidence).toEqual(withoutEvidence);
  });

  it("still keeps every codec when hardware reorders them", () => {
    const result = preferredVideoCodecs(chromeCodecs, ["video/VP9"]);

    expect(result.map((c) => c.mimeType).sort()).toEqual(chromeCodecs.map((c) => c.mimeType).sort());
  });

  it("copes with a browser that offers no H.264 at all", () => {
    const result = preferredVideoCodecs([{ mimeType: "video/VP8" }, { mimeType: "video/VP9" }]);

    expect(result.map((c) => c.mimeType)).toEqual(["video/VP9", "video/VP8"]);
  });

  it("returns an empty list unchanged rather than throwing", () => {
    expect(preferredVideoCodecs([])).toEqual([]);
  });
});
