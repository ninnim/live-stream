import { apiFetch } from "@/lib/api/client";
import type { AuthResult, SsoDiscovery, SsoStart } from "@/lib/types";

/**
 * Single sign-on, from the sign-in page's point of view.
 *
 * All three are unauthenticated, and all three post the address rather than putting it in a query
 * string: sign-in pages get linked and screenshotted, and an email address does not belong in a URL.
 */
export const ssoApi = {
  discover: (email: string, signal?: AbortSignal) =>
    apiFetch<SsoDiscovery>("/api/v1/auth/sso/discover", { method: "POST", body: { email }, signal }),

  start: (email: string) => apiFetch<SsoStart>("/api/v1/auth/sso/start", { method: "POST", body: { email } }),

  /** Exchanges the provider's authorization code for a session. Tokens arrive in the body, not the URL. */
  complete: (state: string, code: string) =>
    apiFetch<AuthResult>("/api/v1/auth/sso/callback", { method: "POST", body: { state, code } }),
};
