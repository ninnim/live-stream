"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { Button, Field, inputClasses } from "@/components/ui/primitives";
import { ApiError } from "@/lib/api/client";
import { liveSessionApi } from "@/lib/api/live-sessions";
import type { LiveSessionVisibility } from "@/lib/types";

export default function NewSessionPage() {
  const router = useRouter();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [visibility, setVisibility] = useState<LiveSessionVisibility>("UNLISTED");
  const [recordingEnabled, setRecordingEnabled] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: React.FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);

    try {
      const session = await liveSessionApi.create({
        title,
        description: description || null,
        visibility,
        recordingEnabled,
      });

      router.push(`/studio/${session.id}`);
    } catch (createError) {
      setError(
        createError instanceof ApiError
          ? createError.message
          : "We could not create the session. Please try again.",
      );
      setBusy(false);
    }
  };

  return (
    <main className="mx-auto flex w-full max-w-xl flex-col gap-6 px-4 py-10">
      <div>
        <h1 className="text-2xl font-semibold text-slate-50">New live session</h1>
        <p className="mt-1 text-sm text-slate-400">
          You will go straight to the studio, where you can preview and start broadcasting.
        </p>
      </div>

      <form onSubmit={submit} className="flex flex-col gap-4">
        <Field label="Title">
          <input
            required
            maxLength={200}
            className={inputClasses}
            value={title}
            onChange={(event) => setTitle(event.target.value)}
            placeholder="Product launch"
          />
        </Field>

        <Field label="Description" hint="Optional.">
          <textarea
            rows={3}
            maxLength={2000}
            className={inputClasses}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
          />
        </Field>

        <Field label="Visibility" hint="Unlisted streams are watchable by anyone with the link.">
          <select
            className={inputClasses}
            value={visibility}
            onChange={(event) => setVisibility(event.target.value as LiveSessionVisibility)}
          >
            <option value="PRIVATE">Private — only your workspace</option>
            <option value="UNLISTED">Unlisted — anyone with the link</option>
            <option value="PUBLIC">Public</option>
          </select>
        </Field>

        <label className="flex items-center gap-3 text-sm text-slate-300">
          <input
            type="checkbox"
            className="size-4 rounded border-slate-700 bg-slate-900"
            checked={recordingEnabled}
            onChange={(event) => setRecordingEnabled(event.target.checked)}
          />
          Record this session
        </label>

        {error ? (
          <p role="alert" className="text-sm text-red-400">
            {error}
          </p>
        ) : null}

        <Button type="submit" disabled={busy}>
          {busy ? "Creating…" : "Create and open studio"}
        </Button>
      </form>
    </main>
  );
}
