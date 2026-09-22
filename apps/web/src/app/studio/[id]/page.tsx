import { StudioShell } from "@/components/studio/StudioShell";

export default async function StudioPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <StudioShell sessionId={id} />;
}
