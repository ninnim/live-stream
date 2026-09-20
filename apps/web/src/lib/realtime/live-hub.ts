import {
  HttpTransportType,
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import { API_BASE_URL, tokenStore } from "@/lib/api/client";
import type {
  DestinationStatusPayload,
  LiveSessionHealth,
  LiveSessionStatusPayload,
  Recording,
  SourceStatusPayload,
} from "@/lib/types";

/**
 * Client for the `/hubs/live` control channel (docs/09-api-specification.md).
 *
 * The hub carries server-authoritative state. It is a latency optimisation, not the source of
 * truth: the studio also polls REST, so a dropped socket degrades responsiveness rather than
 * correctness.
 */
export interface LiveHubHandlers {
  onSessionStateChanged?: (status: LiveSessionStatusPayload) => void;
  onHealthUpdated?: (sessionId: string, health: LiveSessionHealth) => void;
  onViewerCountUpdated?: (sessionId: string, viewerCount: number) => void;
  onRecordingStateChanged?: (sessionId: string, recording: Recording) => void;
  onErrorRaised?: (sessionId: string, error: { errorCode: string; message: string }) => void;
  /**
   * One destination changed state. Carried separately from session state so the studio can show a
   * platform failing while the session badge stays Live — the visible form of the isolation
   * guarantee.
   */
  onDestinationStateChanged?: (destination: DestinationStatusPayload) => void;
  /**
   * One contributing device changed state. Separate from session state so a phone dropping out of
   * a multi-camera show updates only its own tile.
   */
  onSourceStateChanged?: (source: SourceStatusPayload) => void;
  onConnectionStateChanged?: (connected: boolean) => void;
}

export class LiveHubClient {
  private connection: HubConnection | null = null;

  constructor(
    private readonly sessionId: string,
    private readonly handlers: LiveHubHandlers,
  ) {}

  async connect(): Promise<void> {
    if (this.connection) return;

    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/live`, {
        // The browser cannot set an Authorization header on the WebSocket handshake, so SignalR
        // passes the token as a query parameter; the API only honours that for hub paths.
        accessTokenFactory: () => tokenStore.getAccessToken() ?? "",
        transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect([0, 2000, 5000, 10_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on("sessionStateChanged", (status: LiveSessionStatusPayload) =>
      this.handlers.onSessionStateChanged?.(status),
    );
    connection.on("healthUpdated", (sessionId: string, health: LiveSessionHealth) =>
      this.handlers.onHealthUpdated?.(sessionId, health),
    );
    connection.on("viewerCountUpdated", (sessionId: string, viewerCount: number) =>
      this.handlers.onViewerCountUpdated?.(sessionId, viewerCount),
    );
    connection.on("recordingStateChanged", (sessionId: string, recording: Recording) =>
      this.handlers.onRecordingStateChanged?.(sessionId, recording),
    );
    connection.on("errorRaised", (sessionId: string, error: { errorCode: string; message: string }) =>
      this.handlers.onErrorRaised?.(sessionId, error),
    );
    connection.on("destinationStateChanged", (destination: DestinationStatusPayload) =>
      this.handlers.onDestinationStateChanged?.(destination),
    );
    connection.on("sourceStateChanged", (source: SourceStatusPayload) =>
      this.handlers.onSourceStateChanged?.(source),
    );

    connection.onreconnected(() => {
      this.handlers.onConnectionStateChanged?.(true);
      // Group membership does not survive a reconnect, so rejoin before expecting events again.
      void connection.invoke("JoinSession", this.sessionId).catch(() => undefined);
    });

    connection.onreconnecting(() => this.handlers.onConnectionStateChanged?.(false));
    connection.onclose(() => this.handlers.onConnectionStateChanged?.(false));

    this.connection = connection;

    await connection.start();
    await connection.invoke("JoinSession", this.sessionId);
    this.handlers.onConnectionStateChanged?.(true);
  }

  get isConnected(): boolean {
    return this.connection?.state === HubConnectionState.Connected;
  }

  async disconnect(): Promise<void> {
    const connection = this.connection;
    this.connection = null;

    if (!connection) return;

    try {
      if (connection.state === HubConnectionState.Connected) {
        await connection.invoke("LeaveSession", this.sessionId);
      }
      await connection.stop();
    } catch {
      // Disconnecting must never surface an error to the broadcaster.
    }
  }
}
