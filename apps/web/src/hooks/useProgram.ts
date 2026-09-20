"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { sourceApi } from "@/lib/api/devices";
import { AudioMixer } from "@/lib/media/audio-mixer";
import { ProgramCompositor } from "@/lib/media/compositor";
import { type LayoutId, capacityOf } from "@/lib/media/layout";
import { type SubscriptionState, WhepSubscriber } from "@/lib/media/whep";
import type { SceneLayout, SessionBranding, SessionScene, SessionSource } from "@/lib/types";

/** How a source's picture is reaching the studio. */
export type FeedState = SubscriptionState | "local";

export interface ProgramFeed {
  source: SessionSource;
  /** The studio's own camera, or a remote device pulled in over WHEP. */
  stream: MediaStream | null;
  state: FeedState;
  /** True when this source is one of the ones currently being drawn into the program. */
  onAir: boolean;
}

/** A caption the operator can put up and take down during the show. */
export interface LowerThirdText {
  title: string;
  subtitle: string;
}

/** Per-source audio, for the mixer. */
export interface SourceAudio {
  muted: boolean;
  /** 0 to 1. */
  gain: number;
}

export interface UseProgramOptions {
  sessionId: string;
  sources: SessionSource[];
  /** The studio's own capture. Stands in for the Host source, which shares the session's path. */
  localStream: MediaStream | null;
  /** Watched rather than read off the stream: the stream's identity never changes, its tracks do. */
  localAudioTrack: MediaStreamTrack | null;
  /** The look this session broadcasts with. Null until it has loaded. */
  branding: SessionBranding | null;
  /** Composition only runs while the studio holds media; before that there is nothing to draw. */
  enabled: boolean;
  /**
   * Called when the outgoing program track changes identity — which happens once, on entering
   * composition. Cuts and layout changes inside composition do not fire it, which is the whole
   * point of composing.
   */
  onProgramTrackChanged?: (track: MediaStreamTrack) => Promise<void> | void;
  /**
   * Asked to put a source on air. Recalling a scene routes through this rather than promoting the
   * source itself, so a scene is subject to the same rules as any other cut.
   */
  onCutRequested?: (sourceId: string) => void;
}

export interface UseProgramResult {
  layout: LayoutId;
  setLayout: (layout: LayoutId) => void;
  feeds: ProgramFeed[];
  /** True while the program is being composed rather than sent straight from the camera. */
  composing: boolean;
  /** The composed picture, for the program monitor. Null while sending the camera directly. */
  programStream: MediaStream | null;
  registerElement: (sourceId: string, element: HTMLVideoElement | null) => void;
  /** Which source fills the second slot of a two-source layout. Null means "whichever is next". */
  secondarySourceId: string | null;
  setSecondarySourceId: (sourceId: string | null) => void;
  lowerThird: LowerThirdText;
  setLowerThird: (text: LowerThirdText) => void;
  /** Whether the caption is currently on the picture. A live control, not configuration. */
  lowerThirdVisible: boolean;
  setLowerThirdVisible: (visible: boolean) => void;
  audio: Record<string, SourceAudio>;
  setSourceAudio: (sourceId: string, audio: Partial<SourceAudio>) => void;
  /** Arranges the studio to match a saved shot, and asks for the cut it implies. */
  applyScene: (scene: SessionScene) => void;
}

const DEFAULT_AUDIO: SourceAudio = { muted: false, gain: 1 };

const SCENE_LAYOUTS: Record<SceneLayout, LayoutId> = {
  Solo: "solo",
  SideBySide: "side-by-side",
  PictureInPicture: "picture-in-picture",
};

/** Sources that carry pictures. Anything else is in the session for other reasons. */
function contributesMedia(source: SessionSource): boolean {
  return source.contributesMedia && source.status !== "Revoked";
}

/**
 * Drives what the audience sees.
 *
 * The studio composes the program in the browser and publishes the result, so switching sources
 * changes what is drawn rather than what is sent. That is what makes a cut instant: the encoder
 * never restarts and the outgoing track never changes
 * (docs/decisions/0012-program-switching-and-composition.md).
 *
 * Composition is not always running. A solo studio broadcast publishes its camera straight through,
 * exactly as it did before this existed — one fewer copy per frame, and immune to the frame-rate
 * throttling browsers apply to a backgrounded tab. The canvas comes up when the show actually needs
 * it: a second source on air, or a layout that shows more than one.
 */
