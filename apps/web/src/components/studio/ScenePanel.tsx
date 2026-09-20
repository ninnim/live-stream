"use client";

import { useState } from "react";
import { Badge, Button, Card, Field, Select, inputClasses } from "@/components/ui/primitives";
import type { UseProgramResult } from "@/hooks/useProgram";
import type { UseStudioConfigResult } from "@/hooks/useStudioConfig";
import type { SceneLayout, SessionScene, SessionSource } from "@/lib/types";

const LAYOUT_OPTIONS: { value: SceneLayout; label: string }[] = [
  { value: "Solo", label: "Full screen" },
  { value: "SideBySide", label: "Side by side" },
  { value: "PictureInPicture", label: "Picture in picture" },
];

interface ScenePanelProps {
  config: UseStudioConfigResult;
  program: UseProgramResult;
  sources: SessionSource[];
  sessionIsEnded: boolean;
}

/**
 * Prepared shots.
 *
 * A scene is a name for an arrangement — layout, sources, caption — that an operator sets up before
 * a show and recalls during it with one press. Recalling one asks the studio to arrange itself; it
 * does not force anything on air, because a camera that has stopped sending cannot go on air and a
 * scene saved an hour ago knows nothing about that.
 */
export function ScenePanel({ config, program, sources, sessionIsEnded }: ScenePanelProps) {
  const [adding, setAdding] = useState(false);

  const mediaSources = sources.filter((source) => source.contributesMedia && source.status !== "Revoked");

  return (
    <Card className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-100">Scenes</h2>
          <p className="mt-0.5 text-xs text-slate-500">
            Prepared shots. Press a number key to recall one.
          </p>
        </div>

        <Button variant="secondary" onClick={() => setAdding((open) => !open)} disabled={sessionIsEnded}>
          {adding ? "Cancel" : "Save current shot"}
        </Button>
      </div>

      {config.error ? (
        <p role="alert" className="rounded-lg bg-red-950/60 px-3 py-2 text-sm text-red-300">
          {config.error}
        </p>
      ) : null}

      {adding ? (
        <SceneForm
          sources={mediaSources}
          busy={config.busy}
          initial={{
            name: "",
            layout: layoutToScene(program.layout),
            primarySourceId: sources.find((source) => source.isProgram)?.id ?? null,
            secondarySourceId: program.secondarySourceId,
            lowerThirdTitle: program.lowerThird.title || null,
            lowerThirdSubtitle: program.lowerThird.subtitle || null,
          }}
          onCancel={() => setAdding(false)}
          onSave={async (scene) => {
            const created = await config.addScene(scene);
            if (created) setAdding(false);
          }}
        />
      ) : null}

      {config.scenes.length === 0 && !adding ? (
        <p className="text-sm text-slate-500">
          No scenes yet. Arrange the shot you want, then save it here to bring it back with one press.
        </p>
      ) : null}

      <ul className="flex flex-col gap-2">
        {config.scenes.map((scene, index) => (
          <SceneRow
            key={scene.id}
            scene={scene}
            index={index}
            sources={mediaSources}
            busy={config.busy}
            disabled={sessionIsEnded}
            onRecall={() => program.applyScene(scene)}
            onDelete={() => void config.deleteScene(scene.id)}
          />
        ))}
      </ul>
    </Card>
  );
}

function SceneRow({
  scene,
  index,
  sources,
  busy,
  disabled,
  onRecall,
  onDelete,
}: {
  scene: SessionScene;
  index: number;
  sources: SessionSource[];
  busy: boolean;
  disabled: boolean;
  onRecall: () => void;
  onDelete: () => void;
}) {
  const primary = sources.find((source) => source.id === scene.primarySourceId);
  const missing = scene.primarySourceId !== null && primary === undefined;

  return (
    <li className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-900/40 p-3">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          {/* The shortcut is shown on the scene rather than in a help panel nobody opens. */}
          {index < 9 ? <Badge tone="neutral">{index + 1}</Badge> : null}
          <p className="truncate text-sm font-medium text-slate-200">{scene.name}</p>
        </div>
        <p className="mt-0.5 truncate text-xs text-slate-500">
          {LAYOUT_OPTIONS.find((option) => option.value === scene.layout)?.label ?? scene.layout}
          {primary ? ` · ${primary.displayName}` : ""}
          {scene.lowerThirdTitle ? ` · “${scene.lowerThirdTitle}”` : ""}
        </p>
        {missing ? (
          <p className="mt-1 text-xs text-amber-300">
            The camera this scene used has been removed. Recalling it keeps the current shot.
          </p>
        ) : null}
      </div>

      <div className="flex gap-2">
        <Button variant="secondary" onClick={onRecall} disabled={disabled}>
          Recall
        </Button>
        <Button variant="ghost" onClick={onDelete} disabled={busy || disabled}>
          Delete
        </Button>
      </div>
    </li>
  );
}

