"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import {
  LocalRecorder,
  type LocalRecorderOptions,
  RecordingCancelled,
  type RecordingSink,
  type RecordingFormat,
  type RecordingSources,
  openRecordingSink,
  pickRecordingFormat,
  recordingFileName,
} from "@/lib/media/local-recording";

export interface UseLocalRecordingOptions {
  /** Names the file. The session's title, so a recording can be found again. */
  title: string;
  /** What to record. Read at the moment Record is pressed, and kept current for audio. */
  sources: RecordingSources;
  /** Injectable for tests; the real ones talk to the browser's file picker and MediaRecorder. */
  openSink?: (fileName: string, format: RecordingFormat) => Promise<RecordingSink>;
  createRecorder?: LocalRecorderOptions["createRecorder"];
  mixer?: LocalRecorderOptions["mixer"];
  isTypeSupported?: (mimeType: string) => boolean;
}

export interface UseLocalRecordingResult {
  recording: boolean;
  /** True between pressing Record and the recorder actually running — the file picker is open. */
  starting: boolean;
  /** Seconds elapsed. Counted from a clock rather than from bytes, so it moves on a still screen. */
  elapsedSeconds: number;
  bytesWritten: number;
  /** True when the recording is being held in memory, which cannot go on indefinitely. */
  buffersInMemory: boolean;
  /** Whether this browser can record at all. */
  supported: boolean;
  error: string | null;
  notice: string | null;
  start: () => Promise<void>;
  stop: () => Promise<void>;
  dismiss: () => void;
}

/**
 * Recording the studio to a file, without going live.
 *
 * Deliberately independent of the broadcast. Starting a recording does not touch the publisher, and
 * a recording that is running has no opinion about whether a broadcast is too — recording a show
 * while it goes out is a reasonable thing to want, and refusing it would be arbitrary.
 */
export function useLocalRecording(options: UseLocalRecordingOptions): UseLocalRecordingResult {
  const [recording, setRecording] = useState(false);
  const [starting, setStarting] = useState(false);
  const [elapsedSeconds, setElapsedSeconds] = useState(0);
  const [bytesWritten, setBytesWritten] = useState(0);
  const [buffersInMemory, setBuffersInMemory] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const recorderRef = useRef<LocalRecorder | null>(null);
  const optionsRef = useRef(options);

  useEffect(() => {
    optionsRef.current = options;
  }, [options]);

  // Only a video source decides the container; the audio-only formats are a different list.
  const hasVideo = options.sources.video !== null;
  const supported =
    pickRecordingFormat(hasVideo, options.isTypeSupported) !== null &&
    (hasVideo || options.sources.audio !== null);

  // Keeps the mix current without restarting anything: the recorder's own audio track does not
  // change, only what feeds it.
  const audioTrack = options.sources.audio;
  useEffect(() => {
    recorderRef.current?.setAudio(audioTrack);
  }, [audioTrack]);

  useEffect(() => {
    if (!recording) return;

    const startedAt = Date.now();
    const timer = setInterval(
      () => setElapsedSeconds(Math.floor((Date.now() - startedAt) / 1000)),
      1000,
    );

    return () => clearInterval(timer);
  }, [recording]);

  /**
   * A recording is not on disk until it is closed, so leaving the page mid-recording loses it.
   * The browser will only show its own generic warning, but a generic warning is the difference
   * between losing an hour of work and not.
   */
  useEffect(() => {
    if (!recording) return;

    const warn = (event: BeforeUnloadEvent): void => {
      event.preventDefault();
      event.returnValue = "";
    };

    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [recording]);

  const start = useCallback(async () => {
    const current = optionsRef.current;
    const sources = current.sources;

    if (recorderRef.current || !sources.video && !sources.audio) return;

    const format = pickRecordingFormat(sources.video !== null, current.isTypeSupported);
    if (!format) {
      setError("This browser cannot record video. Try Chrome, Edge, or Firefox.");
      return;
    }

    setStarting(true);
    setError(null);
    setNotice(null);

    let sink: RecordingSink;

    try {
      // Before anything else, and inside the click: a file picker that is not opened from a user
      // gesture is refused by the browser, and the operator would get no dialog and no recording.
      const fileName = recordingFileName(current.title, format.extension);
      sink = await (current.openSink ?? openRecordingSink)(fileName, format);
    } catch (sinkError) {
      setStarting(false);

      // Cancelling the save dialog is a decision, not a fault, and must not leave an error banner.
      if (!(sinkError instanceof RecordingCancelled)) {
        setError("Could not open a file to record into.");
      }

      return;
    }

    const recorder = new LocalRecorder(sink, format, {
      mixer: current.mixer,
      createRecorder: current.createRecorder,
      onProgress: setBytesWritten,
      onStopped: (_reason, message) => {
        recorderRef.current = null;
        setRecording(false);
        setStarting(false);
        if (message) setNotice(message);
      },
    });

    try {
      recorder.start(sources);
    } catch {
      setStarting(false);
      await sink.abort();
      setError("Could not start recording.");
      return;
    }

    recorderRef.current = recorder;
    setBuffersInMemory(sink.buffersInMemory);
    setBytesWritten(0);
    setElapsedSeconds(0);
    setRecording(true);
    setStarting(false);
  }, []);

  const stop = useCallback(async () => {
    await recorderRef.current?.stop("requested");
  }, []);

  // A studio torn down mid-recording must still close the file. Everything written is kept.
  useEffect(
    () => () => {
      void recorderRef.current?.stop("requested");
    },
    [],
  );

  const dismiss = useCallback(() => {
    setError(null);
    setNotice(null);
  }, []);

  return {
    recording,
    starting,
    elapsedSeconds,
    bytesWritten,
    buffersInMemory,
    supported,
    error,
    notice,
    start,
    stop,
    dismiss,
  };
}
