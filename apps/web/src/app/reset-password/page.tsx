"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { Button, Field, inputClasses } from "@/components/ui/primitives";
import { ApiError } from "@/lib/api/client";
import { authApi } from "@/lib/api/live-sessions";

/** Matches the server, which refuses anything shorter. Checked here so a typo costs no round trip. */
const MINIMUM_LENGTH = 12;

/**
 * Choosing a new password from a reset link.
 *
 * The link is in the address bar, so it is in the browser's history and in the tab title's URL
 * — which is unavoidable, and is why the token is single-use and short-lived rather than why it
 * would be safe to make it neither.
 */
function ResetPasswordForm() {
  const router = useRouter();
  const params = useSearchParams();
  const token = params.get("token") ?? "";

  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  // Derived rather than held in state, so it cannot disagree with the fields it describes.
  const tooShort = password.length > 0 && password.length < MINIMUM_LENGTH;
  const mismatched = confirmation.length > 0 && confirmation !== password;
  const submittable =
    token.length > 0 && password.length >= MINIMUM_LENGTH && confirmation === password && !busy;

  const submit = async (event: React.FormEvent): Promise<void> => {
    event.preventDefault();
    if (!submittable) return;

    setBusy(true);
    setError(null);

    try {
      await authApi.resetPassword({ token, newPassword: password });
      setDone(true);
    } catch (resetError) {
      setError(
        resetError instanceof ApiError
          ? resetError.message
          : "We could not reach the server. Please try again.",
      );
      setBusy(false);
    }
  };

  if (!token) {
    return (
      <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-4 px-6">
        <h1 className="text-2xl font-semibold text-slate-50">This link is incomplete</h1>
        <p className="text-sm text-slate-400">
          Open the link from your email exactly as it was sent, or ask for a new one.
        </p>
        <Link href="/forgot-password" className="text-sm text-sky-400 underline-offset-4 hover:underline">
          Send a new link
        </Link>
      </main>
    );
  }

  if (done) {
    return (
      <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-4 px-6">
        <h1 className="text-2xl font-semibold text-slate-50">Password changed</h1>
        {/*
          Said plainly, because it is a consequence people need to know about rather than a detail:
          anyone signed in anywhere — including whoever prompted the reset — has been signed out.
        */}
        <p className="text-sm text-slate-400">
          You have been signed out everywhere, on every device. Sign in with your new password.
        </p>
        <Button onClick={() => router.push("/login")}>Go to sign in</Button>
      </main>
    );
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-6 px-6">
      <div>
        <h1 className="text-2xl font-semibold text-slate-50">Choose a new password</h1>
        <p className="mt-1 text-sm text-slate-400">
          At least {MINIMUM_LENGTH} characters. This signs you out everywhere else.
        </p>
      </div>

      <form onSubmit={submit} className="flex flex-col gap-4">
        <Field label="New password">
          <input
            type="password"
            required
            autoFocus
            autoComplete="new-password"
            className={inputClasses}
            value={password}
            onChange={(event) => setPassword(event.target.value)}
          />
        </Field>

        {tooShort ? (
          <p className="-mt-2 text-xs text-amber-300">
            {MINIMUM_LENGTH - password.length} more character
            {MINIMUM_LENGTH - password.length === 1 ? "" : "s"} needed.
          </p>
        ) : null}

        <Field label="Confirm new password">
          <input
            type="password"
            required
            autoComplete="new-password"
            className={inputClasses}
            value={confirmation}
            onChange={(event) => setConfirmation(event.target.value)}
          />
        </Field>

        {mismatched ? (
          <p className="-mt-2 text-xs text-amber-300">These do not match.</p>
        ) : null}

        {error ? (
          <div role="alert" className="rounded-lg border border-red-500/30 bg-red-500/10 p-3">
            <p className="text-sm text-red-300">{error}</p>
            <Link
              href="/forgot-password"
              className="mt-1 inline-block text-sm text-sky-400 underline-offset-4 hover:underline"
            >
              Send a new link
            </Link>
          </div>
        ) : null}

        <Button type="submit" disabled={!submittable}>
          {busy ? "Saving…" : "Change password"}
        </Button>
      </form>
    </main>
  );
}

export default function ResetPasswordPage() {
  return (
    <Suspense>
      <ResetPasswordForm />
    </Suspense>
  );
}
