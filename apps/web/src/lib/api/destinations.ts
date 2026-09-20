import { apiFetch } from "@/lib/api/client";
import type {
  Destination,
  DestinationEvent,
  DestinationProvider,
  ProviderAccount,
  ProviderDescriptor,
} from "@/lib/types";

/**
 * Multi-platform distribution endpoints.
 *
 * A stream key travels one way only: into `create` and `update`. Nothing here returns one, because
 * nothing on the server will emit one.
 */
export const destinationApi = {
  list: (sessionId: string, signal?: AbortSignal) =>
    apiFetch<Destination[]>(`/api/v1/live-sessions/${sessionId}/destinations`, { signal }),

  get: (sessionId: string, destinationId: string, signal?: AbortSignal) =>
    apiFetch<Destination>(`/api/v1/live-sessions/${sessionId}/destinations/${destinationId}`, { signal }),

  events: (sessionId: string, destinationId: string, limit = 50, signal?: AbortSignal) =>
    apiFetch<DestinationEvent[]>(
      `/api/v1/live-sessions/${sessionId}/destinations/${destinationId}/events?limit=${limit}`,
      { signal },
    ),

  create: (
    sessionId: string,
    body: {
      provider: DestinationProvider;
      displayName: string;
      ingestUrl?: string | null;
      streamKey?: string | null;
      providerAccountId?: string | null;
    },
  ) => apiFetch<Destination>(`/api/v1/live-sessions/${sessionId}/destinations`, { method: "POST", body }),

  /** A null `streamKey` leaves the stored key untouched, so the form never has to hold it. */
  update: (
    sessionId: string,
    destinationId: string,
    body: {
      displayName?: string | null;
      ingestUrl?: string | null;
      streamKey?: string | null;
      enabled?: boolean | null;
    },
  ) =>
    apiFetch<Destination>(`/api/v1/live-sessions/${sessionId}/destinations/${destinationId}`, {
      method: "PATCH",
      body,
    }),

  remove: (sessionId: string, destinationId: string) =>
    apiFetch<void>(`/api/v1/live-sessions/${sessionId}/destinations/${destinationId}`, { method: "DELETE" }),

  start: (sessionId: string, destinationId: string) =>
    apiFetch<Destination>(`/api/v1/live-sessions/${sessionId}/destinations/${destinationId}/start`, {
      method: "POST",
    }),

  stop: (sessionId: string, destinationId: string) =>
    apiFetch<Destination>(`/api/v1/live-sessions/${sessionId}/destinations/${destinationId}/stop`, {
      method: "POST",
    }),
};

export const distributionApi = {
  providers: (signal?: AbortSignal) =>
    apiFetch<ProviderDescriptor[]>("/api/v1/distribution/providers", { signal }),

  accounts: (workspaceId: string, signal?: AbortSignal) =>
    apiFetch<ProviderAccount[]>(`/api/v1/distribution/workspaces/${workspaceId}/accounts`, { signal }),

  /**
   * Starts account linking. The returned `state` must be echoed back on the callback; it is what
   * ties the consent redirect to this browser and this workspace.
   */
  beginAuthorization: (workspaceId: string, provider: DestinationProvider, redirectUri: string) =>
    apiFetch<{ authorizationUrl: string; state: string; expiresAt: string }>(
      `/api/v1/distribution/workspaces/${workspaceId}/accounts/${provider}/authorize`,
      { method: "POST", body: { redirectUri } },
    ),

  completeAuthorization: (code: string, state: string) =>
    apiFetch<ProviderAccount>("/api/v1/distribution/accounts/callback", {
      method: "POST",
      body: { code, state },
    }),

  disconnect: (accountId: string) =>
    apiFetch<void>(`/api/v1/distribution/accounts/${accountId}`, { method: "DELETE" }),
};
