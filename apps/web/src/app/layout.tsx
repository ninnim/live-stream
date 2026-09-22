import type { Metadata, Viewport } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Live Studio",
  description: "Broadcast live from your browser — no external encoder required.",
  // Makes the broadcaster installable from a phone's browser. Installed, it launches without the
  // browser chrome that eats a fifth of the viewfinder, and the session survives the address bar
  // being gone. Nothing about broadcasting depends on it (ADR 0021).
  manifest: "/manifest.webmanifest",
  applicationName: "Live Studio",
  appleWebApp: {
    capable: true,
    title: "Live Studio",
    // The status bar sits over the viewfinder rather than above it, which is why the mobile
    // broadcaster pads its controls with the safe-area insets.
    statusBarStyle: "black-translucent",
  },
  icons: {
    icon: [{ url: "/icons/icon-192.png", sizes: "192x192", type: "image/png" }],
    apple: [{ url: "/icons/icon-192.png", sizes: "192x192", type: "image/png" }],
  },
};

/**
 * `viewportFit: "cover"` puts the page under the notch and the home indicator, which is what a
 * full-bleed viewfinder needs — and what makes `env(safe-area-inset-*)` return anything other than
 * zero, so the controls that use it stay clear of both.
 *
 * `maximumScale` is not set: pinch-zoom is left working. A viewfinder is not a reason to take
 * zooming away from somebody who needs it to read the screen.
 */
export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  viewportFit: "cover",
  themeColor: "#020617",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body className="min-h-screen bg-slate-950 text-slate-200 antialiased">{children}</body>
    </html>
  );
}
