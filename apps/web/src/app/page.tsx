import Link from "next/link";

export default function HomePage() {
  return (
    <main className="mx-auto flex min-h-screen max-w-3xl flex-col justify-center gap-8 px-6 py-16">
      <div>
        <p className="text-xs font-medium uppercase tracking-wide text-sky-400">Live Streaming Platform</p>
        <h1 className="mt-3 text-4xl font-semibold text-slate-50">Go live from your browser.</h1>
        <p className="mt-4 max-w-xl text-slate-400">
          Create a session, pick your camera and microphone, and start broadcasting. No external
          encoder, no extra software to install.
        </p>
      </div>

      <div className="flex flex-wrap gap-3">
        <Link
          href="/login"
          className="rounded-lg bg-sky-500 px-5 py-2.5 text-sm font-semibold text-white hover:bg-sky-400"
        >
          Sign in
        </Link>
        <Link
          href="/register"
          className="rounded-lg bg-slate-800 px-5 py-2.5 text-sm font-semibold text-slate-100 ring-1 ring-inset ring-slate-700 hover:bg-slate-700"
        >
          Create an account
        </Link>
      </div>
    </main>
  );
}
