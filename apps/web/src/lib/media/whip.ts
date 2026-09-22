/**
 * WHIP (WebRTC-HTTP Ingestion Protocol) publisher.
 *
 * This is what removes the OBS dependency: the browser's own `RTCPeerConnection` publishes the
 * camera and microphone straight into the media gateway over a single HTTP exchange. There is no
 * vendor SDK here, so swapping the media provider does not touch the studio
 * (ai/architecture-rules.md: no RTMP/codec details inside UI components).
 *
 * Authorization is a short-lived, path-scoped bearer token issued by our API per connection
 * attempt. It is held in memory only and never persisted.
 *
 * The publisher owns its outgoing tracks rather than reading them from a stream on each connect.
 * That is what makes changing camera mid-broadcast possible: `RTCRtpSender.replaceTrack` swaps the
 * source inside the existing transport with no renegotiation, so viewers see a cut rather than a
 * dropout, and a later reconnect uses whatever track is current.
 */

import type { EncodingLimits } from "@/lib/media/adaptive";
import {
  type OutboundSample,
  type PublishQuality,
  readOutboundVideo,
  summarizeQuality,
} from "@/lib/media/quality";

export type BroadcastConnectionState =
  | "idle"
  | "connecting"
  | "connected"
  | "reconnecting"
  | "failed"
  | "closed";

export interface WhipPublisherEvents {
  onStateChange?: (state: BroadcastConnectionState, detail?: string) => void;
  /** Fired for each reconnect attempt, with a 1-based attempt number. */
  onReconnectAttempt?: (attempt: number, delayMs: number) => void;
}

export interface WhipCredential {
  ingestUrl: string;
  token: string;
  /**
   * ICE servers for this attempt, supplied by the server. Any TURN credential is therefore
   * short-lived and issued per connection, exactly like the ingest token.
   */
  iceServers?: RTCIceServer[];
}

export interface WhipCredentialProvider {
  /** Returns a fresh ingest URL, token, and ICE configuration. Called for every attempt. */
  (): Promise<WhipCredential>;
}

export interface WhipPublisherOptions {
  stream: MediaStream;
  getCredential: WhipCredentialProvider;
  events?: WhipPublisherEvents;
  /** Maximum reconnect attempts before giving up. */
  maxReconnectAttempts?: number;
  /** Fallback ICE servers, used only when the credential response supplies none. */
  iceServers?: RTCIceServer[];
  /** Opus bitrate to request, in bits per second. See {@link DEFAULT_AUDIO_BITRATE}. */
  audioBitrate?: number;
  /**
   * Upper bound for the outgoing video, in bits per second.
   *
   * Without one the browser picks, and for a shared screen it picks conservatively — around
   * 2.5 Mbps, which is thin for 1080p and visibly thin for 1080p60.
   */
  videoBitrate?: number;
  /**
   * Codec families this machine's GPU can encode. Shapes the codec preference — see
   * {@link preferredVideoCodecs}. Empty means "not established", which changes nothing.
   */
  hardwareCodecs?: readonly string[];
  /** Injectable for tests. */
  fetchImpl?: typeof fetch;
  createPeerConnection?: (config: RTCConfiguration) => RTCPeerConnection;
  /** Injectable timer so reconnect backoff can be tested without real delays. */
  setTimeoutImpl?: (handler: () => void, timeout: number) => number;
  clearTimeoutImpl?: (handle: number) => void;
}

/**
 * Username half of the Basic credential. The gateway ignores it — authorization is decided entirely
 * by the token in the password — but Basic auth requires a user component.
 */
const WHIP_USER = "broadcaster";

/** Base64 for a UTF-8 string, in browsers and in test environments alike. */
function base64(value: string): string {
  if (typeof btoa === "function") {
    // btoa is latin1-only; the token is base64url ASCII, so this is safe here.
    return btoa(value);
  }

  return Buffer.from(value, "utf8").toString("base64");
}

/**
 * Video codecs in the order this platform wants them, best first.
 *
 * H.264 is preferred over what a browser offers on its own, which is VP8. Three reasons, and they
 * compound:
 *
 * - **Hardware.** Practically every GPU has an H.264 encoder and practically none has a VP8 one, so
 *   VP8 is encoded on the CPU. On a laptop that is the difference between holding 1080p60 and
 *   watching the frame rate collapse while the fans spin up.
 * - **Quality per bit.** VP8 is the oldest codec in the list and looks it, most visibly on the
 *   high-detail, high-motion content — games — that this costs the most to send.
 * - **It is what the egress speaks.** RTMP carries H.264 only, so every other choice guarantees a
 *   transcode at the relay.
 *
 * VP9 sits second: better than VP8 and still software-encoded almost everywhere.
 */
