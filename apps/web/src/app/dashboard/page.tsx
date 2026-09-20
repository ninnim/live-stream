"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { Badge, Button, Card } from "@/components/ui/primitives";
import { ApiError, tokenStore } from "@/lib/api/client";
import { authApi, liveSessionApi } from "@/lib/api/live-sessions";
import { statusLabel, statusTone } from "@/lib/format";
import type { CurrentUser, LiveSession } from "@/lib/types";

export default function DashboardPage() {
  const router = useRouter();
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [sessions, setSessions] = useState<LiveSession[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    try {
      const [currentUser, page] = await Promise.all([
        authApi.me(),
        liveSessionApi.list({ pageSize: 25 }),
      ]);

      setUser(currentUser);
      setSessions(page.items);
      setError(null);
    } catch (loadError) {
      if (loadError instanceof ApiError && loadError.isAuthFailure) {
        router.push("/login");
        return;
      }

      setError("We could not load your sessions. Please refresh.");
    } finally {
      setLoading(false);
    }
  }, [router]);

  // Scheduled rather than called directly so the loaded state lands in its own commit instead of
  // cascading a render inside this effect.
  useEffect(() => {
    const timer = setTimeout(() => void load(), 0);
    return () => clearTimeout(timer);
  }, [load]);

  const signOut = async (): Promise<void> => {
    try {
      await authApi.logout(tokenStore.getRefreshToken());
    } catch {
      // Signing out locally matters more than the server round-trip succeeding.
    } finally {
      tokenStore.clear();
      router.push("/login");
    }
  };

  return (
    <main className="mx-auto flex w-full max-w-4xl flex-col gap-6 px-4 py-8">
      <header className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold text-slate-50">Live sessions</h1>
          {user ? <p className="mt-1 text-sm text-slate-500">Signed in as {user.email}</p> : null}
        </div>

        <div className="flex gap-3">
          <Link
            href="/sessions/new"
            className="rounded-lg bg-sky-500 px-4 py-2.5 text-sm font-semibold text-white hover:bg-sky-400"
          >
            New live session
          </Link>
          <Link
            href="/workspace"
            className="inline-flex items-center rounded-lg px-4 py-2.5 text-sm font-semibold text-slate-300 transition hover:bg-slate-800 hover:text-white"
          >
            Workspace
          </Link>
          <Button variant="ghost" onClick={() => void signOut()}>
            Sign out
          </Button>
        </div>
      </header>

      {error ? (
        <p role="alert" className="text-sm text-red-400">
          {error}
        </p>
      ) : null}

      {loading ? <p className="text-sm text-slate-500">Loading…</p> : null}

      {!loading && sessions.length === 0 ? (
        <Card className="text-center">
          <p className="text-slate-300">You have not created a live session yet.</p>
          <p className="mt-1 text-sm text-slate-500">
            Create one to open the studio and broadcast from your browser.
          </p>
        </Card>
      ) : null}

      <ul className="flex flex-col gap-3">
        {sessions.map((session) => (
          <li key={session.id}>
            <Card className="flex flex-wrap items-center justify-between gap-4">
              <div className="min-w-0">
                <p className="truncate font-medium text-slate-100">{session.title}</p>
                <p className="mt-1 text-xs text-slate-500">
                  Created {new Date(session.createdAt).toLocaleString()}
                  {session.recordingEnabled ? " · Recording on" : ""}
                </p>
              </div>

              <div className="flex items-center gap-3">
                <Badge tone={statusTone(session.status)} pulse={session.status === "LIVE"}>
                  {statusLabel(session.status)}
                </Badge>

                <Link
                  href={`/studio/${session.id}`}
                  className="rounded-lg bg-slate-800 px-4 py-2 text-sm font-semibold text-slate-100 ring-1 ring-inset ring-slate-700 hover:bg-slate-700"
                >
                  Open studio
                </Link>
              </div>
            </Card>
          </li>
        ))}
      </ul>
    </main>
  );
}
