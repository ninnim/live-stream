"use client";

import { useCallback, useRef, useState } from "react";
import { Button, Card, Field, Select, inputClasses } from "@/components/ui/primitives";
import type { UseProgramResult } from "@/hooks/useProgram";
import type { UseStudioConfigResult } from "@/hooks/useStudioConfig";
import type { LogoPosition } from "@/lib/types";

/** Mirrors the server's cap. Checked here so the failure arrives before the upload does. */
const MAX_LOGO_BYTES = 256 * 1024;

const POSITIONS: { value: LogoPosition; label: string }[] = [
  { value: "TopLeft", label: "Top left" },
  { value: "TopRight", label: "Top right" },
  { value: "BottomLeft", label: "Bottom left" },
  { value: "BottomRight", label: "Bottom right" },
];

interface OverlayPanelProps {
  config: UseStudioConfigResult;
  program: UseProgramResult;
  sessionIsEnded: boolean;
}

/**
 * Captions and branding.
 *
 * The caption is a live control — typed and put up during the show, like a fader — so it lives in
 * the studio's memory rather than in the database. Branding is configuration and is saved, because
 * a watermark is a property of the show rather than a decision made in the moment.
 */
export function OverlayPanel({ config, program, sessionIsEnded }: OverlayPanelProps) {
  const branding = config.branding;
  const fileRef = useRef<HTMLInputElement | null>(null);
  const [logoError, setLogoError] = useState<string | null>(null);

  /**
   * Continuous controls are held locally and committed when the operator lets go.
   *
   * A colour picker and a slider both fire on every step of a drag. Saving each one would send a
   * request per pixel of travel, and the responses can land out of order — leaving the stored
   * colour a shade the operator passed through rather than the one they chose.
   */
  const accent = useCommitted(branding?.accentColor ?? "#0EA5E9", (value) =>
    config.saveBranding({ replaceLogo: false, accentColor: value }),
  );

  const opacity = useCommitted(branding?.logoOpacityPercent ?? 80, (value) =>
    config.saveBranding({ replaceLogo: false, logoOpacityPercent: value }),
  );

  const onPickLogo = useCallback(
    async (file: File | undefined) => {
      setLogoError(null);
      if (!file) return;

      if (!["image/png", "image/jpeg", "image/webp"].includes(file.type)) {
        setLogoError("Choose a PNG, JPEG or WebP image.");
        return;
      }

      const dataUri = await readAsDataUri(file);

      // Checked against the encoded length, which is what is stored and sent — base64 is about a
      // third larger than the file, so checking the file size would let some through.
      if (dataUri.length > MAX_LOGO_BYTES) {
        setLogoError("That image is too large. Use one under 180 KB.");
        return;
      }

      await config.saveBranding({ replaceLogo: true, logoDataUri: dataUri, showLogo: true });
    },
    [config],
  );

  return (
    <Card className="flex flex-col gap-5">
      <div>
        <h2 className="text-sm font-semibold text-slate-100">Overlays</h2>
        <p className="mt-0.5 text-xs text-slate-500">
          Captions go up and down live. Branding is saved with the session.
        </p>
      </div>

      {/* Caption — a live control. */}
      <div className="flex flex-col gap-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Caption">
            <input
              className={inputClasses}
              value={program.lowerThird.title}
              aria-label="Lower third title"
              placeholder="Ada Lovelace"
              maxLength={120}
              onChange={(event) =>
                program.setLowerThird({ ...program.lowerThird, title: event.target.value })
              }
            />
          </Field>

          <Field label="Subtitle">
            <input
              className={inputClasses}
              value={program.lowerThird.subtitle}
              aria-label="Lower third subtitle"
              placeholder="Analytical Engines"
              maxLength={120}
              onChange={(event) =>
                program.setLowerThird({ ...program.lowerThird, subtitle: event.target.value })
              }
            />
          </Field>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          <Button
            variant={program.lowerThirdVisible ? "danger" : "primary"}
            aria-pressed={program.lowerThirdVisible}
            disabled={sessionIsEnded || program.lowerThird.title.trim().length === 0}
            onClick={() => program.setLowerThirdVisible(!program.lowerThirdVisible)}
          >
            {program.lowerThirdVisible ? "Hide caption" : "Show caption"}
          </Button>

          {/*
            Showing a caption is itself enough to start the compositor, so this reports what is
            happening rather than warning about a prerequisite the operator has to arrange.
          */}
          <p className="text-xs text-slate-500">
            {program.composing
              ? "Captions are drawn into the program."
              : "Showing a caption starts composing the program."}
          </p>
        </div>
      </div>

      {/* Branding — saved configuration. */}
      <div className="flex flex-col gap-3 border-t border-slate-800 pt-4">
        <div className="flex flex-wrap items-center gap-3">
          <input
            ref={fileRef}
            type="file"
            accept="image/png,image/jpeg,image/webp"
            className="hidden"
            aria-label="Logo file"
            onChange={(event) => void onPickLogo(event.target.files?.[0])}
          />

          <Button variant="secondary" onClick={() => fileRef.current?.click()} disabled={config.busy}>
            {branding?.logoDataUri ? "Replace logo" : "Upload logo"}
          </Button>

          {branding?.logoDataUri ? (
            <>
              {/* eslint-disable-next-line @next/next/no-img-element -- a data URI, not a remote asset */}
              <img
                src={branding.logoDataUri}
                alt="Session logo"
                className="h-8 w-auto rounded bg-slate-800 p-1"
              />
              <Button
                variant="ghost"
                disabled={config.busy}
                onClick={() => void config.saveBranding({ replaceLogo: true, logoDataUri: null })}
              >
                Remove
              </Button>
            </>
          ) : null}
        </div>

        {logoError ? (
          <p role="alert" className="text-xs text-red-300">
            {logoError}
          </p>
        ) : null}

        <div className="grid gap-3 sm:grid-cols-3">
          <Select
            label="Logo position"
            value={branding?.logoPosition ?? "TopRight"}
            disabled={config.busy || !branding?.logoDataUri}
            onChange={(event) =>
              void config.saveBranding({
                replaceLogo: false,
                logoPosition: event.target.value as LogoPosition,
              })
            }
          >
            {POSITIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>

          <label className="flex flex-col gap-1.5 text-sm">
            <span className="font-medium text-slate-400">Logo opacity</span>
            <input
              type="range"
              min={10}
              max={100}
              step={5}
              value={opacity.value}
              aria-label="Logo opacity"
              disabled={!branding?.logoDataUri}
              onChange={(event) => opacity.set(Number(event.target.value))}
              onPointerUp={opacity.commit}
              onBlur={opacity.commit}
              className="mt-2"
            />
          </label>

          <label className="flex flex-col gap-1.5 text-sm">
            <span className="font-medium text-slate-400">Accent colour</span>
            <input
              type="color"
              value={accent.value}
              aria-label="Accent colour"
              onChange={(event) => accent.set(event.target.value)}
              onBlur={accent.commit}
              className="h-10 w-full cursor-pointer rounded-lg border border-slate-700 bg-slate-900"
            />
          </label>
        </div>

        <label className="flex items-center gap-2 text-sm text-slate-300">
          <input
            type="checkbox"
            checked={branding?.showLogo ?? false}
            disabled={config.busy || !branding?.logoDataUri}
            onChange={(event) =>
              void config.saveBranding({ replaceLogo: false, showLogo: event.target.checked })
            }
            className="h-4 w-4 rounded border-slate-700 bg-slate-900"
          />
          Show the logo on the program
        </label>
      </div>
    </Card>
  );
}

/**
 * A control the operator drags, saved when they let go.
 *
 * Follows the server's value while idle, so a change made in another control room shows up, but
 * never overwrites what someone is in the middle of setting.
 */
function useCommitted<T>(serverValue: T, save: (value: T) => Promise<void> | void) {
  const [draft, setDraft] = useState<T | null>(null);

  const value = draft ?? serverValue;

  const commit = useCallback(() => {
    setDraft((current) => {
      if (current !== null && current !== serverValue) void save(current);
      return null;
    });
  }, [save, serverValue]);

  return { value, set: setDraft, commit };
}

function readAsDataUri(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(new Error("Could not read that file."));
    reader.readAsDataURL(file);
  });
}
