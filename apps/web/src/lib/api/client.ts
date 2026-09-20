import type { AuthResult } from "@/lib/types";

export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL?.replace(/\/$/, "") ?? "http://localhost:8080";

const ACCESS_TOKEN_KEY = "livestream.accessToken";
const REFRESH_TOKEN_KEY = "livestream.refreshToken";

/**
 * An error carrying the API's stable error code, so the UI can react to a specific failure
 * (for example a lost session) rather than pattern-matching on message text.
 */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly errorCode: string | null,
    readonly correlationId: string | null,
  ) {
    super(message);
    this.name = "ApiError";
  }

  /** True when the session should be treated as gone rather than retried. */
  get isSessionGone(): boolean {
    return this.errorCode === "LIVE_001_SESSION_NOT_FOUND" || this.status === 404;
  }

  get isAuthFailure(): boolean {
    return this.status === 401;
  }
}

/**
 * Token storage.
 *
 * Tokens live in `localStorage` so a studio reload does not sign the broadcaster out mid-stream.
 * This is a deliberate, documented trade-off: it is readable by any script on the origin, so the
 * access token is short-lived and no streaming credential is ever stored here — ingest tokens are
 * requested fresh per connection and kept only in memory.
 */
export const tokenStore = {
  getAccessToken(): string | null {
    if (typeof window === "undefined") return null;
    try {
      return window.localStorage.getItem(ACCESS_TOKEN_KEY);
    } catch {
      return null; // Private mode or blocked storage.
    }
  },

  getRefreshToken(): string | null {
    if (typeof window === "undefined") return null;
    try {
      return window.localStorage.getItem(REFRESH_TOKEN_KEY);
    } catch {
      return null;
    }
  },

  save(auth: Pick<AuthResult, "accessToken" | "refreshToken">): void {
    if (typeof window === "undefined") return;
    try {
      window.localStorage.setItem(ACCESS_TOKEN_KEY, auth.accessToken);
      window.localStorage.setItem(REFRESH_TOKEN_KEY, auth.refreshToken);
    } catch {
      // Storage unavailable: the session still works for this page load.
    }
  },

  clear(): void {
    if (typeof window === "undefined") return;
    try {
      window.localStorage.removeItem(ACCESS_TOKEN_KEY);
      window.localStorage.removeItem(REFRESH_TOKEN_KEY);
    } catch {
      // Nothing to do.
    }
  },
};

interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  /** Skip the Authorization header, for endpoints that must be called anonymously. */
  anonymous?: boolean;
  signal?: AbortSignal;
}

let refreshInFlight: Promise<boolean> | null = null;

/**
 * Exchanges the refresh token for a new pair.
 *
 * Concurrent callers share one in-flight refresh: the API rotates refresh tokens and treats a
 * replayed one as theft, so parallel refreshes would revoke the user's whole session.
 */
async function refreshAccessToken(): Promise<boolean> {
  refreshInFlight ??= (async () => {
    const refreshToken = tokenStore.getRefreshToken();
    if (!refreshToken) return false;

    try {
      const response = await fetch(`${API_BASE_URL}/api/v1/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken }),
      });

      if (!response.ok) {
        tokenStore.clear();
        return false;
      }

      const auth = (await response.json()) as AuthResult;
      tokenStore.save(auth);
      return true;
    } catch {
      return false;
    } finally {
      refreshInFlight = null;
    }
  })();

  return refreshInFlight;
}

async function toApiError(response: Response): Promise<ApiError> {
  let errorCode: string | null = null;
  let message = `Request failed with status ${response.status}.`;

  try {
    const problem = (await response.json()) as {
      title?: string;
      detail?: string;
      errorCode?: string;
    };

    errorCode = problem.errorCode ?? null;
    // The API writes user-facing, actionable titles; prefer them over a generic status message.
    message = problem.title ?? problem.detail ?? message;
  } catch {
    // Non-JSON error body; keep the generic message.
  }

  return new ApiError(message, response.status, errorCode, response.headers.get("X-Correlation-Id"));
}

/**
 * Performs an authenticated API request, transparently refreshing the access token once on 401.
 */
export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const send = async (): Promise<Response> => {
    const headers: Record<string, string> = { Accept: "application/json" };

    if (options.body !== undefined) {
      headers["Content-Type"] = "application/json";
    }

    if (!options.anonymous) {
      const accessToken = tokenStore.getAccessToken();
      if (accessToken) {
        headers.Authorization = `Bearer ${accessToken}`;
      }
    }

    return fetch(`${API_BASE_URL}${path}`, {
      method: options.method ?? "GET",
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    });
  };

  let response = await send();

  if (response.status === 401 && !options.anonymous && (await refreshAccessToken())) {
    response = await send();
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  if (response.status === 204 || response.headers.get("Content-Length") === "0") {
    return undefined as T;
  }

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}