const VIDEO_CODEC_PREFERENCE = ["video/H264", "video/VP9", "video/VP8"];

/**
 * Reorders a browser's codec list into this platform's preference.
 *
 * Every codec the browser offered is kept, in preference order, with anything unranked left at the
 * end in its original order. Dropping codecs would be a way to make a session fail to negotiate at
 * all on some future browser; reordering only changes what is chosen when there is a choice.
 */
export function preferredVideoCodecs<T extends { mimeType: string }>(
  codecs: readonly T[],
  /**
   * Codec families this machine's GPU can encode, if that was established.
   *
   * When H.264 is among them the order below is already right and nothing changes. When it is not
   * — some Intel and AMD parts accelerate VP9 but not H.264 — a hardware codec is promoted above
   * it. That trades a transcode at the relay, which has its own hardware encoder, for the
   * broadcaster's CPU, which is the scarcer of the two and the one whose exhaustion the audience
   * actually sees.
   */
  hardwareCodecs: readonly string[] = [],
): T[] {
  const accelerated = new Set(hardwareCodecs.map((mime) => mime.toLowerCase()));
  const h264IsAccelerated = accelerated.has("video/h264");

  const rank = (codec: T): number => {
    const mimeType = codec.mimeType.toLowerCase();
    const index = VIDEO_CODEC_PREFERENCE.findIndex((mime) => mime.toLowerCase() === mimeType);
    const base = index === -1 ? VIDEO_CODEC_PREFERENCE.length : index;

    // Only when H.264 would otherwise force software encoding. Promoting a hardware codec over a
    // hardware H.264 would cost a transcode for nothing.
    return !h264IsAccelerated && accelerated.has(mimeType) ? base - VIDEO_CODEC_PREFERENCE.length : base;
  };

  // A stable sort, so retransmission and forward-error-correction entries keep their position
  // relative to the codec they belong to.
  return [...codecs]
    .map((codec, index) => ({ codec, index, rank: rank(codec) }))
    .sort((left, right) => left.rank - right.rank || left.index - right.index)
    .map((entry) => entry.codec);
}

/**
 * Opus bitrate requested in the offer, in bits per second.
 *
 * 128 kbps stereo is the broadcast default. It is not what a browser picks on its own: left alone,
 * Chrome negotiates a microphone track as mono at roughly 32 kbps, tuned for intelligible speech
 * over a bad connection. That is the right default for a video call and audibly poor for anything
 * with music, a game, or two people talking over each other.
 */
export const DEFAULT_AUDIO_BITRATE = 128_000;

/**
 * Ceiling for the outgoing video, in bits per second.
 *
 * 6 Mbps is sized for the hardest case the studio offers — 1080p60 gameplay — and is a ceiling
 * rather than a target: WebRTC sends far less for a still slide, and backs off on its own when the
 * connection cannot carry it. Left unset the browser caps a screen share at around 2.5 Mbps, which
 * is thin at 1080p and visibly thin at 60fps.
 */
export const DEFAULT_VIDEO_BITRATE = 6_000_000;

/**
 * Asks for stereo, high-bitrate Opus in the offer.
 *
 * This has to happen in the SDP. There is no track constraint or sender parameter for it: the
 * browser decides Opus's channel count and target rate from the `fmtp` line it offers, so an
 * untouched offer is a decision to send mono speech audio.
 *
 * `sprop-stereo` tells the far end we are sending stereo; `stereo` tells it we can receive it.
 * Both are set because MediaMTX echoes the parameters it is offered, and a mismatch downgrades the
 * session to mono without reporting anything.
 *
 * Anything unexpected is left exactly as it was — a broadcast that publishes with default audio is
 * far better than one that fails because a regular expression did not match.
 */
