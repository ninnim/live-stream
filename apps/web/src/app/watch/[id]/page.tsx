import { WatchClient } from "./WatchClient";

export default async function WatchPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <WatchClient sessionId={id} />;
}
