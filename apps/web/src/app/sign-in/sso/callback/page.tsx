"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";
import { Card } from "@/components/ui/primitives";
import { ApiError, tokenStore } from "@/lib/api/client";
import { ssoApi } from "@/lib/api/sso";

/**
 * Where an identity provider sends the browser back to.
 *
 * The provider's redirect lands here, on the web app, and this page posts the authorization code to
 * the API — which answers with tokens in a response body. Having the API receive the redirect and
 * bounce back with tokens in the URL would be simpler and would write them into browser history and
 * every proxy log on the way.
 */
export default function SsoCallbackPage() {
  return (
    <Suspense fallback={<Shell>Signing you in…</Shell>}>
      <SsoCallback />
    </Suspense>
  );
}

function SsoCallback() {
  const router = useRouter();
  const params = useSearchParams();
  const [exchangeError, setExchangeError] = useState<string | null>(null);

  // The code is single use: React's development double-effect would spend it on the first run and
  // fail on the second, which looks exactly like a broken sign-in.
  const exchanged = useRef(false);

  const state = params.get("state");
  const code = params.get("code");

  // Derived rather than held in state: what the URL says is knowable during render, and putting it
  // in an effect would only make the page render once as "signing you in" before contradicting
  // itself.
  const linkError = params.get("error")
    ? "Your identity provider cancelled that sign-in."
    : !state || !code
      ? "That sign-in link is incomplete. Start again from the sign-in page."
      : null;

  useEffect(() => {
    if (linkError || !state || !code || exchanged.current) {
      return;
    }

    exchanged.current = true;

    void (async () => {
      try {
        const auth = await ssoApi.complete(state, code);
        tokenStore.save(auth);
        router.replace("/dashboard");
      } catch (completeError) {
        setExchangeError(
          completeError instanceof ApiError
            ? completeError.message
            : "We could not complete that sign-in.",
        );
      }
    })();
  }, [linkError, state, code, router]);

  const error = linkError ?? exchangeError;

  if (error) {
    return (
      <Shell>
        <p role="alert" className="text-sm text-red-400">
          {error}
        </p>
        <Link
          href="/login"
          className="mt-3 inline-block text-sm text-sky-400 underline-offset-4 hover:underline"
        >
          Back to sign in
        </Link>
      </Shell>
    );
  }

  return <Shell>Signing you in…</Shell>;
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center px-6">
      <Card>{children}</Card>
    </main>
  );
}
