"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { Card } from "@/components/ui/primitives";
import { DataPanel } from "@/components/workspace/DataPanel";
import { LimitsPanel } from "@/components/workspace/LimitsPanel";
import { SsoPanel } from "@/components/workspace/SsoPanel";
import { UsagePanel } from "@/components/workspace/UsagePanel";
import { ApiError } from "@/lib/api/client";
import { authApi } from "@/lib/api/live-sessions";
import { workspaceApi } from "@/lib/api/workspace";
import type {
  CurrentUser,
  SsoConnection,
  UpdateWorkspaceLimits,
  UpsertSsoConnection,
  WorkspaceLimits,
  WorkspaceUsage,
} from "@/lib/types";

/** Roles that may change what the workspace is, as opposed to what happens inside it. */
const ADMIN_ROLES = new Set(["OWNER", "ADMIN"]);

/**
 * Workspace administration (implementation/phase-7-scale-security-and-globalization.md).
 *
 * The administrative sections are hidden rather than disabled for people who cannot use them: a
 * form that rejects every save is worse than one that was never offered. The API enforces the same
 * rules regardless — this page decides what to render, never what is allowed.
 */
export default function WorkspacePage() {
  const router = useRouter();
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [limits, setLimits] = useState<WorkspaceLimits | null>(null);
  const [usage, setUsage] = useState<WorkspaceUsage | null>(null);
  const [sso, setSso] = useState<SsoConnection | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    try {
      const currentUser = await authApi.me();
      const membership = currentUser.workspaces[0];

      if (!membership) {
        setError("You are not a member of any workspace.");
        return;
      }

      setUser(currentUser);

      const isAdmin = ADMIN_ROLES.has(membership.role.toUpperCase());

      // Limits and usage are readable by every member; the identity provider is not, so it is only
      // requested when it will be rendered.
      const [workspaceLimits, workspaceUsage, connection] = await Promise.all([
        workspaceApi.limits(membership.workspaceId),
        workspaceApi.usage(membership.workspaceId),
        isAdmin ? workspaceApi.sso(membership.workspaceId) : Promise.resolve(null),
      ]);

      setLimits(workspaceLimits);
      setUsage(workspaceUsage);
      setSso(connection);
      setError(null);
    } catch (loadError) {
      if (loadError instanceof ApiError && loadError.isAuthFailure) {
        router.push("/login");
        return;
      }

      setError("We could not load this workspace. Please refresh.");
    } finally {
      setLoading(false);
    }
  }, [router]);

  // Scheduled rather than awaited inside the effect, so the loaded state lands in its own commit.
  useEffect(() => {
    const timer = setTimeout(() => void load(), 0);
    return () => clearTimeout(timer);
  }, [load]);

  const membership = user?.workspaces[0];
  const isAdmin = membership ? ADMIN_ROLES.has(membership.role.toUpperCase()) : false;

  const saveLimits = async (update: UpdateWorkspaceLimits): Promise<void> => {
    if (!membership) return;
    setLimits(await workspaceApi.updateLimits(membership.workspaceId, update));
  };

  const saveSso = async (update: UpsertSsoConnection): Promise<void> => {
    if (!membership) return;
    setSso(await workspaceApi.saveSso(membership.workspaceId, update));
  };

  const removeSso = async (): Promise<void> => {
    if (!membership) return;
    await workspaceApi.removeSso(membership.workspaceId);
    setSso(null);
  };

  return (
    <main className="mx-auto flex min-h-screen max-w-3xl flex-col gap-6 px-6 py-10">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <h1 className="text-2xl font-semibold text-slate-50">
            {membership?.name ?? "Workspace"}
          </h1>
          <p className="mt-1 text-sm text-slate-400">Plan, usage, sign-in, and data.</p>
        </div>

        <Link href="/dashboard" className="text-sm text-sky-400 underline-offset-4 hover:underline">
          Back to sessions
        </Link>
      </div>

      {loading ? <Card>Loading…</Card> : null}

      {error ? (
        <Card>
          <p role="alert" className="text-sm text-red-400">
            {error}
          </p>
        </Card>
      ) : null}

      {usage ? <UsagePanel usage={usage} /> : null}

      {limits ? <LimitsPanel limits={limits} canEdit={isAdmin} onSave={saveLimits} /> : null}

      {isAdmin && limits ? (
        <SsoPanel
          connection={sso}
          allowed={limits.singleSignOnAllowed}
          onSave={saveSso}
          onRemove={removeSso}
        />
      ) : null}

      {isAdmin && membership ? (
        <DataPanel
          workspaceName={membership.name}
          onExport={() => workspaceApi.export(membership.workspaceId)}
          onErase={async (confirmation) => {
            const receipt = await workspaceApi.erase(membership.workspaceId, confirmation);
            router.push("/dashboard");
            return receipt;
          }}
        />
      ) : null}
    </main>
  );
}
