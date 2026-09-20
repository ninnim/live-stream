import { apiFetch } from "@/lib/api/client";
import type {
  AuthResult,
  BroadcasterSignal,
  CurrentUser,
  IngestCredential,
  LiveSession,
  LiveSessionEvent,
  LiveSessionHealth,
  LiveSessionStatusPayload,
  LiveSessionVisibility,
  Paged,
  Playback,
  Recording,
} from "@/lib/types";

// ---------------------------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------------------------

export const authApi = {
  register: (body: {
    email: string;
    password: string;
    displayName: string;
    workspaceName?: string;
  }) => apiFetch<AuthResult>("/api/v1/auth/register", { method: "POST", body, anonymous: true }),

  login: (body: { email: string; password: string }) =>
    apiFetch<AuthResult>("/api/v1/auth/login", { method: "POST", body, anonymous: true }),

  logout: (refreshToken: string | null) =>
    apiFetch<void>("/api/v1/auth/logout", { method: "POST", body: { refreshToken } }),

  me: (signal?: AbortSignal) => apiFetch<CurrentUser>("/api/v1/auth/me", { signal }),

  /**
   * Asks for a reset link. Succeeds whether or not the address belongs to anybody — the server
   * deliberately answers identically, so this page must not imply otherwise.
   */
  forgotPassword: (body: { email: string }) =>
    apiFetch<void>("/api/v1/auth/forgot-password", { method: "POST", body, anonymous: true }),

  resetPassword: (body: { token: string; newPassword: string }) =>
    apiFetch<void>("/api/v1/auth/reset-password", { method: "POST", body, anonymous: true }),
};

// ---------------------------------------------------------------------------------------------
// Live sessions
// ---------------------------------------------------------------------------------------------

export const liveSessionApi = {
  list: (params: { status?: string; page?: number; pageSize?: number } = {}, signal?: AbortSignal) => {
    const query = new URLSearchParams();
    if (params.status) query.set("status", params.status);
    if (params.page) query.set("page", String(params.page));
    if (params.pageSize) query.set("pageSize", String(params.pageSize));

    const suffix = query.size > 0 ? `?${query.toString()}` : "";
    return apiFetch<Paged<LiveSession>>(`/api/v1/live-sessions${suffix}`, { signal });
  },

  get: (id: string, signal?: AbortSignal) => apiFetch<LiveSession>(`/api/v1/live-sessions/${id}`, { signal }),

  status: (id: string, signal?: AbortSignal) =>
    apiFetch<LiveSessionStatusPayload>(`/api/v1/live-sessions/${id}/status`, { signal }),

  health: (id: string, signal?: AbortSignal) =>
    apiFetch<LiveSessionHealth>(`/api/v1/live-sessions/${id}/health`, { signal }),

  events: (id: string, limit = 50, signal?: AbortSignal) =>
    apiFetch<LiveSessionEvent[]>(`/api/v1/live-sessions/${id}/events?limit=${limit}`, { signal }),

  recordings: (id: string, signal?: AbortSignal) =>
    apiFetch<Recording[]>(`/api/v1/live-sessions/${id}/recordings`, { signal }),

  create: (body: {
    title: string;
    description?: string | null;
    visibility: LiveSessionVisibility;
    recordingEnabled: boolean;
  }) => apiFetch<LiveSession>("/api/v1/live-sessions", { method: "POST", body }),

  prepare: (id: string) => apiFetch<LiveSession>(`/api/v1/live-sessions/${id}/prepare`, { method: "POST" }),

  start: (id: string) =>
    apiFetch<LiveSessionStatusPayload>(`/api/v1/live-sessions/${id}/start`, { method: "POST" }),

  stop: (id: string) =>
    apiFetch<LiveSessionStatusPayload>(`/api/v1/live-sessions/${id}/stop`, { method: "POST" }),

  /**
   * Requests a fresh broadcaster credential. Called once per connection attempt, including every
   * reconnect, because credentials are short-lived by design.
   */
  issueCredential: (id: string) =>
    apiFetch<IngestCredential>(`/api/v1/live-sessions/${id}/sources/credentials`, { method: "POST" }),

  /** Reports a client transport event for diagnostics. Never changes server-side session state. */
  reportSignal: (id: string, signal: BroadcasterSignal, detail?: string) =>
    apiFetch<void>(`/api/v1/live-sessions/${id}/broadcaster-signals`, {
      method: "POST",
      body: { signal, detail: detail ?? null },
    }),

  playback: (id: string, signal?: AbortSignal) =>
    apiFetch<Playback>(`/api/v1/live-sessions/${id}/playback`, { anonymous: true, signal }),
};
