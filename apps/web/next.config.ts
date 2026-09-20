import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  reactStrictMode: true,
  // The studio holds camera/microphone permissions and short-lived tokens; do not advertise the
  // framework version to clients.
  poweredByHeader: false,
  // Emits a self-contained server bundle, which is what infrastructure/docker/web.Dockerfile ships.
  output: "standalone",
};

export default nextConfig;