export function withAudioQuality(sdp: string, bitrate: number): string {
  const payloads = [...sdp.matchAll(/^a=rtpmap:(\d+)\s+opus\/48000\/2/gim)].map((match) => match[1]);

  if (payloads.length === 0) {
    return sdp;
  }

  let result = sdp;

  for (const payload of payloads) {
    const existing = new RegExp(`^a=fmtp:${payload} (.*)$`, "im");
    const wanted = `stereo=1;sprop-stereo=1;useinbandfec=1;maxaveragebitrate=${bitrate}`;

    result = existing.test(result)
      ? result.replace(existing, (_line, parameters: string) => {
          // Keep whatever the browser asked for, minus the keys being overridden.
          const kept = parameters
            .split(";")
            .map((entry) => entry.trim())
            .filter(
              (entry) =>
                entry.length > 0
                && !/^(stereo|sprop-stereo|useinbandfec|maxaveragebitrate)=/i.test(entry),
            );

          return `a=fmtp:${payload} ${[...kept, wanted].join(";")}`;
        })
      : result.replace(
          new RegExp(`^(a=rtpmap:${payload} opus/48000/2.*)$`, "im"),
          `$1\r\na=fmtp:${payload} ${wanted}`,
        );
  }

  return result;
}

/**
 * Exponential backoff with a cap and jitter.
 *
 * Jitter matters when a shared network drops: without it every broadcaster retries in lockstep and
 * hammers the gateway at the same instants.
 */
export function reconnectDelayMs(attempt: number, random: () => number = Math.random): number {
  const base = Math.min(1000 * 2 ** Math.max(0, attempt - 1), 15_000);
  return Math.round(base * (0.75 + random() * 0.5));
}

/**
 * Tells the encoder what to sacrifice when it cannot keep up.
 *
 * Camera video is graded for motion: shed resolution and keep the frame rate, because a soft but
 * fluid picture reads as live television whereas a sharp, juddering one reads as broken. Screen
 * content is graded the other way — text that blurs is unreadable, and slides do not move.
 */
function degradationPreferenceFor(track: MediaStreamTrack): RTCDegradationPreference {
  return track.contentHint === "detail" || track.contentHint === "text"
    ? "maintain-resolution"
    : "maintain-framerate";
}

export class WhipPublisher {
  private peerConnection: RTCPeerConnection | null = null;
  private resourceUrl: string | null = null;
  private reconnectHandle: number | null = null;
  private attempt = 0;
  private disposed = false;
  private currentState: BroadcastConnectionState = "idle";

  /** The tracks actually being sent. Swapped by `replaceTrack`, re-added on every reconnect. */
  private videoTrack: MediaStreamTrack | null;
  private audioTrack: MediaStreamTrack | null;
  private videoSender: RTCRtpSender | null = null;
  private audioSender: RTCRtpSender | null = null;

  /** Previous stats reading. Cleared per connection: a new peer connection restarts its counters. */
  private previousSample: OutboundSample | null = null;

  /**
   * Encoder limits applied on top of this publisher's defaults, from {@link applyEncoding}.
   *
   * Deliberately *not* cleared when the connection is rebuilt. A reconnect happens because the
   * network faltered, which is the moment the limits matter most — throwing them away would have
   * every reconnect start again at full quality into the link that just failed.
   */
  private encodingLimits: EncodingLimits | null = null;

  private readonly maxReconnectAttempts: number;
  private readonly fetchImpl: typeof fetch;
  private readonly createPeerConnection: (config: RTCConfiguration) => RTCPeerConnection;
  private readonly setTimeoutImpl: (handler: () => void, timeout: number) => number;
  private readonly clearTimeoutImpl: (handle: number) => void;

  constructor(private readonly options: WhipPublisherOptions) {
    this.maxReconnectAttempts = options.maxReconnectAttempts ?? 8;
    this.fetchImpl = options.fetchImpl ?? ((...args) => fetch(...args));
    this.createPeerConnection = options.createPeerConnection ?? ((config) => new RTCPeerConnection(config));
    this.setTimeoutImpl =
      options.setTimeoutImpl ?? ((handler, timeout) => window.setTimeout(handler, timeout));
    this.clearTimeoutImpl = options.clearTimeoutImpl ?? ((handle) => window.clearTimeout(handle));

    this.videoTrack = options.stream.getVideoTracks()[0] ?? null;
    this.audioTrack = options.stream.getAudioTracks()[0] ?? null;
  }

  get state(): BroadcastConnectionState {
    return this.currentState;
  }

  /** Opens the publishing connection. Throws only if the very first attempt fails. */
  async start(): Promise<void> {
    this.disposed = false;
    this.attempt = 0;
    await this.connect();
  }

