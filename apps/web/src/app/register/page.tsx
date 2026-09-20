"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { Button, Field, inputClasses } from "@/components/ui/primitives";
import { ApiError, tokenStore } from "@/lib/api/client";
import { authApi } from "@/lib/api/live-sessions";

export default function RegisterPage() {
  const router = useRouter();
  const [displayName, setDisplayName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [workspaceName, setWorkspaceName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: React.FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);

    try {
      const auth = await authApi.register({
        email,
        password,
        displayName,
        workspaceName: workspaceName || undefined,
      });
      tokenStore.save(auth);
      router.push("/dashboard");
    } catch (registerError) {
      setError(
        registerError instanceof ApiError
          ? registerError.message
          : "We could not create your account. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-6 px-6 py-12">
      <div>
        <h1 className="text-2xl font-semibold text-slate-50">Create your account</h1>
        <p className="mt-1 text-sm text-slate-400">You will get a workspace to host your live sessions.</p>
      </div>

      <form onSubmit={submit} className="flex flex-col gap-4">
        <Field label="Your name">
          <input
            required
            autoComplete="name"
            className={inputClasses}
            value={displayName}
            onChange={(event) => setDisplayName(event.target.value)}
          />
        </Field>

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

        <Field label="Password" hint="At least 12 characters.">
          <input
            type="password"
            required
            minLength={12}
            autoComplete="new-password"
            className={inputClasses}
            value={password}
            onChange={(event) => setPassword(event.target.value)}
          />
        </Field>

        <Field label="Workspace name" hint="Optional — we will name one after you if you skip this.">
          <input
            className={inputClasses}
            value={workspaceName}
            onChange={(event) => setWorkspaceName(event.target.value)}
          />
        </Field>

        {error ? (
          <p role="alert" className="text-sm text-red-400">
            {error}
          </p>
        ) : null}

        <Button type="submit" disabled={busy}>
          {busy ? "Creating account…" : "Create account"}
        </Button>
      </form>

      <p className="text-sm text-slate-500">
        Already registered?{" "}
        <Link href="/login" className="text-sky-400 underline-offset-4 hover:underline">
          Sign in
        </Link>
      </p>
    </main>
  );
}
