import { API_BASE_URL, ApiError, apiFetch } from "@/lib/api/client";
import type {
  DevicePaired,
  DeviceSession,
  IngestCredential,
  SessionSource,
  SourceInvitation,
  SourcePreview,
  SourceRole,
} from "@/lib/types";

const DEVICE_TOKEN_KEY = "livestream.deviceToken";

/**
 * Storage for a paired device's token.
 *
 * Deliberately separate from the user token store. A phone acting as a camera has no account, and
 * the two credentials mean different things — mixing them would let a device token be sent to
 * operator endpoints, or a user token to device ones.
 *
 * Keyed by session so one phone can be paired to two sessions without the second overwriting the
 * first.
 */
export const deviceTokenStore = {
  get(sessionId: string): string | null {
    if (typeof window === "undefined") return null;
    try {
      return window.localStorage.getItem(`${DEVICE_TOKEN_KEY}.${sessionId}`);
    } catch {
      return null; // Private mode or blocked storage.
    }
  },

  save(sessionId: string, token: string): void {
    if (typeof window === "undefined") return;
    try {
      window.localStorage.setItem(`${DEVICE_TOKEN_KEY}.${sessionId}`, token);
    } catch {
      // Storage unavailable: pairing still works for this page load.
    }
  },

  clear(sessionId: string): void {
    if (typeof window === "undefined") return;
    try {
      window.localStorage.removeItem(`${DEVICE_TOKEN_KEY}.${sessionId}`);
    } catch {
      // Nothing to do.
    }
  },
};

/**
 * Performs a request authenticated as a device.
 *
 * There is no refresh: a device token is short-lived by design, and when it stops working the
 * honest answer is to pair again rather than to silently extend access an operator may have
 * withdrawn.
 */
async function deviceFetch<T>(
  path: string,
  deviceToken: string,
  options: { method?: "GET" | "POST"; body?: unknown } = {},
): Promise<T> {
  const headers: Record<string, string> = {
    Accept: "application/json",
    Authorization: `Device ${deviceToken}`,
  };

  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  const response = await fetch(`${API_BASE_URL}${path}`, {
    method: options.method ?? "GET",
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });

  if (!response.ok) {
    let errorCode: string | null = null;
    let message = `Request failed with status ${response.status}.`;

    try {
      const problem = (await response.json()) as { title?: string; detail?: string; errorCode?: string };
      errorCode = problem.errorCode ?? null;
      message = problem.title ?? problem.detail ?? message;
    } catch {
      // Non-JSON error body; keep the generic message.
    }

    throw new ApiError(message, response.status, errorCode, response.headers.get("X-Correlation-Id"));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const deviceApi = {
  /**
   * Redeems a pairing code. Anonymous — the device has no account, which is the whole point of the
   * code.
   */
  async claim(code: string, deviceLabel?: string | null): Promise<DevicePaired> {
    const response = await fetch(`${API_BASE_URL}/api/v1/pairings/claim`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ code, deviceLabel: deviceLabel ?? null }),
    });

    if (!response.ok) {
      let message = "That pairing code is not valid. Ask for a new one.";
      let errorCode: string | null = null;

      try {
        const problem = (await response.json()) as { title?: string; errorCode?: string };
        errorCode = problem.errorCode ?? null;
        message = problem.title ?? message;
      } catch {
        // Keep the friendly default.
      }

      throw new ApiError(message, response.status, errorCode, null);
    }

    return (await response.json()) as DevicePaired;
  },

  session: (deviceToken: string) => deviceFetch<DeviceSession>("/api/v1/device/session", deviceToken),

  /** A fresh publish credential for this device's own path. Requested per connection attempt. */
  issueCredential: (deviceToken: string) =>
    deviceFetch<IngestCredential>("/api/v1/device/credentials", deviceToken, { method: "POST" }),
};

// Operator endpoints are ordinary authenticated calls, so they reuse the shared client rather than
// the device-token path above.
const apiGet = <T,>(path: string, signal?: AbortSignal) => apiFetch<T>(path, { signal });
const apiPost = <T,>(path: string, body: unknown) => apiFetch<T>(path, { method: "POST", body });
const apiPatch = <T,>(path: string, body: unknown) => apiFetch<T>(path, { method: "PATCH", body });
const apiDelete = <T,>(path: string) => apiFetch<T>(path, { method: "DELETE" });

/** Operator-side management of the devices contributing to a session. */
export const sourceApi = {
  list: (sessionId: string, signal?: AbortSignal) =>
    apiGet<SessionSource[]>(`/api/v1/live-sessions/${sessionId}/sources`, signal),

  invite: (sessionId: string, role: SourceRole, displayName: string) =>
    apiPost<SourceInvitation>(`/api/v1/live-sessions/${sessionId}/sources/invitations`, {
      role,
      displayName,
    }),

  rename: (sessionId: string, sourceId: string, displayName: string) =>
    apiPatch<SessionSource>(`/api/v1/live-sessions/${sessionId}/sources/${sourceId}`, { displayName }),

  revoke: (sessionId: string, sourceId: string) =>
    apiDelete<SessionSource>(`/api/v1/live-sessions/${sessionId}/sources/${sourceId}`),

  preview: (sessionId: string, sourceId: string) =>
    apiPost<SourcePreview>(`/api/v1/live-sessions/${sessionId}/sources/${sourceId}/preview`, undefined),

  /**
   * Puts one source on air. Returns every source whose program state changed — both ends of the
   * swap — so the control room can update without re-reading the whole list.
   */
  setProgram: (sessionId: string, sourceId: string) =>
    apiPost<SessionSource[]>(
      `/api/v1/live-sessions/${sessionId}/sources/${sourceId}/program`,
      undefined,
    ),
};
