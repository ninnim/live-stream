"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Button, Field, inputClasses } from "@/components/ui/primitives";
import { ApiError, tokenStore } from "@/lib/api/client";
import { authApi } from "@/lib/api/live-sessions";
import { ssoApi } from "@/lib/api/sso";
import type { SsoDiscovery } from "@/lib/types";

export default function LoginPage() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Keyed by the address it was asked about, so editing the email stops showing a stale answer
  // without an effect having to clear it.
  const [discovered, setDiscovered] = useState<{ email: string; result: SsoDiscovery } | null>(null);

  const candidate = email.trim().toLowerCase();
  const looksComplete = candidate.includes("@") && !candidate.endsWith("@");
  const sso = discovered?.email === candidate ? discovered.result : null;

  // Asked once the address looks complete, and never blocking: an identity provider being down
  // must not stop somebody signing in with a password.
  useEffect(() => {
    if (!looksComplete) {
      return;
    }

    const controller = new AbortController();
    const timer = setTimeout(() => {
      void ssoApi
        .discover(candidate, controller.signal)
        .then((result) => setDiscovered({ email: candidate, result }))
        .catch(() => setDiscovered({ email: candidate, result: { available: false, workspaceName: null } }));
    }, 400);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [candidate, looksComplete]);

  const submit = async (event: React.FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);

    try {
      const auth = await authApi.login({ email, password });
      tokenStore.save(auth);
      router.push("/dashboard");
    } catch (loginError) {
      setError(
        loginError instanceof ApiError ? loginError.message : "We could not sign you in. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  };

  const startSso = async (): Promise<void> => {
    setBusy(true);
    setError(null);

    try {
      const start = await ssoApi.start(email.trim().toLowerCase());
      window.location.assign(start.authorizationUrl);
    } catch (startError) {
      setError(
        startError instanceof ApiError
          ? startError.message
          : "We could not reach your identity provider.",
      );
      setBusy(false);
    }
  };

  return (
    <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-6 px-6">
      <div>
        <h1 className="text-2xl font-semibold text-slate-50">Sign in</h1>
        <p className="mt-1 text-sm text-slate-400">Access your live sessions and studio.</p>
      </div>

      <form onSubmit={submit} className="flex flex-col gap-4">
        <Field label="Email">
          <input
            type="email"
            required
            autoComplete="email"
            className={inputClasses}
            value={email}
            onChange={(event) => setEmail(event.target.value)}
          />
        </Field>

        {sso?.available ? (
          <div className="rounded-lg bg-slate-900/60 p-3">
            <p className="text-sm text-slate-300">
              {sso.workspaceName
                ? `${sso.workspaceName} uses single sign-on.`
                : "This address uses single sign-on."}
            </p>
            <Button type="button" className="mt-2 w-full" disabled={busy} onClick={() => void startSso()}>
              Continue with single sign-on
            </Button>
          </div>
        ) : null}

        <Field label="Password">
          <input
            type="password"
            required
            autoComplete="current-password"
            className={inputClasses}
            value={password}
            onChange={(event) => setPassword(event.target.value)}
          />
        </Field>

        {error ? (
          <p role="alert" className="text-sm text-red-400">
            {error}
          </p>
        ) : null}

        <Button type="submit" disabled={busy}>
          {busy ? "Signing in…" : "Sign in"}
        </Button>
      </form>

      <p className="text-sm text-slate-500">
        No account?{" "}
        <Link href="/register" className="text-sky-400 underline-offset-4 hover:underline">
          Create one
        </Link>
        {" · "}
        {/*
          Carries whatever has been typed. Somebody reaching for this has already tried their
          password and failed; making them type their address again would be gratuitous.
        */}
        <Link
          href={candidate ? `/forgot-password?email=${encodeURIComponent(candidate)}` : "/forgot-password"}
          className="text-sky-400 underline-offset-4 hover:underline"
        >
          Forgot your password?
        </Link>
      </p>
    </main>
  );
}
