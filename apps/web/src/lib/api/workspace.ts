import { apiFetch } from "@/lib/api/client";
import type {
  SsoConnection,
  UpdateWorkspaceLimits,
  UpsertSsoConnection,
  WorkspaceErasure,
  WorkspaceLimits,
  WorkspaceUsage,
} from "@/lib/types";

/**
 * Workspace administration (implementation/phase-7-scale-security-and-globalization.md).
 *
 * Nothing here can raise what a workspace is entitled to — that is an operator action on a separate
 * internal API this client cannot reach, and deliberately so.
 */
export const workspaceApi = {
  limits: (workspaceId: string, signal?: AbortSignal) =>
    apiFetch<WorkspaceLimits>(`/api/v1/workspaces/${workspaceId}/limits`, { signal }),

  updateLimits: (workspaceId: string, body: UpdateWorkspaceLimits) =>
    apiFetch<WorkspaceLimits>(`/api/v1/workspaces/${workspaceId}/limits`, { method: "PUT", body }),

  usage: (workspaceId: string, signal?: AbortSignal) =>
    apiFetch<WorkspaceUsage>(`/api/v1/workspaces/${workspaceId}/usage`, { signal }),

  /** 204 when this workspace has no identity provider, which the client reads as null. */
  sso: async (workspaceId: string, signal?: AbortSignal): Promise<SsoConnection | null> =>
    (await apiFetch<SsoConnection | undefined>(`/api/v1/workspaces/${workspaceId}/sso`, { signal })) ?? null,

  saveSso: (workspaceId: string, body: UpsertSsoConnection) =>
    apiFetch<SsoConnection>(`/api/v1/workspaces/${workspaceId}/sso`, { method: "PUT", body }),

  removeSso: (workspaceId: string) =>
    apiFetch<void>(`/api/v1/workspaces/${workspaceId}/sso`, { method: "DELETE" }),

  export: (workspaceId: string) =>
    apiFetch<unknown>(`/api/v1/workspaces/${workspaceId}/export`),

  erase: (workspaceId: string, confirmation: string) =>
    apiFetch<WorkspaceErasure>(`/api/v1/workspaces/${workspaceId}`, {
      method: "DELETE",
      body: { confirmation },
    }),
};