export function useProgram({
  sessionId,
  sources,
  localStream,
  localAudioTrack,
  branding,
  enabled,
  onProgramTrackChanged,
  onCutRequested,
}: UseProgramOptions): UseProgramResult {
  const [layout, setLayout] = useState<LayoutId>("solo");
  const [remoteStreams, setRemoteStreams] = useState<Record<string, MediaStream>>({});
  const [feedStates, setFeedStates] = useState<Record<string, SubscriptionState>>({});
  const [programStream, setProgramStream] = useState<MediaStream | null>(null);
  const [secondarySourceId, setSecondarySourceId] = useState<string | null>(null);
  const [lowerThird, setLowerThird] = useState<LowerThirdText>({ title: "", subtitle: "" });
  const [lowerThirdVisible, setLowerThirdVisible] = useState(false);
  const [audio, setAudio] = useState<Record<string, SourceAudio>>({});
  const [logo, setLogo] = useState<{ dataUri: string; image: HTMLImageElement } | null>(null);

  const cutRef = useRef(onCutRequested);
  useEffect(() => {
    cutRef.current = onCutRequested;
  }, [onCutRequested]);

  const subscribersRef = useRef(new Map<string, WhepSubscriber>());
  const elementsRef = useRef(new Map<string, HTMLVideoElement>());
  const compositorRef = useRef<ProgramCompositor | null>(null);
  const mixerRef = useRef<AudioMixer | null>(null);
  const publishedTrackRef = useRef<MediaStreamTrack | null>(null);

  const handlerRef = useRef(onProgramTrackChanged);
  useEffect(() => {
    handlerRef.current = onProgramTrackChanged;
  }, [onProgramTrackChanged]);

  const mediaSources = useMemo(() => sources.filter(contributesMedia), [sources]);

  // The Host source shares the session's media path — it *is* the program — so pulling a preview of
  // it would feed the output back into its own input. The studio's local capture stands in for it.
  const remoteSources = useMemo(
    () => mediaSources.filter((source) => source.role !== "Host" && source.status === "Connected"),
    [mediaSources],
  );

  const remoteKey = remoteSources.map((source) => source.id).sort().join(",");

  /**
   * Opens and closes previews to match the connected devices.
   *
   * Keyed on the set of ids rather than the array, so presence updates — a bitrate ticking over —
   * do not tear down and rebuild every subscription several times a minute.
   */
  useEffect(() => {
    if (!enabled) return;

    const wanted = new Set(remoteKey ? remoteKey.split(",") : []);
    const subscribers = subscribersRef.current;

    for (const [sourceId, subscriber] of subscribers) {
      if (wanted.has(sourceId)) continue;

      subscribers.delete(sourceId);
      void subscriber.stop();
      setRemoteStreams((current) => {
        if (!(sourceId in current)) return current;

        const next = { ...current };
        delete next[sourceId];
        return next;
      });
    }

    for (const sourceId of wanted) {
      if (subscribers.has(sourceId)) continue;

      const subscriber = new WhepSubscriber({
        // A fresh read credential per attempt, exactly like publishing: previews are authorized
        // per subscription and expire on their own, so a closed control room leaves nothing behind.
        getCredential: async () => {
          const preview = await sourceApi.preview(sessionId, sourceId);
          return { webRtcUrl: preview.webRtcUrl, readToken: preview.readToken };
        },
        onStream: (stream) => setRemoteStreams((current) => ({ ...current, [sourceId]: stream })),
        onStateChange: (state) => setFeedStates((current) => ({ ...current, [sourceId]: state })),
      });

      subscribers.set(sourceId, subscriber);
      void subscriber.start().catch(() => {
        // The subscriber reports its own failure through onStateChange and retries; a preview that
        // never forms must not take the control room down with it.
      });
    }
  }, [enabled, remoteKey, sessionId]);

  // Close every preview when the studio goes away.
  useEffect(() => {
    const subscribers = subscribersRef.current;

    return () => {
      for (const subscriber of subscribers.values()) void subscriber.stop();
      subscribers.clear();
    };
  }, []);

  const registerElement = useCallback((sourceId: string, element: HTMLVideoElement | null) => {
    if (element) elementsRef.current.set(sourceId, element);
    else elementsRef.current.delete(sourceId);
  }, []);

  /**
   * Decodes the watermark once, rather than per frame.
   *
   * The source is always a data URI, which is what keeps the canvas untainted — a cross-origin
   * image would make `captureStream` throw and take the broadcast down for the sake of a logo.
   */
  useEffect(() => {
    const dataUri = branding?.logoDataUri;
    if (!dataUri || typeof Image === "undefined") return;

    let cancelled = false;
    const image = new Image();

    image.onload = () => {
      if (!cancelled) setLogo({ dataUri, image });
    };
    image.onerror = () => {
      // A logo that will not decode is a cosmetic failure. The show carries on without it.
      if (!cancelled) setLogo(null);
    };

    image.src = dataUri;

    return () => {
      cancelled = true;
    };
  }, [branding?.logoDataUri]);

  /**
   * The decoded logo, but only while it is still the one the session is asking for.
   *
   * Keyed on the data URI rather than cleared when branding changes: a stale image drawn for the
   * frames between "the operator replaced the logo" and "the new one finished decoding" would put
   * the previous show's brand on air.
   */
  const logoImage =
    branding?.showLogo && logo && logo.dataUri === branding.logoDataUri ? logo.image : null;

  const setSourceAudio = useCallback((sourceId: string, update: Partial<SourceAudio>) => {
    setAudio((current) => ({
      ...current,
      [sourceId]: { ...DEFAULT_AUDIO, ...current[sourceId], ...update },
    }));
  }, []);

  /**
   * Arranges the studio to match a saved shot.
   *
   * The cut is requested rather than performed: promoting a source has rules of its own — a camera
   * that has stopped sending cannot go on air — and a scene saved an hour ago knows none of them.
   */
  const applyScene = useCallback((scene: SessionScene) => {
    setLayout(SCENE_LAYOUTS[scene.layout] ?? "solo");
    setSecondarySourceId(scene.secondarySourceId);
    setLowerThird({ title: scene.lowerThirdTitle ?? "", subtitle: scene.lowerThirdSubtitle ?? "" });

    // The caption comes up with the scene only when the scene carries one; recalling a shot with no
    // caption should not leave the previous one on screen.
    setLowerThirdVisible(Boolean(scene.lowerThirdTitle));

    if (scene.primarySourceId) cutRef.current?.(scene.primarySourceId);
  }, []);

  /**
   * The sources on air, program first.
   *
   * Order is the layout's contract: slot zero is the full frame in every layout, so putting the
   * program there means an operator cuts without also having to think about position.
   */
  const onAirSources = useMemo(() => {
    const program = mediaSources.find((source) => source.isProgram);
    const capacity = capacityOf(layout);

    const rest = mediaSources.filter((source) => source !== program);

    // An explicitly chosen second source wins; otherwise the next available one fills the slot, so
    // picking a two-source layout shows something immediately rather than half a frame of black.
    const chosen = rest.find((source) => source.id === secondarySourceId);
    const ordered = [program, chosen, ...rest].filter(
      (source, index, all): source is SessionSource =>
        source !== undefined && all.indexOf(source) === index,
    );

    return ordered.slice(0, capacity);
  }, [mediaSources, layout, secondarySourceId]);

  const onAirKey = onAirSources.map((source) => source.id).join(",");
  const programSource = onAirSources[0];
  const programIsStudio = programSource?.role === "Host";

  /**
   * Whether the canvas is needed at all.
   *
   * The rule is "compose only when there is something to draw that the raw camera cannot provide":
   * a second source actually on air, a caption, or a watermark. A two-source *layout* with only one
   * source is not enough — it would pay the whole cost of composition to draw one camera full
   * frame, which is exactly what sending the camera does for free.
   *
   * Derived rather than latched, so a show that returns to a plain solo shot returns to sending the
   * camera directly: one fewer copy per frame, and immune to the frame-rate throttling a browser
   * applies to a backgrounded tab. The cost is a track swap at each boundary, which `replaceTrack`
   * makes seamless.
   */
  const captionShowing = lowerThirdVisible && lowerThird.title.trim().length > 0;
  const composing =
    (layout !== "solo" && onAirSources.length > 1) ||
    captionShowing ||
    logoImage !== null ||
    (programSource !== undefined && !programIsStudio);

  /**
   * Keeps the composition matching the show.
   *
   * Runs on every cut and layout change, but only hands a track to the publisher once — on entering
   * composition. After that the canvas and the mix are the outgoing tracks, and everything the
   * operator does happens inside them.
   */
  useEffect(() => {
    if (!enabled || !localStream) return;

    // Back to a plain camera shot: stop drawing, and put the camera itself on air again.
    if (!composing) {
      compositorRef.current?.stop();

      const localVideo = localStream.getVideoTracks()[0] ?? null;
      if (localVideo && publishedTrackRef.current !== localVideo) {
        publishedTrackRef.current = localVideo;
        setProgramStream(null);
        void handlerRef.current?.(localVideo);
      }

      return;
    }

    const compositor = (compositorRef.current ??= new ProgramCompositor());
    const mixer = (mixerRef.current ??= new AudioMixer());

    compositor.setLayout(layout);
    compositor.setLayers(
      onAirSources.flatMap((source) => {
        const element = elementsRef.current.get(source.id);
        return element ? [{ id: source.id, label: source.displayName, element }] : [];
      }),
    );
    compositor.setOverlay({
      lowerThird:
        lowerThirdVisible && lowerThird.title
          ? {
              title: lowerThird.title,
              subtitle: lowerThird.subtitle || null,
              accentColor: branding?.accentColor ?? "#0EA5E9",
            }
          : null,
      watermark:
        logoImage && branding
          ? {
              image: logoImage,
              corner: branding.logoPosition,
              opacity: branding.logoOpacityPercent / 100,
            }
          : null,
    });

    compositor.start();

    // Audio follows the picture, and the studio microphone is always in the mix. The host is
    // narrating: going silent because the operator cut to a guest is not what a cut means.
    const audible = onAirSources.flatMap((source) => {
      const track =
        source.role === "Host" ? localAudioTrack : (remoteStreams[source.id]?.getAudioTracks()[0] ?? null);
      const level = audio[source.id] ?? DEFAULT_AUDIO;
      return track ? [{ id: source.id, track, muted: level.muted, gain: level.gain }] : [];
    });

    if (localAudioTrack && !audible.some((entry) => entry.track === localAudioTrack)) {
      const studio = mediaSources.find((source) => source.role === "Host");
      const level = studio ? (audio[studio.id] ?? DEFAULT_AUDIO) : DEFAULT_AUDIO;

      audible.push({
        id: "studio-microphone",
        track: localAudioTrack,
        muted: level.muted,
        gain: level.gain,
      });
    }

    mixer.setSources(audible);
    void mixer.resume();

    const composedVideo = compositor.videoTrack;
    const mixedAudio = mixer.track;

    if (composedVideo && publishedTrackRef.current !== composedVideo) {
      publishedTrackRef.current = composedVideo;

      const composed = new MediaStream();
      composed.addTrack(composedVideo);
      if (mixedAudio) composed.addTrack(mixedAudio);
      setProgramStream(composed);

      void handlerRef.current?.(composedVideo);
      if (mixedAudio) void handlerRef.current?.(mixedAudio);
    }
  }, [
    enabled,
    composing,
    layout,
    onAirKey,
    localStream,
    localAudioTrack,
    remoteStreams,
    onAirSources,
    mediaSources,
    audio,
    branding,
    logoImage,
    lowerThird,
    lowerThirdVisible,
  ]);

  // Tear the composition down with the studio. The compositor holds a canvas capture and the mixer
  // an audio context; both survive a React unmount unless closed.
  useEffect(() => {
    return () => {
      compositorRef.current?.dispose();
      compositorRef.current = null;
      void mixerRef.current?.dispose();
      mixerRef.current = null;
    };
  }, []);

  const onAirIds = useMemo(() => new Set(onAirSources.map((source) => source.id)), [onAirSources]);

  const feeds = useMemo<ProgramFeed[]>(
    () =>
      mediaSources.map((source) => ({
        source,
        stream: source.role === "Host" ? localStream : (remoteStreams[source.id] ?? null),
        state: source.role === "Host" ? "local" : (feedStates[source.id] ?? "idle"),
        onAir: onAirIds.has(source.id),
      })),
    [mediaSources, localStream, remoteStreams, feedStates, onAirIds],
  );

  return {
    layout,
    setLayout,
    feeds,
    composing,
    programStream,
    registerElement,
    secondarySourceId,
    setSecondarySourceId,
    lowerThird,
    setLowerThird,
    lowerThirdVisible,
    setLowerThirdVisible,
    audio,
    setSourceAudio,
    applyScene,
  };
}