  /**
   * Swaps one outgoing track for another without renegotiating.
   *
   * Used for changing camera or microphone mid-broadcast, including a device that was plugged in
   * after the show started. The internal track is updated before the swap is attempted, so even a
   * failed swap converges on the right source at the next reconnect.
   *
   * Throws if the live swap fails, because the alternative is a preview showing the new camera
   * while the audience watches a track that has already been stopped.
   */
  async replaceTrack(track: MediaStreamTrack): Promise<void> {
    const sender = track.kind === "video" ? this.videoSender : this.audioSender;

    if (track.kind === "video") {
      this.videoTrack = track;
    } else {
      this.audioTrack = track;
    }

    if (this.disposed) return;

    if (!sender) {
      // No transceiver exists for this kind, because there was no such device when the connection
      // was negotiated — a screen-only broadcast that has just gained a microphone. WHIP offers no
      // way to add one in place, so the connection is renegotiated.
      //
      // A brief reconnect is the honest outcome. The alternative is a device the studio shows as
      // live and the audience never hears, which is the worse failure by a distance.
      //
      // Only once the connection is established. Renegotiating while one is still being made
      // tears down the attempt in flight — which is exactly what a studio reload does, since
      // it acquires devices and resumes publishing at the same moment.
      if (this.currentState === "connected") {
        await this.renegotiate();
      }

      return;
    }

    try {
      await sender.replaceTrack(track);
    } catch (error) {
      throw new Error(
        `Could not switch to the selected ${track.kind === "video" ? "camera" : "microphone"}: ${
          error instanceof Error ? error.message : String(error)
        }`,
      );
    }

    if (track.kind === "video") {
      await this.tuneVideoSender(sender, track);
    }
  }

  /**
   * Retunes the running encoder, without renegotiating anything.
   *
   * `setParameters` changes bitrate, resolution scale and frame rate inside the connection that is
   * already up: there is no new offer, no new peer connection, and no gap in what the audience is
   * watching. That is what makes automatic adaptation acceptable on a phone at all — the
   * alternative, re-opening the camera at a different rung, blacks the outgoing video out for as
   * long as the device takes to re-open (ADR 0021).
   *
   * Never throws. A browser that will not apply a limit leaves the broadcast exactly as it was,
   * which is a worse picture rather than no picture.
   */
  async applyEncoding(limits: EncodingLimits): Promise<void> {
    this.encodingLimits = limits;

    const sender = this.videoSender;
    const track = this.videoTrack;
    if (!sender || !track || this.disposed) return;

    await this.tuneVideoSender(sender, track);
  }

  /** The limits currently in force, for a caller that needs to show them. */
  get encoding(): EncodingLimits | null {
    return this.encodingLimits;
  }

  /**
   * Stops sending one kind of media, because its last device went away.
   *
   * The sender is left in place with no track rather than being removed: an `RTCRtpSender` with a
   * null track simply stops producing, which needs no renegotiation, and keeps the transceiver
   * ready for a device that comes back.
   */
  async removeTrack(kind: "video" | "audio"): Promise<void> {
    const sender = kind === "video" ? this.videoSender : this.audioSender;

    if (kind === "video") {
      this.videoTrack = null;
    } else {
      this.audioTrack = null;
    }

    if (!sender || this.disposed) return;

    try {
      await sender.replaceTrack(null);
    } catch {
      // Best effort: the device is already gone, and failing here would turn a device unplug into
      // a broadcast error.
    }
  }

  /**
   * Asks for H.264 ahead of the VP8 a browser offers by itself.
   *
   * Must happen before the offer is created — codec preferences are baked into it — which is why
   * this sits inside the connect path rather than beside the other sender tuning.
   *
   * Every failure here is silent and harmless. `setCodecPreferences` is unevenly implemented, a
   * browser may offer no H.264 at all, and either way the session still negotiates: it just
   * negotiates whatever the browser would have chosen anyway. Losing a codec preference costs
   * quality; throwing here would cost the broadcast.
   */
  private preferBetterVideoCodec(peerConnection: RTCPeerConnection, sender: RTCRtpSender): void {
    try {
      const capabilities = RTCRtpSender.getCapabilities?.("video");
      if (!capabilities?.codecs?.length) return;

      const transceiver = peerConnection
        .getTransceivers?.()
        ?.find((candidate) => candidate.sender === sender);

      transceiver?.setCodecPreferences?.(
        preferredVideoCodecs(capabilities.codecs, this.options.hardwareCodecs ?? []),
      );
    } catch {
      // See above: the stream publishes without it.
    }
  }

