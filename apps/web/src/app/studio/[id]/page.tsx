import { LiveStudio } from "@/components/studio/LiveStudio";

export default async function StudioPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <LiveStudio sessionId={id} />;
}
