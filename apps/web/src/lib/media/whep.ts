/**
 * WHEP (WebRTC-HTTP Egress Protocol) subscriber.
 *
 * The receiving mirror of {@link ./whip.ts}: one HTTP exchange, and the gateway starts sending. The
 * control room uses it to pull each contributing device into the studio, where those streams are
 * composed into the program.
 *
 * WebRTC rather than HLS because this is a production tool, not a viewer. HLS carries several
 * seconds of buffering, which is fine for an audience and useless for someone deciding when to cut
 * — by the time a presenter appeared on an HLS preview, the moment would have passed.
 *
 * Authorization is a short-lived, path-scoped read credential issued by our API per subscription.
 * It is held in memory only and never persisted.
 */

export type SubscriptionState = "idle" | "connecting" | "connected" | "reconnecting" | "failed" | "closed";

export interface WhepCredential {
  webRtcUrl: string;
  readToken: string;
  iceServers?: RTCIceServer[];
}

export interface WhepSubscriberOptions {
  /** Returns a fresh playback URL and read token. Called for every attempt, including retries. */
  getCredential: () => Promise<WhepCredential>;
  /** Receives the inbound stream once media arrives. Called again if the subscription re-forms. */
  onStream: (stream: MediaStream) => void;
  onStateChange?: (state: SubscriptionState, detail?: string) => void;
  maxReconnectAttempts?: number;
  fetchImpl?: typeof fetch;
  createPeerConnection?: (config: RTCConfiguration) => RTCPeerConnection;
  setTimeoutImpl?: (handler: () => void, timeout: number) => number;
  clearTimeoutImpl?: (handle: number) => void;
}

/** Matches the publish side: the gateway decides on the password, but Basic needs a username. */
const WHEP_USER = "viewer";

function base64(value: string): string {
  if (typeof btoa === "function") return btoa(value);
  return Buffer.from(value, "utf8").toString("base64");
}

/**
 * Backoff for a preview that drops.
 *
 * Shorter and flatter than the publisher's: a preview reconnecting is a cosmetic inconvenience for
 * one operator, whereas a publisher reconnecting is the broadcast, so this can afford to be eager.
 * Capped low so a device that comes back is on screen quickly.
 */
export function previewRetryDelayMs(attempt: number, random: () => number = Math.random): number {
  const base = Math.min(500 * 2 ** Math.max(0, attempt - 1), 5000);
  return Math.round(base * (0.75 + random() * 0.5));
}

export class WhepSubscriber {
  private peerConnection: RTCPeerConnection | null = null;
  private resourceUrl: string | null = null;
  private retryHandle: number | null = null;
  private attempt = 0;
  private disposed = false;
  private currentState: SubscriptionState = "idle";

  private readonly maxReconnectAttempts: number;
  private readonly fetchImpl: typeof fetch;
  private readonly createPeerConnection: (config: RTCConfiguration) => RTCPeerConnection;
  private readonly setTimeoutImpl: (handler: () => void, timeout: number) => number;
  private readonly clearTimeoutImpl: (handle: number) => void;

  constructor(private readonly options: WhepSubscriberOptions) {
    this.maxReconnectAttempts = options.maxReconnectAttempts ?? 10;
    this.fetchImpl = options.fetchImpl ?? ((...args) => fetch(...args));
    this.createPeerConnection = options.createPeerConnection ?? ((config) => new RTCPeerConnection(config));
    this.setTimeoutImpl =
      options.setTimeoutImpl ?? ((handler, timeout) => window.setTimeout(handler, timeout));
    this.clearTimeoutImpl = options.clearTimeoutImpl ?? ((handle) => window.clearTimeout(handle));
  }

  get state(): SubscriptionState {
    return this.currentState;
  }

  async start(): Promise<void> {
    this.disposed = false;
    this.attempt = 0;
    await this.connect();
  }

  async stop(): Promise<void> {
    this.disposed = true;
    this.cancelPendingRetry();

    const resourceUrl = this.resourceUrl;
    this.resourceUrl = null;

    this.teardown();
    this.setState("closed");

    if (resourceUrl) {
      try {
        await this.fetchImpl(resourceUrl, { method: "DELETE" });
      } catch {
        // Best effort. The gateway reaps an abandoned subscription on its own.
      }
    }
  }