  /**
   * Rebuilds the connection deliberately, rather than in response to a failure.
   *
   * The attempt counter is reset because this is not a retry: it must not consume the budget that
   * exists for recovering from a genuine network fault.
   */
  private async renegotiate(): Promise<void> {
    this.attempt = 0;
    await this.connect();
  }

  /**
   * Measures how well video is actually going out.
   *
   * Returns `null` when nothing is publishing, and reports "measuring" on the first reading of a
   * connection — every figure worth showing is a rate, and a rate needs two samples.
   */
  async sampleQuality(): Promise<PublishQuality | null> {
    const peerConnection = this.peerConnection;
    if (!peerConnection || this.currentState !== "connected") return null;

    let report: RTCStatsReport;
    try {
      report = await peerConnection.getStats();
    } catch {
      // Stats are diagnostics. A browser that refuses them must not disturb the broadcast.
      return null;
    }

    const sample = readOutboundVideo(report);
    if (!sample) return null;

    const quality = summarizeQuality(sample, this.previousSample);
    this.previousSample = sample;
    return quality;
  }

  /** Closes the connection and stops all reconnect activity. Safe to call repeatedly. */
  async stop(): Promise<void> {
    this.disposed = true;
    this.cancelPendingReconnect();

    const resourceUrl = this.resourceUrl;
    this.resourceUrl = null;

    this.teardownPeerConnection();
    this.setState("closed");

    // Best-effort WHIP session delete so the gateway releases the path promptly. The server's own
    // reconciliation covers us if this never lands.
    if (resourceUrl) {
      try {
        await this.fetchImpl(resourceUrl, { method: "DELETE" });
      } catch {
        // Ignore: teardown must not fail the stop flow.
      }
    }
  }

  private async connect(): Promise<void> {
    if (this.disposed) return;

    this.setState(this.attempt === 0 ? "connecting" : "reconnecting");
    this.teardownPeerConnection();

    const { ingestUrl, token, iceServers } = await this.options.getCredential();

    const peerConnection = this.createPeerConnection({
      iceServers: iceServers ?? this.options.iceServers ?? [],
    });
    this.peerConnection = peerConnection;

    // Adds whatever is current, which after a device switch is not what the constructor was given.
    for (const track of [this.videoTrack, this.audioTrack]) {
      if (!track) continue;

      const sender = peerConnection.addTrack(track, this.options.stream);
      if (track.kind === "video") {
        this.videoSender = sender ?? null;
        if (sender) {
          this.preferBetterVideoCodec(peerConnection, sender);
          await this.tuneVideoSender(sender, track);
        }
      } else {
        this.audioSender = sender ?? null;
      }
    }

    peerConnection.onconnectionstatechange = () => {
      if (this.disposed || peerConnection !== this.peerConnection) return;

      switch (peerConnection.connectionState) {
        case "connected":
          this.attempt = 0;
          this.setState("connected");
          break;
        case "failed":
        case "disconnected":
          // Not fatal on its own: the server holds the session open through its recovery window
          // while we retry.
          this.scheduleReconnect(`transport ${peerConnection.connectionState}`);
          break;
        default:
          break;
      }
    };

    const offer = await peerConnection.createOffer();
    await peerConnection.setLocalDescription(offer);

    // Wait for ICE gathering so the offer carries candidates; WHIP is a single request/response.
    await waitForIceGathering(peerConnection);

    const localDescription = peerConnection.localDescription;
    if (!localDescription?.sdp) {
      throw new Error("Failed to produce a local session description.");
    }

    const response = await this.fetchImpl(ingestUrl, {
      method: "POST",
      headers: {
        "Content-Type": "application/sdp",
        // HTTP Basic, not Bearer: the gateway forwards Basic credentials to the control plane's
        // authorization hook, whereas a Bearer header is only interpreted when the gateway itself
        // is configured to validate JWTs. The token travels as the password.
        Authorization: `Basic ${base64(`${WHIP_USER}:${token}`)}`,
      },
      body: withAudioQuality(localDescription.sdp, this.options.audioBitrate ?? DEFAULT_AUDIO_BITRATE),
    });

    if (!response.ok) {
      throw new Error(`The streaming server rejected the connection (status ${response.status}).`);
    }

    // WHIP returns the created session's URL in Location, used later to delete it.
    const location = response.headers.get("Location");
    this.resourceUrl = location ? new URL(location, ingestUrl).toString() : null;

    const answerSdp = await response.text();
    await peerConnection.setRemoteDescription({ type: "answer", sdp: answerSdp });
  }

