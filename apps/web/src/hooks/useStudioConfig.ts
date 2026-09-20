"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/lib/api/client";
import { studioApi } from "@/lib/api/studio";
import type { SaveScene, SessionBranding, SessionScene, UpdateBranding } from "@/lib/types";

export interface UseStudioConfigResult {
  branding: SessionBranding | null;
  scenes: SessionScene[];
  loading: boolean;
  error: string | null;
  busy: boolean;
  refresh: () => Promise<void>;
  saveBranding: (update: UpdateBranding) => Promise<void>;
  addScene: (scene: SaveScene) => Promise<SessionScene | null>;
  updateScene: (sceneId: string, scene: SaveScene) => Promise<void>;
  deleteScene: (sceneId: string) => Promise<void>;
  clearError: () => void;
}

/**
 * The studio's configuration: how it looks, and the shots it has prepared.
 *
 * Deliberately not polled. Branding and scenes are edited by the operator sitting in front of them,
 * not driven by the server the way session state and presence are — polling would fight the form
 * someone is typing into.
 */
export function useStudioConfig(sessionId: string): UseStudioConfigResult {
  const [branding, setBranding] = useState<SessionBranding | null>(null);
  const [scenes, setScenes] = useState<SessionScene[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const [loadedBranding, loadedScenes] = await Promise.all([
        studioApi.branding(sessionId),
        studioApi.scenes(sessionId),
      ]);

      setBranding(loadedBranding);
      setScenes(loadedScenes);
      setError(null);
    } catch (loadError) {
      setError(loadError instanceof ApiError ? loadError.message : "Could not load the studio setup.");
    } finally {
      setLoading(false);
    }
  }, [sessionId]);

  useEffect(() => {
    // On a timer of zero rather than directly in the effect body, so the state updates land in
    // their own commit instead of cascading inside this one.
    const initial = setTimeout(() => void refresh(), 0);
    return () => clearTimeout(initial);
  }, [refresh]);

  /** Runs a write with the busy flag and error reporting every path needs. */
  const run = useCallback(async <T,>(action: () => Promise<T>): Promise<T | null> => {
    setBusy(true);
    setError(null);

    try {
      return await action();
    } catch (actionError) {
      setError(
        actionError instanceof ApiError ? actionError.message : "That did not save. Please try again.",
      );
      return null;
    } finally {
      setBusy(false);
    }
  }, []);

  const saveBranding = useCallback(
    async (update: UpdateBranding) => {
      const saved = await run(() => studioApi.updateBranding(sessionId, update));
      if (saved) setBranding(saved);
    },
    [run, sessionId],
  );

  const addScene = useCallback(
    async (scene: SaveScene) => {
      const created = await run(() => studioApi.addScene(sessionId, scene));
      if (created) setScenes((current) => [...current, created]);
      return created;
    },
    [run, sessionId],
  );

  const updateScene = useCallback(
    async (sceneId: string, scene: SaveScene) => {
      const saved = await run(() => studioApi.updateScene(sessionId, sceneId, scene));
      if (saved) {
        setScenes((current) => current.map((existing) => (existing.id === sceneId ? saved : existing)));
      }
    },
    [run, sessionId],
  );

  const deleteScene = useCallback(
    async (sceneId: string) => {
      // Removed from the list only once the server has confirmed it: a scene that reappears on the
      // next load is worse than one that takes a moment to go.
      const removed = await run(async () => {
        await studioApi.deleteScene(sessionId, sceneId);
        return true;
      });

      if (removed) setScenes((current) => current.filter((scene) => scene.id !== sceneId));
    },
    [run, sessionId],
  );

  const clearError = useCallback(() => setError(null), []);

  return {
    branding,
    scenes,
    loading,
    error,
    busy,
    refresh,
    saveBranding,
    addScene,
    updateScene,
    deleteScene,
    clearError,
  };
}
