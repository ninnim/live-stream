"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { Button, Field, inputClasses } from "@/components/ui/primitives";
import { authApi } from "@/lib/api/live-sessions";

/**
 * Asking for a reset link.
 *
 * The page says the same thing whatever happened, because the server answers the same way whatever
 * happened. Confirming that an address is or is not registered would make this the easiest place on
 * the platform to enumerate its users, and a page that said "no account with that address" would
 * undo the server's care in one line of copy.
 */
function ForgotPasswordForm() {
  const params = useSearchParams();
  const [email, setEmail] = useState(params.get("email") ?? "");
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (event: React.FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);

    try {
      await authApi.forgotPassword({ email: email.trim() });
      setSent(true);
    } catch {
      // Only a transport failure can land here — the server accepts every well-formed request.
      // Worth reporting, because "nothing happened" would otherwise look like a sent email.
      setError("We could not reach the server. Please try again.");
    } finally {
      setBusy(false);
    }
  };

  if (sent) {
    return (
      <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-6 px-6">
        <div>
          <h1 className="text-2xl font-semibold text-slate-50">Check your email</h1>
          <p className="mt-2 text-sm text-slate-400">
            If <span className="text-slate-200">{email.trim()}</span> has an account, a link to
            choose a new password is on its way. It works once and expires in an hour.
          </p>
          <p className="mt-2 text-sm text-slate-500">
            Nothing arrived? Check your spam folder, then{" "}
            <button
              type="button"
              onClick={() => setSent(false)}
              className="text-sky-400 underline-offset-4 hover:underline"
            >
              try again
            </button>
            .
          </p>
        </div>

        <Link href="/login" className="text-sm text-sky-400 underline-offset-4 hover:underline">
          Back to sign in
        </Link>
      </main>
    );
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-6 px-6">
      <div>
        <h1 className="text-2xl font-semibold text-slate-50">Forgot your password?</h1>
        <p className="mt-1 text-sm text-slate-400">
          Enter your email address and we will send you a link to choose a new one.
        </p>
      </div>

      <form onSubmit={submit} className="flex flex-col gap-4">
        <Field label="Email">
          <input
            type="email"
            required
            autoFocus
            autoComplete="email"
            className={inputClasses}
            value={email}
            onChange={(event) => setEmail(event.target.value)}
          />
        </Field>

        {error ? (
          <p role="alert" className="text-sm text-red-400">
            {error}
          </p>
        ) : null}

        <Button type="submit" disabled={busy}>
          {busy ? "Sending…" : "Send reset link"}
        </Button>
      </form>

      <p className="text-sm text-slate-500">
        Remembered it?{" "}
        <Link href="/login" className="text-sky-400 underline-offset-4 hover:underline">
          Sign in
        </Link>
      </p>
    </main>
  );
}

export default function ForgotPasswordPage() {
  // `useSearchParams` needs a boundary, or the whole route opts out of static rendering.
  return (
    <Suspense>
      <ForgotPasswordForm />
    </Suspense>
  );
}