interface SceneDraft {
  name: string;
  layout: SceneLayout;
  primarySourceId: string | null;
  secondarySourceId: string | null;
  lowerThirdTitle: string | null;
  lowerThirdSubtitle: string | null;
}

function SceneForm({
  sources,
  initial,
  busy,
  onSave,
  onCancel,
}: {
  sources: SessionSource[];
  initial: SceneDraft;
  busy: boolean;
  onSave: (scene: SceneDraft) => void;
  onCancel: () => void;
}) {
  const [draft, setDraft] = useState<SceneDraft>(initial);
  const twoSource = draft.layout !== "Solo";

  return (
    <form
      className="flex flex-col gap-3 rounded-xl border border-slate-800 bg-slate-900/60 p-4"
      onSubmit={(event) => {
        event.preventDefault();
        onSave(draft);
      }}
    >
      <Field label="Scene name">
        <input
          className={inputClasses}
          value={draft.name}
          aria-label="Scene name"
          placeholder="Opening"
          maxLength={60}
          onChange={(event) => setDraft({ ...draft, name: event.target.value })}
        />
      </Field>

      <div className="grid gap-3 sm:grid-cols-2">
        <Select
          label="Layout"
          value={draft.layout}
          onChange={(event) => setDraft({ ...draft, layout: event.target.value as SceneLayout })}
        >
          {LAYOUT_OPTIONS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>

        <Select
          label="On air"
          value={draft.primarySourceId ?? ""}
          onChange={(event) => setDraft({ ...draft, primarySourceId: event.target.value || null })}
        >
          <option value="">Keep whatever is on air</option>
          {sources.map((source) => (
            <option key={source.id} value={source.id}>
              {source.displayName}
            </option>
          ))}
        </Select>
      </div>

      {twoSource ? (
        <Select
          label="Second source"
          value={draft.secondarySourceId ?? ""}
          onChange={(event) => setDraft({ ...draft, secondarySourceId: event.target.value || null })}
        >
          <option value="">Whichever is available</option>
          {sources
            .filter((source) => source.id !== draft.primarySourceId)
            .map((source) => (
              <option key={source.id} value={source.id}>
                {source.displayName}
              </option>
            ))}
        </Select>
      ) : null}

      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Caption">
          <input
            className={inputClasses}
            value={draft.lowerThirdTitle ?? ""}
            aria-label="Caption"
            placeholder="Ada Lovelace"
            maxLength={120}
            onChange={(event) => setDraft({ ...draft, lowerThirdTitle: event.target.value || null })}
          />
        </Field>

        <Field label="Caption subtitle">
          <input
            className={inputClasses}
            value={draft.lowerThirdSubtitle ?? ""}
            aria-label="Caption subtitle"
            placeholder="Analytical Engines"
            maxLength={120}
            onChange={(event) => setDraft({ ...draft, lowerThirdSubtitle: event.target.value || null })}
          />
        </Field>
      </div>

      <div className="flex gap-2">
        <Button type="submit" disabled={busy || draft.name.trim().length === 0}>
          {busy ? "Saving…" : "Save scene"}
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

function layoutToScene(layout: UseProgramResult["layout"]): SceneLayout {
  if (layout === "side-by-side") return "SideBySide";
  if (layout === "picture-in-picture") return "PictureInPicture";
  return "Solo";
}
