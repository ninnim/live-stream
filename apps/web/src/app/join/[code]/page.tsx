import { JoinClient } from "@/components/device/JoinClient";

export const metadata = {
  title: "Join a live session",
};

/**
 * Where a scanned QR code lands.
 *
 * The code is a path segment rather than a query string so it survives being copied, shared and
 * re-opened, and so it never ends up in a referrer header on an outbound link.
 */
export default async function JoinWithCodePage({ params }: { params: Promise<{ code: string }> }) {
  const { code } = await params;

  return <JoinClient initialCode={decodeURIComponent(code)} />;
}
