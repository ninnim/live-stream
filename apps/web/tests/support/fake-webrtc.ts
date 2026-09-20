import { vi } from "vitest";

/** A sender that behaves enough like the real one for track replacement to be observable. */
export class FakeRTCRtpSender {
  parameters: RTCRtpSendParameters = { encodings: [] } as unknown as RTCRtpSendParameters;

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

/**
 * A functional `RTCPeerConnection` stand-in for jsdom, which ships no WebRTC implementation.
 *
 * It implements just enough of the offer/answer handshake for the WHIP publisher to run its real
 * code path, so component tests exercise the actual start flow rather than a mocked one.
 */
export class FakeRTCPeerConnection {
  connectionState: RTCPeerConnectionState = "new";
  iceGatheringState: RTCIceGatheringState = "complete";
  localDescription: RTCSessionDescriptionInit | null = null;
  onconnectionstatechange: (() => void) | null = null;

  /** Every sender created by `addTrack`, in the order the publisher added them. */
  static senders: FakeRTCRtpSender[] = [];

  addEventListener = vi.fn();
  removeEventListener = vi.fn();
  close = vi.fn();

  addTrack(track: MediaStreamTrack): FakeRTCRtpSender {
    const sender = new FakeRTCRtpSender(track);
    FakeRTCPeerConnection.senders.push(sender);
    return sender;
  }

  getStats(): Promise<RTCStatsReport> {
    return Promise.resolve(new Map() as unknown as RTCStatsReport);
  }

  createOffer(): Promise<RTCSessionDescriptionInit> {
    return Promise.resolve({ type: "offer", sdp: "v=0\r\nfake-offer" });
  }

  setLocalDescription(description: RTCSessionDescriptionInit): Promise<void> {
    this.localDescription = description;
    return Promise.resolve();
  }

  setRemoteDescription(): Promise<void> {
    return Promise.resolve();
  }
}

/** Installs the WebRTC and `fetch` stubs a successful WHIP publish needs. */
export function installWebRtcStubs(): void {
  FakeRTCPeerConnection.senders = [];

  vi.stubGlobal("RTCPeerConnection", FakeRTCPeerConnection);
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({
      ok: true,
      status: 201,
      headers: { get: () => null },
      text: () => Promise.resolve("v=0\r\nfake-answer"),
    } as unknown as Response),
  );
}