  private async connect(): Promise<void> {
    if (this.disposed) return;

    this.setState(this.attempt === 0 ? "connecting" : "reconnecting");
    this.teardown();

    const { webRtcUrl, readToken, iceServers } = await this.options.getCredential();

    const peerConnection = this.createPeerConnection({ iceServers: iceServers ?? [] });
    this.peerConnection = peerConnection;

    // Receive-only, and declared up front: WHEP is a single request/response, so the offer has to
    // describe what we want before the gateway has said what it has.
    peerConnection.addTransceiver("video", { direction: "recvonly" });
    peerConnection.addTransceiver("audio", { direction: "recvonly" });

    const inbound = new MediaStream();

    peerConnection.ontrack = (event) => {
      if (this.disposed || peerConnection !== this.peerConnection) return;

      inbound.addTrack(event.track);

      // Fires once per track. Handing over on the first is what puts a picture up promptly; the
      // stream object is the same one, so audio joining later needs no second notification.
      if (inbound.getTracks().length === 1) {
        this.options.onStream(inbound);
      }
    };

    peerConnection.onconnectionstatechange = () => {
      if (this.disposed || peerConnection !== this.peerConnection) return;

      switch (peerConnection.connectionState) {
        case "connected":
          this.attempt = 0;
          this.setState("connected");
          break;
        case "failed":
        case "disconnected":
          this.scheduleRetry(`transport ${peerConnection.connectionState}`);
          break;
        default:
          break;
      }
    };

    const offer = await peerConnection.createOffer();
    await peerConnection.setLocalDescription(offer);
    await waitForIceGathering(peerConnection);

    const localDescription = peerConnection.localDescription;
    if (!localDescription?.sdp) {
      throw new Error("Failed to produce a local session description.");
    }

    const response = await this.fetchImpl(webRtcUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/sdp",
        Authorization: `Basic ${base64(`${WHEP_USER}:${readToken}`)}`,
      },
      body: localDescription.sdp,
    });

    if (!response.ok) {
      throw new Error(`The streaming server refused the preview (status ${response.status}).`);
    }

    const location = response.headers.get("Location");
    this.resourceUrl = location ? new URL(location, webRtcUrl).toString() : null;

    const answerSdp = await response.text();
    await peerConnection.setRemoteDescription({ type: "answer", sdp: answerSdp });
  }

  private scheduleRetry(reason: string): void {
    if (this.disposed || this.retryHandle !== null) return;

    this.attempt += 1;

    if (this.attempt > this.maxReconnectAttempts) {
      this.setState("failed", `Preview did not recover after ${this.maxReconnectAttempts} attempts.`);
      return;
    }

    this.setState("reconnecting", reason);

    this.retryHandle = this.setTimeoutImpl(() => {
      this.retryHandle = null;
      void this.connect().catch((error: unknown) => {
        this.scheduleRetry(error instanceof Error ? error.message : String(error));
      });
    }, previewRetryDelayMs(this.attempt));
  }

  private cancelPendingRetry(): void {
    if (this.retryHandle !== null) {
      this.clearTimeoutImpl(this.retryHandle);
      this.retryHandle = null;
    }
  }

  private teardown(): void {
    if (!this.peerConnection) return;

    this.peerConnection.ontrack = null;
    this.peerConnection.onconnectionstatechange = null;
    try {
      this.peerConnection.close();
    } catch {
      // Already closed.
    }
    this.peerConnection = null;
  }

  private setState(state: SubscriptionState, detail?: string): void {
    if (this.currentState === state) return;
    this.currentState = state;
    this.options.onStateChange?.(state, detail);
  }
}

/** Resolves when ICE gathering completes, or after a short timeout so a slow network cannot hang. */
export function waitForIceGathering(peerConnection: RTCPeerConnection, timeoutMs = 3000): Promise<void> {
  if (peerConnection.iceGatheringState === "complete") return Promise.resolve();

  return new Promise((resolve) => {
    const finish = (): void => {
      peerConnection.removeEventListener("icegatheringstatechange", onStateChange);
      clearTimeout(timer);
      resolve();
    };

    const onStateChange = (): void => {
      if (peerConnection.iceGatheringState === "complete") finish();
    };

    const timer = setTimeout(finish, timeoutMs);
    peerConnection.addEventListener("icegatheringstatechange", onStateChange);
  });
}
