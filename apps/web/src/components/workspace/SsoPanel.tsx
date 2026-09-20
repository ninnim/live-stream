"use client";

import { useState } from "react";
import { Badge, Button, Card, Field, Select, inputClasses } from "@/components/ui/primitives";
import type { SsoConnection, UpsertSsoConnection } from "@/lib/types";

/** Roles single sign-on may grant. Owner and Admin are absent because the API refuses them. */
const ASSIGNABLE_ROLES = ["Producer", "Host", "Moderator", "Analyst", "Viewer"];

/**
 * Enterprise single sign-on.
 *
 * Two things this panel has to communicate honestly, because both surprise people:
 * a claimed domain does nothing until an operator verifies it, and the client secret is write-only
 * — it goes in and is never shown again, exactly like a destination's stream key.
 */
export function SsoPanel({
  connection,
  allowed,
  onSave,
  onRemove,
}: {
  connection: SsoConnection | null;
  allowed: boolean;
  onSave: (update: UpsertSsoConnection) => Promise<void>;
  onRemove: () => Promise<void>;
}) {
  const [issuer, setIssuer] = useState(connection?.issuer ?? "");
  const [clientId, setClientId] = useState(connection?.clientId ?? "");
  const [clientSecret, setClientSecret] = useState("");
  const [domains, setDomains] = useState(connection?.domains.map((d) => d.domain).join(", ") ?? "");
  const [defaultRole, setDefaultRole] = useState(titleCase(connection?.defaultRole ?? "Host"));
  const [enabled, setEnabled] = useState(connection?.enabled ?? true);
  const [jit, setJit] = useState(connection?.jitProvisioning ?? true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!allowed) {
    return (
      <Card>
        <h2 className="text-lg font-semibold text-slate-100">Single sign-on</h2>
        <p className="mt-1 text-sm text-slate-400">
          Available on the Business and Enterprise plans.
        </p>
      </Card>
    );
  }

  const submit = async (event: React.FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);

    try {
      await onSave({
        issuer: issuer.trim(),
        clientId: clientId.trim(),
        // Empty means unchanged. Sending "" would blank a working secret every time somebody
        // edited an unrelated field.
        clientSecret: clientSecret.trim() === "" ? null : clientSecret.trim(),
        enabled,
        jitProvisioning: jit,
        defaultRole,
        domains: domains
          .split(/[,\s]+/)
          .map((value) => value.trim())
          .filter((value) => value.length > 0),
      });
      setClientSecret("");
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "We could not save that connection.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-lg font-semibold text-slate-100">Single sign-on</h2>
        {connection ? (
          <Badge tone={connection.usable ? "positive" : "caution"}>
            {connection.usable ? "Active" : "Awaiting verification"}
          </Badge>
        ) : null}
      </div>

      {connection && !connection.usable ? (
        <p className="mt-2 rounded-lg bg-amber-500/10 p-3 text-sm text-amber-200">
          Nobody can sign in yet. Ask your platform operator to verify a domain below — whoever holds
          a domain decides who may sign in with an address there, so it is not self-service.
        </p>
      ) : null}

      <form onSubmit={submit} className="mt-4 flex flex-col gap-4">
        <Field label="Issuer" hint="The OpenID Connect issuer URL. Must be https.">
          <input
            type="url"
            required
            placeholder="https://login.microsoftonline.com/…/v2.0"
            className={inputClasses}
            value={issuer}
            onChange={(event) => setIssuer(event.target.value)}
          />
        </Field>

        <Field label="Client ID">
          <input
            type="text"
            required
            className={inputClasses}
            value={clientId}
            onChange={(event) => setClientId(event.target.value)}
          />
        </Field>

        <Field
          label="Client secret"
          hint={connection ? "Leave empty to keep the stored secret." : "Stored encrypted; never shown again."}
        >
          <input
            type="password"
            autoComplete="off"
            required={!connection}
            className={inputClasses}
            value={clientSecret}
            onChange={(event) => setClientSecret(event.target.value)}
          />
        </Field>

        <Field label="Email domains" hint="Comma separated. Each one needs operator verification.">
          <input
            type="text"
            required
            placeholder="example.com, example.co.uk"
            className={inputClasses}
            value={domains}
            onChange={(event) => setDomains(event.target.value)}
          />
        </Field>

        <Select
          label="Role for new members"
          value={defaultRole}
          onChange={(event) => setDefaultRole(event.target.value)}
        >
          {ASSIGNABLE_ROLES.map((role) => (
            <option key={role} value={role}>
              {role}
            </option>
          ))}
        </Select>

        <label className="flex items-center gap-2 text-sm text-slate-300">
          <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />
          Allow sign-in through this provider
        </label>

        <label className="flex items-center gap-2 text-sm text-slate-300">
          <input type="checkbox" checked={jit} onChange={(event) => setJit(event.target.checked)} />
          Create accounts on first sign-in
        </label>

        {connection ? (
          <div className="rounded-lg bg-slate-900/60 p-3 text-sm">
            <p className="font-medium text-slate-300">Redirect URI</p>
            <code className="mt-1 block break-all text-xs text-slate-400">{connection.redirectUri}</code>

            <p className="mt-3 font-medium text-slate-300">Domains</p>
            <ul className="mt-1 space-y-1">
              {connection.domains.map((domain) => (
                <li key={domain.domain} className="flex items-center gap-2 text-xs">
                  <span className="text-slate-400">{domain.domain}</span>
                  <Badge tone={domain.verified ? "positive" : "caution"}>
                    {domain.verified ? "Verified" : "Unverified"}
                  </Badge>
                </li>
              ))}
            </ul>
          </div>
        ) : null}

        {error ? (
          <p role="alert" className="text-sm text-red-400">
            {error}
          </p>
        ) : null}

        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={busy}>
            {busy ? "Saving…" : connection ? "Save changes" : "Set up single sign-on"}
          </Button>

          {connection ? (
            <Button type="button" variant="ghost" disabled={busy} onClick={() => void onRemove()}>
              Remove
            </Button>
          ) : null}
        </div>
      </form>
    </Card>
  );
}

function titleCase(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1).toLowerCase();
}
