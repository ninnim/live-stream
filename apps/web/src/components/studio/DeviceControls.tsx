"use client";

import { Badge, Button, Select } from "@/components/ui/primitives";
import { InputLevelMeter } from "@/components/studio/InputLevelMeter";
import type { VideoSource } from "@/hooks/useMediaDevices";
import { type InputLevels, MICROPHONE_INPUT, SCREEN_INPUT } from "@/hooks/useInputLevels";
import type { CaptureAudioLevels } from "@/lib/media/capture-audio";
import {
  type CaptureQuality,
  type DeviceConnection,
  type MediaDeviceOption,
  type ScreenCaptureMode,
  type ScreenFrameRate,
  type MicrophoneMode,
  MAX_DELIVERED_FRAME_RATE,
  SCREEN_FRAME_RATES,
  type TrackFormat,
  connectionLabel,
} from "@/lib/media/devices";

/**
 * Order the device picker groups them in. Plugged-in hardware comes first: someone who has just
 * connected a camera or capture card is looking for it, and burying it under the laptop webcam is
 * the difference between the feature existing and being found.
 */
const CONNECTION_ORDER: DeviceConnection[] = ["wired", "wireless", "built-in", "virtual", "unknown"];

const SCREEN_MODE_OPTIONS: { value: ScreenCaptureMode; label: string }[] = [
  { value: "presentation", label: "Slides and code — 30 fps, stays sharp" },
  { value: "gameplay", label: "Games and video — 60 fps, stays smooth" },
];

const QUALITY_OPTIONS: { value: CaptureQuality; label: string }[] = [
  { value: "1080p", label: "1080p — best quality" },
  { value: "720p", label: "720p — recommended" },
  { value: "540p", label: "540p — slow connections" },
  { value: "auto", label: "Automatic" },
];

interface DeviceControlsProps {
  cameras: MediaDeviceOption[];
  microphones: MediaDeviceOption[];
  selectedCameraId: string | null;
  selectedMicrophoneId: string | null;
  cameraEnabled: boolean;
  microphoneEnabled: boolean;
  quality: CaptureQuality;
  videoFormat: TrackFormat | null;
  videoSource: VideoSource;
  screenMode: ScreenCaptureMode;
  screenFrameRate: ScreenFrameRate;
  /** Whether a track of each kind is actually open. Both are optional. */
  hasVideo: boolean;
  hasAudio: boolean;
  /** Whether the shared screen is sending its sound, which is the only reason to show a balance. */
  hasScreenAudio: boolean;
  /** Balance between the two capture sources, 0 to 1 each. */
  audioLevels: CaptureAudioLevels;
  /** What each open input is picking up right now. Absent until the meter has a reading. */
  inputLevels: InputLevels;
  microphoneMode: MicrophoneMode;
  /** What was detected about this machine's encoding, in one sentence. Null until it is known. */
  capabilityNote: string | null;
  /** A device change is in flight. The controls stay readable but must not be re-entered. */
  switching: boolean;
  onSelectCamera: (deviceId: string) => void;
  onSelectMicrophone: (deviceId: string) => void;
  onSelectQuality: (quality: CaptureQuality) => void;
  onToggleCamera: () => void;
  onToggleMicrophone: () => void;
  onChangeAudioLevels: (levels: CaptureAudioLevels) => void;
  onSelectMicrophoneMode: (mode: MicrophoneMode) => void;
  onShareScreen: () => void;
  onSelectScreenMode: (mode: ScreenCaptureMode) => void;
  onSelectScreenFrameRate: (rate: ScreenFrameRate) => void;
  onStopSharing: () => void;
}

/** Groups devices for the picker, preserving `CONNECTION_ORDER` and dropping empty groups. */
function groupByConnection(
  devices: MediaDeviceOption[],
): { connection: DeviceConnection; devices: MediaDeviceOption[] }[] {
  return CONNECTION_ORDER.map((connection) => ({
    connection,
    devices: devices.filter((device) => device.connection === connection),
  })).filter((group) => group.devices.length > 0);
}

/**
 * Renders the options for one device kind.
 *
 * Groups are only labelled when there is more than one, because before permission is granted every
 * device is unidentifiable and a lone heading reading "Other" is pure noise.
 */
function DeviceOptions({ devices, emptyLabel }: { devices: MediaDeviceOption[]; emptyLabel: string }) {
  if (devices.length === 0) {
    return <option value="">{emptyLabel}</option>;
  }

  const groups = groupByConnection(devices);

  if (groups.length <= 1) {
    return (
      <>
        {devices.map((device) => (
          <option key={device.deviceId} value={device.deviceId}>
            {device.label}
          </option>
        ))}
      </>
    );
  }

  return (
    <>
      {groups.map((group) => (
        <optgroup key={group.connection} label={connectionLabel(group.connection)}>
          {group.devices.map((device) => (
            <option key={device.deviceId} value={device.deviceId}>
              {device.label}
            </option>
          ))}
        </optgroup>
      ))}
    </>
  );
}