  /**
   * Applies encoder tuning to the video sender.
   *
   * Best-effort by design: `setParameters` and `degradationPreference` have uneven support, and
   * their absence costs a little smoothness under load rather than breaking the broadcast. Failing
   * the publish over a tuning hint would trade a working stream for a cosmetic one.
   */
  private async tuneVideoSender(sender: RTCRtpSender, track: MediaStreamTrack): Promise<void> {
    try {
      if (!sender.getParameters || !sender.setParameters) return;

      const parameters = sender.getParameters();
      parameters.degradationPreference = degradationPreferenceFor(track);

      // A ceiling, not a target: WebRTC still backs off when the connection cannot carry it. Left
      // unset, the browser picks its own — around 2.5 Mbps for a shared screen, which is thin for
      // 1080p and visibly thin at 60fps.
      //
      // Adaptive limits win over the publisher's default, because they were measured from this
      // broadcast rather than assumed before it started.
      const ceiling = this.encodingLimits?.maxBitrate ?? this.options.videoBitrate;
      const scale = this.encodingLimits?.scaleResolutionDownBy;
      const frameRate = this.encodingLimits?.maxFramerate;

      // `encodings` can be empty before the first negotiation completes, in which case there is
      // nothing to tune yet and the next reconnect will do it.
      if (parameters.encodings?.length) {
        for (const encoding of parameters.encodings) {
          if (ceiling) encoding.maxBitrate = ceiling;

          // Resolution and frame rate are only ever touched by a caller that asked for them, so a
          // publisher with no adaptive limits — every desktop broadcast — sends exactly the
          // parameters it always did.
          //
          // Within that, both are written on every call rather than only when present: each rung
          // states all three, so climbing back is what clears a scale or a cap that an earlier
          // rung set. Leaving them behind would hold a recovered broadcast at 20fps.
          if (this.encodingLimits) {
            encoding.scaleResolutionDownBy = scale ?? 1;

            if (frameRate) {
              encoding.maxFramerate = frameRate;
            } else {
              delete encoding.maxFramerate;
            }
          }
        }
      }

      await sender.setParameters(parameters);
    } catch {
      // Tuning is an optimisation, not a requirement. The stream publishes without it.
    }
  }

  private scheduleReconnect(reason: string): void {
    if (this.disposed || this.reconnectHandle !== null) return;

    this.attempt += 1;

    if (this.attempt > this.maxReconnectAttempts) {
      this.setState("failed", `Could not reconnect after ${this.maxReconnectAttempts} attempts.`);
      return;
    }

    const delay = reconnectDelayMs(this.attempt);
    this.setState("reconnecting", reason);
    this.options.events?.onReconnectAttempt?.(this.attempt, delay);

    this.reconnectHandle = this.setTimeoutImpl(() => {
      this.reconnectHandle = null;
      void this.connect().catch((error: unknown) => {
        const detail = error instanceof Error ? error.message : String(error);
        this.scheduleReconnect(detail);
      });
    }, delay);
  }

  private cancelPendingReconnect(): void {
    if (this.reconnectHandle !== null) {
      this.clearTimeoutImpl(this.reconnectHandle);
      this.reconnectHandle = null;
    }
  }

  private teardownPeerConnection(): void {
    // Counters belong to the connection being discarded, and the senders with it.
    this.previousSample = null;
    this.videoSender = null;
    this.audioSender = null;

    if (!this.peerConnection) return;

    this.peerConnection.onconnectionstatechange = null;
    try {
      this.peerConnection.close();
    } catch {
      // Already closed.
    }
    this.peerConnection = null;
  }

  private setState(state: BroadcastConnectionState, detail?: string): void {
    if (this.currentState === state) return;
    this.currentState = state;
    this.options.events?.onStateChange?.(state, detail);
  }
}

/** Resolves when ICE gathering completes, or after a short timeout so a slow network cannot hang start. */
export function waitForIceGathering(
  peerConnection: RTCPeerConnection,
  timeoutMs = 3000,
): Promise<void> {
  if (peerConnection.iceGatheringState === "complete") {
    return Promise.resolve();
  }

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
