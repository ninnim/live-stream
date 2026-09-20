import type { ButtonHTMLAttributes, ReactNode, SelectHTMLAttributes } from "react";
import type { Tone } from "@/lib/format";

export function cn(...classes: (string | false | null | undefined)[]): string {
  return classes.filter(Boolean).join(" ");
}

const TONE_CLASSES: Record<Tone, string> = {
  neutral: "bg-slate-800 text-slate-300 ring-slate-700",
  positive: "bg-emerald-950 text-emerald-300 ring-emerald-800",
  caution: "bg-amber-950 text-amber-300 ring-amber-800",
  critical: "bg-red-950 text-red-300 ring-red-800",
  live: "bg-red-600 text-white ring-red-500",
};

export function Badge({
  tone = "neutral",
  children,
  pulse = false,
}: {
  tone?: Tone;
  children: ReactNode;
  pulse?: boolean;
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold ring-1 ring-inset",
        TONE_CLASSES[tone],
      )}
    >
      {pulse ? <span className="size-2 animate-pulse rounded-full bg-current" aria-hidden="true" /> : null}
      {children}
    </span>
  );
}

type ButtonVariant = "primary" | "danger" | "secondary" | "ghost";

const BUTTON_CLASSES: Record<ButtonVariant, string> = {
  primary: "bg-sky-500 text-white hover:bg-sky-400 focus-visible:outline-sky-400",
  danger: "bg-red-600 text-white hover:bg-red-500 focus-visible:outline-red-500",
  secondary: "bg-slate-800 text-slate-100 hover:bg-slate-700 ring-1 ring-inset ring-slate-700",
  ghost: "text-slate-300 hover:bg-slate-800 hover:text-white",
};

export function Button({
  variant = "primary",
  className,
  children,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant }) {
  return (
    <button
      type="button"
      className={cn(
        "inline-flex items-center justify-center gap-2 rounded-lg px-4 py-2.5 text-sm font-semibold transition",
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2",
        "disabled:cursor-not-allowed disabled:opacity-50",
        BUTTON_CLASSES[variant],
        className,
      )}
      {...props}
    >
      {children}
    </button>
  );
}

export function Card({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <section
      className={cn("rounded-xl border border-slate-800 bg-slate-900/60 p-5 backdrop-blur", className)}
    >
      {children}
    </section>
  );
}

/** A labelled read-out, used for the studio's duration/viewers/health row. */
export function Metric({
  label,
  value,
  hint,
  tone = "neutral",
}: {
  label: string;
  value: ReactNode;
  /** One short line under the number: what it is out of, or what it means. */
  hint?: string;
  tone?: Tone;
}) {
  const valueTone: Record<Tone, string> = {
    neutral: "text-slate-100",
    positive: "text-emerald-400",
    caution: "text-amber-400",
    critical: "text-red-400",
    live: "text-red-400",
  };

  return (
    <div className="flex flex-col gap-1">
      <span className="text-xs font-medium uppercase tracking-wide text-slate-500">{label}</span>
      <span className={cn("font-mono text-lg font-semibold tabular-nums", valueTone[tone])}>{value}</span>
      {hint ? <span className="text-xs text-slate-500">{hint}</span> : null}
    </div>
  );
}

export function Select({
  label,
  className,
  ...props
}: SelectHTMLAttributes<HTMLSelectElement> & { label: string }) {
  return (
    <label className="flex flex-col gap-1.5 text-sm">
      <span className="font-medium text-slate-400">{label}</span>
      <select
        className={cn(
          "rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-slate-100",
          "focus:border-sky-500 focus:outline-none focus:ring-1 focus:ring-sky-500",
          "disabled:cursor-not-allowed disabled:opacity-50",
          className,
        )}
        {...props}
      />
    </label>
  );
}

export function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1.5 text-sm">
      <span className="font-medium text-slate-300">{label}</span>
      {children}
      {hint ? <span className="text-xs text-slate-500">{hint}</span> : null}
    </label>
  );
}

export const inputClasses =
  "rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-slate-100 placeholder:text-slate-600 focus:border-sky-500 focus:outline-none focus:ring-1 focus:ring-sky-500";