/**
 * One fader.
 *
 * Percentages rather than decibels: this is aimed at somebody who wants their game quieter than
 * their voice, not at somebody reading a meter.
 */
function LevelSlider({
  label,
  value,
  disabled,
  onChange,
}: {
  label: string;
  value: number;
  disabled: boolean;
  onChange: (value: number) => void;
}) {
  const percent = Math.round(value * 100);

  return (
    <label className="flex items-center gap-3 text-sm text-slate-300">
      <span className="w-32 shrink-0">{label}</span>
      <input
        type="range"
        min={0}
        max={100}
        step={5}
        value={percent}
        disabled={disabled}
        aria-label={label}
        onChange={(event) => onChange(Number(event.target.value) / 100)}
        className="h-1 flex-1 cursor-pointer appearance-none rounded bg-slate-700 accent-sky-400 disabled:cursor-not-allowed disabled:opacity-50"
      />
      <span className="w-10 shrink-0 text-right text-xs tabular-nums text-slate-400">{percent}%</span>
    </label>
  );
}

export function DeviceControls({
  cameras,
  microphones,
  selectedCameraId,
  selectedMicrophoneId,
  cameraEnabled,
  microphoneEnabled,
  quality,
  videoFormat,
  videoSource,
  screenMode,
  screenFrameRate,
  hasVideo,
  hasAudio,
  hasScreenAudio,
  audioLevels,
  inputLevels,
  microphoneMode,
  capabilityNote,
  switching,
  onSelectCamera,
  onSelectMicrophone,
  onSelectQuality,
  onToggleCamera,
  onToggleMicrophone,
  onChangeAudioLevels,
  onSelectMicrophoneMode,
  onShareScreen,
  onSelectScreenMode,
  onSelectScreenFrameRate,
  onStopSharing,
}: DeviceControlsProps) {
  const sharing = videoSource === "screen";

  return (
    <div className="flex flex-col gap-4" aria-busy={switching}>
      <div className="grid gap-4 sm:grid-cols-2">
        <Select
          label="Camera"
          value={selectedCameraId ?? ""}
          disabled={switching || cameras.length === 0}
          onChange={(event) => onSelectCamera(event.target.value)}
        >
          <DeviceOptions devices={cameras} emptyLabel="No camera found" />
        </Select>

        <Select
          label="Microphone"
          value={selectedMicrophoneId ?? ""}
          disabled={switching || microphones.length === 0}
          onChange={(event) => onSelectMicrophone(event.target.value)}
        >
          <DeviceOptions devices={microphones} emptyLabel="No microphone found" />
        </Select>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        {/*
          A shared screen has no resolution rungs — it arrives at whatever the display is — so
          while sharing, this slot carries the setting that does apply: what the screen is
          showing, which decides the frame rate and what gets shed under load.
        */}
        {sharing ? (
          <Select
            label="Screen content"
            value={screenMode}
            disabled={switching}
            onChange={(event) => onSelectScreenMode(event.target.value as ScreenCaptureMode)}
          >
            {SCREEN_MODE_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        ) : (
          <Select
            label="Quality"
            value={quality}
            disabled={switching}
            onChange={(event) => onSelectQuality(event.target.value as CaptureQuality)}
          >
            {QUALITY_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        )}

        {sharing ? (
          <Select
            label="Frame rate"
            value={String(screenFrameRate)}
            disabled={switching}
            onChange={(event) =>
              onSelectScreenFrameRate(Number(event.target.value) as ScreenFrameRate)
            }
          >
            {SCREEN_FRAME_RATES.map((rate) => (
              <option key={rate} value={rate}>
                {rate} fps
                {rate > MAX_DELIVERED_FRAME_RATE ? " — capture only" : ""}
              </option>
            ))}
          </Select>
        ) : (
          <div className="flex flex-col justify-end gap-1.5">
            <span className="text-sm font-medium text-slate-400">Sending</span>
            <p className="rounded-lg border border-slate-800 bg-slate-900/60 px-3 py-2 text-sm text-slate-300">
              {videoFormat
                ? `Camera · ${videoFormat.width}×${videoFormat.height}${
                    videoFormat.frameRate > 0 ? ` · ${videoFormat.frameRate} fps` : ""
                  }`
                : "Not capturing"}
            </p>
          </div>
        )}
      </div>

      {/*
        What the machine can do, and what the studio did about it. Shown rather than applied
        silently: the settings above have moved, and somebody who does not know why would
        reasonably conclude the studio had a mind of its own.
      */}
      {capabilityNote ? (
        <p className="rounded-lg border border-slate-800 bg-slate-900/40 px-3 py-2 text-xs text-slate-400">
          {capabilityNote}
        </p>
      ) : null}

      {sharing ? (
        <div className="flex flex-col gap-1.5">
          <span className="text-sm font-medium text-slate-400">Sending</span>
          <p className="rounded-lg border border-slate-800 bg-slate-900/60 px-3 py-2 text-sm text-slate-300">
            {videoFormat
              ? `Screen · ${videoFormat.width}×${videoFormat.height}${
                  videoFormat.frameRate > 0 ? ` · ${videoFormat.frameRate} fps` : ""
                }`
              : "Not capturing"}
          </p>
          {screenFrameRate > MAX_DELIVERED_FRAME_RATE ? (
            <span className="text-xs text-amber-300">
              Above {MAX_DELIVERED_FRAME_RATE} fps the extra frames are captured and then dropped:
              browsers encode WebRTC video at up to {MAX_DELIVERED_FRAME_RATE}, and so does every
              platform this publishes to.
            </span>
          ) : null}
        </div>
      ) : null}

      <p className="text-xs text-slate-500">
        {switching
          ? "Changing device…"
          : "Cameras and microphones can be changed at any time, including while you are live."}
      </p>

      <div className="flex flex-wrap items-center gap-3">
        {/*
          Disabled with no track rather than hidden: the control belongs in the row whether or
          not this machine has the device, and a button that silently does nothing is worse
          than one that shows it cannot.
        */}
        <Button
          variant="secondary"
          onClick={onToggleCamera}
          disabled={!hasVideo}
          aria-pressed={hasVideo && cameraEnabled}
        >
          <Badge tone={!hasVideo ? "neutral" : cameraEnabled ? "positive" : "critical"}>
            {!hasVideo ? "None" : cameraEnabled ? "On" : "Off"}
          </Badge>
          Camera
        </Button>

        <Button
          variant="secondary"
          onClick={onToggleMicrophone}
          disabled={!hasAudio}
          aria-pressed={hasAudio && microphoneEnabled}
        >
          <Badge tone={!hasAudio ? "neutral" : microphoneEnabled ? "positive" : "critical"}>
            {!hasAudio ? "None" : microphoneEnabled ? "On" : "Off"}
          </Badge>
          Microphone
        </Button>

        {/*
          Sharing swaps the outgoing video track in place, so it lands mid-broadcast without a
          dropout. The browser's own "Stop sharing" bar ends the track, which the studio treats as
          "go back to camera" — so both ways out lead to the same place.
        */}
        <Button
          variant="secondary"
          onClick={sharing ? onStopSharing : onShareScreen}
          disabled={switching}
          aria-pressed={sharing}
        >
          {sharing ? "Stop sharing" : "Share screen"}
        </Button>
      </div>

      {/*
        What is actually arriving, which nothing else in this studio reports. Every other audio
        control says what was *asked for*; a device can be selected, unmuted, and stone silent.
      */}
      <div className="flex flex-col gap-3 rounded-lg border border-slate-800 bg-slate-900/40 p-3">
        <InputLevelMeter
          label="Your microphone"
          level={inputLevels[MICROPHONE_INPUT]}
          present={hasAudio && microphoneEnabled}
        />

        {hasScreenAudio ? (
          <InputLevelMeter
            label="Screen sound"
            level={inputLevels[SCREEN_INPUT]}
            present
          />
        ) : null}
      </div>

      {/*
        Only while the screen is sending sound. With one source there is nothing to balance
        against, and a lone fader at full is a control that can only make things worse.
      */}
      {hasScreenAudio ? (
        <fieldset className="flex flex-col gap-3 rounded-lg border border-slate-800 bg-slate-900/40 p-3">
          <legend className="px-1 text-xs font-medium text-slate-400">Audio balance</legend>

          <LevelSlider
            label="Your microphone"
            value={audioLevels.microphone}
            disabled={!hasAudio}
            onChange={(microphone) => onChangeAudioLevels({ ...audioLevels, microphone })}
          />

          <LevelSlider
            label="Screen sound"
            value={audioLevels.screen}
            disabled={false}
            onChange={(screen) => onChangeAudioLevels({ ...audioLevels, screen })}
          />

          <p className="text-xs text-slate-500">
            Both go out together. Screen sound starts lower so your game does not bury you.
          </p>
        </fieldset>
      ) : null}

      {/*
        A choice between two intents, not an effect to switch on. The two settings are genuinely
        opposed — what rescues a voice in a live room destroys a piece of music — so naming what is
        being broadcast is a question somebody can answer, where "voice enhancement: on/off" is one
        they can only guess at.
      */}
      <Select
        label="Sound type"
        value={microphoneMode}
        disabled={switching || !hasAudio}
        onChange={(event) => onSelectMicrophoneMode(event.target.value as MicrophoneMode)}
      >
        <option value="voice">Speech — removes room echo and background noise</option>
        <option value="music">Music — full range, nothing removed</option>
      </Select>
    </div>
  );
}
