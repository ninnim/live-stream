import { JoinClient } from "@/components/device/JoinClient";

export const metadata = {
  title: "Join a live session",
};

/** Entry point for someone typing a pairing code rather than scanning one. */
export default function JoinPage() {
  return <JoinClient />;
}
