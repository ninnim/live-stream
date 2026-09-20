import { apiFetch } from "@/lib/api/client";
import type { SaveScene, SessionBranding, SessionScene, UpdateBranding } from "@/lib/types";

const get = <T,>(path: string, signal?: AbortSignal) => apiFetch<T>(path, { signal });
const post = <T,>(path: string, body: unknown) => apiFetch<T>(path, { method: "POST", body });
const put = <T,>(path: string, body: unknown) => apiFetch<T>(path, { method: "PUT", body });
const remove = <T,>(path: string) => apiFetch<T>(path, { method: "DELETE" });

/**
 * The studio's look and its prepared shots (docs/07-live-studio.md).
 *
 * Both are configuration owned by the session, so both live under it.
 */
export const studioApi = {
  branding: (sessionId: string, signal?: AbortSignal) =>
    get<SessionBranding>(`/api/v1/live-sessions/${sessionId}/branding`, signal),

  /**
   * Saves branding.
   *
   * `replaceLogo` is explicit because absent and null mean different things here: leaving the logo
   * alone versus clearing it. Without the distinction, saving a colour change from a form that does
   * not carry the image would delete the image.
   */
  updateBranding: (sessionId: string, update: UpdateBranding) =>
    put<SessionBranding>(`/api/v1/live-sessions/${sessionId}/branding`, update),

  scenes: (sessionId: string, signal?: AbortSignal) =>
    get<SessionScene[]>(`/api/v1/live-sessions/${sessionId}/scenes`, signal),

  addScene: (sessionId: string, scene: SaveScene) =>
    post<SessionScene>(`/api/v1/live-sessions/${sessionId}/scenes`, scene),

  updateScene: (sessionId: string, sceneId: string, scene: SaveScene) =>
    put<SessionScene>(`/api/v1/live-sessions/${sessionId}/scenes/${sceneId}`, scene),

  deleteScene: (sessionId: string, sceneId: string) =>
    remove<void>(`/api/v1/live-sessions/${sessionId}/scenes/${sceneId}`),
};
