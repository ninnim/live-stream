import { afterEach, describe, expect, it, vi } from "vitest";
import {
  MediaAccessError,
  audioConstraints,
  DEFAULT_MICROPHONE_MODE,
  classifyConnection,
  describeTrackFormat,
  detectMediaSupport,
  enumerateDevices,
  requestCameraTrack,
  requestMicrophoneTrack,
  requestScreenCapture,
  MAX_DELIVERED_FRAME_RATE,
  SCREEN_FRAME_RATES,
  stopStream,
  toMediaAccessError,
  videoConstraints,
} from "@/lib/media/devices";

/** Minimal fakes for the browser media APIs jsdom does not implement. */
function fakeTrack(kind: "video" | "audio", deviceId = "device-1", settings: MediaTrackSettings = {}) {
  return {
    kind,
    enabled: true,
    stop: vi.fn(),
    getSettings: () => ({ deviceId, ...settings }),
  } as unknown as MediaStreamTrack;
}

function fakeStream(tracks: MediaStreamTrack[]) {
  return {
    getTracks: () => tracks,
    getVideoTracks: () => tracks.filter((t) => t.kind === "video"),
    getAudioTracks: () => tracks.filter((t) => t.kind === "audio"),
  } as unknown as MediaStream;
}

function installMediaDevices(overrides: Partial<MediaDevices>) {
  Object.defineProperty(navigator, "mediaDevices", {
    configurable: true,
    value: {
      getUserMedia: vi.fn(),
      enumerateDevices: vi.fn().mockResolvedValue([]),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      ...overrides,
    },
  });

  vi.stubGlobal("RTCPeerConnection", class {});
}

afterEach(() => {
  Reflect.deleteProperty(navigator, "mediaDevices");
});

describe("detectMediaSupport", () => {
  it("reports unsupported when getUserMedia is missing", () => {
    Object.defineProperty(navigator, "mediaDevices", { configurable: true, value: undefined });

    const result = detectMediaSupport();

    expect(result.supported).toBe(false);
    expect(result.error).toBeInstanceOf(MediaAccessError);
  });

  it("reports supported when the capture and WebRTC APIs are present", () => {
    installMediaDevices({});

    expect(detectMediaSupport().supported).toBe(true);
  });
});

describe("toMediaAccessError", () => {
  it("maps a permission denial to actionable guidance", () => {
    const error = toMediaAccessError(
      Object.assign(new Error("Permission denied"), { name: "NotAllowedError" }),
      "camera",
    );

    expect(error.reason).toBe("permission-denied");
    expect(error.userMessage).toContain("camera");
    // The recovery text must tell the user what to actually do.
    expect(error.recovery).toContain("Allow");
  });

  it("maps a busy device to the 'in use elsewhere' case", () => {
    const error = toMediaAccessError(
      Object.assign(new Error("Could not start source"), { name: "NotReadableError" }),
      "camera",
    );

    expect(error.reason).toBe("device-in-use");
  });

  it("maps a missing device to the 'no device' case", () => {
    const error = toMediaAccessError(
      Object.assign(new Error("not found"), { name: "NotFoundError" }),
      "microphone",
    );

    expect(error.reason).toBe("no-device");
  });

  it("maps unsatisfiable constraints to a quality problem", () => {
    const error = toMediaAccessError(
      Object.assign(new Error("too big"), { name: "OverconstrainedError" }),
      "camera",
    );

    expect(error.reason).toBe("constraints-unsatisfiable");
  });

  it("never leaves the user without a message", () => {
    const error = toMediaAccessError(new Error("something odd"), "camera");

    expect(error.reason).toBe("unknown");
    expect(error.userMessage.length).toBeGreaterThan(0);
    expect(error.recovery.length).toBeGreaterThan(0);
  });

  it("keeps the raw browser detail out of the user-facing message", () => {
    const error = toMediaAccessError(
      Object.assign(new Error("Requested device not found"), { name: "NotFoundError" }),
      "camera",
    );

    expect(error.userMessage).not.toContain("Requested device not found");
    expect(error.technicalDetail).toBe("Requested device not found");
  });
});

describe("classifyConnection", () => {
  // The point of the heuristic is that someone who has just plugged something in can find it.
  it("recognises a capture card and a USB camera as cable-connected", () => {
    expect(classifyConnection("Cam Link 4K (0fd9:0066)")).toBe("wired");
    expect(classifyConnection("Logitech BRIO (046d:8886)")).toBe("wired");
    expect(classifyConnection("USB Audio Device")).toBe("wired");
    expect(classifyConnection("Elgato HD60")).toBe("wired");
  });

  it("recognises built-in hardware", () => {
    expect(classifyConnection("FaceTime HD Camera (Built-in)")).toBe("built-in");
    expect(classifyConnection("Integrated Camera")).toBe("built-in");
  });

  it("recognises wireless hardware", () => {
    expect(classifyConnection("AirPods Pro")).toBe("wireless");
    expect(classifyConnection("Jabra Evolve (Bluetooth)")).toBe("wireless");
  });

  it("recognises virtual cameras, including phone bridges", () => {
    expect(classifyConnection("OBS Virtual Camera")).toBe("virtual");
    expect(classifyConnection("Camo")).toBe("virtual");
    expect(classifyConnection("EpocCam Camera")).toBe("virtual");
  });

  it("prefers the more specific claim when a label matches several patterns", () => {
    // "USB" would otherwise win on ordering alone and call a Bluetooth dongle a cable.
    expect(classifyConnection("USB Bluetooth Headset")).toBe("wireless");
    // A virtual camera bridged over USB is still virtual: we do not know which mode is in use.
    expect(classifyConnection("DroidCam USB")).toBe("virtual");
  });

  it("admits when it does not know rather than guessing", () => {
    expect(classifyConnection("C920")).toBe("unknown");
    expect(classifyConnection("")).toBe("unknown");
  });
});

describe("enumerateDevices", () => {
  it("splits cameras and microphones and classifies how each is attached", async () => {
    installMediaDevices({
      enumerateDevices: vi.fn().mockResolvedValue([
        { kind: "videoinput", deviceId: "cam-1", label: "Integrated Camera", groupId: "g1" },
        { kind: "videoinput", deviceId: "cam-2", label: "Cam Link 4K (0fd9:0066)", groupId: "g2" },
        { kind: "audioinput", deviceId: "mic-1", label: "AirPods Pro", groupId: "g3" },
        { kind: "audiooutput", deviceId: "spk-1", label: "Speakers", groupId: "g4" },
      ]),
    } as Partial<MediaDevices>);

    const { cameras, microphones } = await enumerateDevices();

    expect(cameras).toEqual([
      { deviceId: "cam-1", label: "Integrated Camera", kind: "camera", connection: "built-in", groupId: "g1" },
      {
        deviceId: "cam-2",
        label: "Cam Link 4K (0fd9:0066)",
        kind: "camera",
        connection: "wired",
        groupId: "g2",
      },
    ]);
    expect(microphones).toEqual([
      { deviceId: "mic-1", label: "AirPods Pro", kind: "microphone", connection: "wireless", groupId: "g3" },
    ]);
  });

  it("names unlabelled devices so they remain selectable before permission is granted", async () => {
    installMediaDevices({
      enumerateDevices: vi
        .fn()
        .mockResolvedValue([{ kind: "videoinput", deviceId: "cam-1", label: "", groupId: "" }]),
    } as Partial<MediaDevices>);

    const { cameras } = await enumerateDevices();

    expect(cameras[0]?.label).toBe("Camera 1");
    expect(cameras[0]?.connection).toBe("unknown");
  });
});

describe("videoConstraints", () => {
  it("asks for a resolution without demanding one", () => {
    // `exact` would fail outright on a camera that tops out below the rung, turning a routine
    // hardware limit into an error.
    const constraints = videoConstraints("1080p", "cam-1");

    expect(constraints.width).toEqual({ ideal: 1920 });
    expect(constraints.height).toEqual({ ideal: 1080 });
    expect(constraints.deviceId).toEqual({ exact: "cam-1" });
  });

  it("leaves the choice to the browser on automatic", () => {
    expect(videoConstraints("auto")).toEqual({});
  });
});

describe("audioConstraints", () => {
  it("cleans up a voice, because that is what almost every broadcast is", () => {
    // Someone talking into a laptop in an untreated room does not know their room is the problem.
    // Someone broadcasting music knows they are, and goes looking for the setting.
    expect(audioConstraints("voice", "mic-1")).toMatchObject({
      deviceId: { exact: "mic-1" },
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
    });
  });

  it("leaves music completely alone", () => {
    // Noise suppression mangles anything sustained and gain control pumps a properly set-up
    // microphone. Nothing is removed and nothing is levelled.
    expect(audioConstraints("music")).toMatchObject({
      echoCancellation: false,
      noiseSuppression: false,
      autoGainControl: false,
    });
  });

  it("asks for 48kHz stereo in both modes", () => {
    // The mode decides what is done to the signal, never what is captured. Processing does not
    // downgrade the track — measured in the browser the studio runs in; see ADR 0017 §10.
    for (const mode of ["voice", "music"] as const) {
      expect(audioConstraints(mode)).toMatchObject({
        sampleRate: { ideal: 48000 },
        channelCount: { ideal: 2 },
      });
    }
  });

  it("defaults to speech", () => {
    expect(DEFAULT_MICROPHONE_MODE).toBe("voice");
  });
});

describe("requestCameraTrack", () => {
  it("requests only video, so acquiring a camera never disturbs a live microphone", async () => {
    const getUserMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("video")]));
    installMediaDevices({ getUserMedia } as Partial<MediaDevices>);

    await requestCameraTrack({ deviceId: "cam-1" });

    const constraints = getUserMedia.mock.calls[0]?.[0] as MediaStreamConstraints;
    expect(constraints.audio).toBe(false);
    expect((constraints.video as MediaTrackConstraints).deviceId).toEqual({ exact: "cam-1" });
  });

  it("falls back to any camera when the selected one has disappeared", async () => {
    // Unplugging a webcam mid-session should degrade to the built-in camera, not fail outright.
    const getUserMedia = vi
      .fn()
      .mockRejectedValueOnce(Object.assign(new Error("gone"), { name: "OverconstrainedError" }))
      .mockResolvedValueOnce(fakeStream([fakeTrack("video")]));

    installMediaDevices({ getUserMedia } as Partial<MediaDevices>);

    const track = await requestCameraTrack({ deviceId: "missing-cam" });

    expect(track).toBeDefined();
    expect(getUserMedia).toHaveBeenCalledTimes(2);
    const retryConstraints = getUserMedia.mock.calls[1]?.[0] as MediaStreamConstraints;
    expect((retryConstraints.video as MediaTrackConstraints).deviceId).toBeUndefined();
  });

  it("closes any hardware the browser opened beyond what was asked for", async () => {
    // Otherwise a stray track keeps a device — and its indicator light — open with nothing holding
    // a reference to close it.
    const wanted = fakeTrack("video", "cam-1");
    const extra = fakeTrack("audio", "mic-1");
    installMediaDevices({
      getUserMedia: vi.fn().mockResolvedValue(fakeStream([wanted, extra])),
    } as Partial<MediaDevices>);

    const track = await requestCameraTrack();

    expect(track).toBe(wanted);
    expect(extra.stop).toHaveBeenCalled();
    expect(wanted.stop).not.toHaveBeenCalled();
  });

  it("surfaces a friendly error when permission is refused", async () => {
    installMediaDevices({
      getUserMedia: vi
        .fn()
        .mockRejectedValue(Object.assign(new Error("denied"), { name: "NotAllowedError" })),
      // Hardware present, prompt blocked — otherwise this is the "no device" case instead.
      enumerateDevices: vi
        .fn()
        .mockResolvedValue([{ kind: "videoinput", deviceId: "cam-1", label: "Built-in", groupId: "g" }]),
    } as Partial<MediaDevices>);

    await expect(requestCameraTrack()).rejects.toMatchObject({ reason: "permission-denied" });
  });

  it("says the hardware is missing rather than blaming permissions", async () => {
    // Browsers report a dismissed prompt as NotAllowedError even with no hardware present, so
    // "allow access in your address bar" would be misleading advice on a machine with no camera.
    installMediaDevices({
      getUserMedia: vi
        .fn()
        .mockRejectedValue(Object.assign(new Error("Permission dismissed"), { name: "NotAllowedError" })),
      enumerateDevices: vi
        .fn()
        .mockResolvedValue([{ kind: "audioinput", deviceId: "mic-1", label: "Headset", groupId: "g" }]),
    } as Partial<MediaDevices>);

    await expect(requestCameraTrack()).rejects.toMatchObject({
      reason: "no-device",
      userMessage: expect.stringContaining("No camera"),
    });
  });
});

describe("requestMicrophoneTrack", () => {
  it("requests only audio, so changing microphone never re-opens the camera", async () => {
    const getUserMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("audio")]));
    installMediaDevices({ getUserMedia } as Partial<MediaDevices>);

    await requestMicrophoneTrack({ deviceId: "mic-1" });

    const constraints = getUserMedia.mock.calls[0]?.[0] as MediaStreamConstraints;
    expect(constraints.video).toBe(false);
    expect((constraints.audio as MediaTrackConstraints).deviceId).toEqual({ exact: "mic-1" });
  });

  it("falls back to any microphone when the selected one has disappeared", async () => {
    const getUserMedia = vi
      .fn()
      .mockRejectedValueOnce(Object.assign(new Error("gone"), { name: "NotFoundError" }))
      .mockResolvedValueOnce(fakeStream([fakeTrack("audio")]));

    installMediaDevices({ getUserMedia } as Partial<MediaDevices>);

    await expect(requestMicrophoneTrack({ deviceId: "missing-mic" })).resolves.toBeDefined();
    expect(getUserMedia).toHaveBeenCalledTimes(2);
  });
});

describe("requestScreenCapture", () => {
  it("says the share was cancelled rather than blaming camera permissions", async () => {
    // Dismissing the picker reports NotAllowedError, the same code as a blocked camera. Answering
    // "allow camera access in your address bar" to someone who pressed Cancel is nonsense.
    installMediaDevices({
      getDisplayMedia: vi
        .fn()
        .mockRejectedValue(Object.assign(new Error("Permission denied"), { name: "NotAllowedError" })),
    } as Partial<MediaDevices>);

    await expect(requestScreenCapture()).rejects.toMatchObject({
      userMessage: expect.stringContaining("cancelled"),
      recovery: expect.stringContaining("Share screen"),
    });
  });

  it("reports plainly when the browser cannot share a screen at all", async () => {
    installMediaDevices({});

    await expect(requestScreenCapture()).rejects.toMatchObject({ reason: "unsupported-browser" });
  });

  it("captures slides at 30fps and grades them to stay sharp", async () => {
    const track = fakeTrack("video", "screen-1");
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([track]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    const captured = await requestScreenCapture("presentation");

    expect(getDisplayMedia).toHaveBeenCalledWith(
      expect.objectContaining({ video: { frameRate: { ideal: 30 } } }),
    );

    // `detail` is what makes the publisher shed frames rather than sharpness. Text that
    // blurs is unreadable; a slide that updates a little late is not.
    expect(captured.video.contentHint).toBe("detail");
  });

  it("captures gameplay at 60fps and grades it to stay smooth", async () => {
    const track = fakeTrack("video", "screen-1");
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([track]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    const captured = await requestScreenCapture("gameplay");

    expect(getDisplayMedia).toHaveBeenCalledWith(
      expect.objectContaining({ video: { frameRate: { ideal: 60 } } }),
    );

    // The opposite trade: 900p at a steady 60 beats a razor-sharp stutter.
    expect(captured.video.contentHint).toBe("motion");
  });

  it("asks for the screen's sound, because that is what puts the checkbox in front of the sharer", async () => {
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("video")]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    await requestScreenCapture("gameplay");

    expect(getDisplayMedia).toHaveBeenCalledWith(expect.objectContaining({ audio: true }));
  });

  it("returns the screen's sound when the sharer included it, graded as music", async () => {
    const audio = fakeTrack("audio", "screen-audio");
    const getDisplayMedia = vi
      .fn()
      .mockResolvedValue(fakeStream([fakeTrack("video", "screen-1"), audio]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    const captured = await requestScreenCapture("gameplay");

    expect(captured.audio).toBe(audio);

    // Never speech into a headset. Saying so keeps voice processing away from a game's soundtrack.
    expect(captured.audio?.contentHint).toBe("music");
    expect(audio.stop).not.toHaveBeenCalled();
  });

  it("shares happily with no sound, because most sharers do not tick the box", async () => {
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("video")]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    await expect(requestScreenCapture("gameplay")).resolves.toMatchObject({ audio: null });
  });

  it("stops surplus tracks but never the one of each kind it keeps", async () => {
    // A regression guard: the obvious helper for this stops every track except one *kind*, which
    // here would stop the screen audio a moment after asking for it.
    const video = fakeTrack("video", "screen-1");
    const audio = fakeTrack("audio", "screen-audio");
    const surplus = fakeTrack("audio", "surplus");
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([video, audio, surplus]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    const captured = await requestScreenCapture("gameplay");

    expect(captured.video).toBe(video);
    expect(captured.audio).toBe(audio);
    expect(surplus.stop).toHaveBeenCalled();
    expect(video.stop).not.toHaveBeenCalled();
    expect(audio.stop).not.toHaveBeenCalled();
  });

  it("asks for a frame rate as ideal, never exact", async () => {
    // A display that cannot manage 60 should still share at whatever it does, rather than
    // failing the request outright.
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("video")]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    await requestScreenCapture("gameplay");

    const constraints = getDisplayMedia.mock.calls[0]?.[0] as MediaStreamConstraints;
    const video = constraints.video as MediaTrackConstraints;
    expect(video.frameRate).not.toHaveProperty("exact");
  });

  it("never constrains resolution, so a screen arrives at its native size", async () => {
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("video")]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    await requestScreenCapture("gameplay");

    const video = (getDisplayMedia.mock.calls[0]?.[0] as MediaStreamConstraints)
      .video as MediaTrackConstraints;
    expect(video.width).toBeUndefined();
    expect(video.height).toBeUndefined();
  });

  it("defaults to slides, because that is the safer guess for an unknown share", async () => {
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("video")]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    await requestScreenCapture();

    expect(getDisplayMedia).toHaveBeenCalledWith(
      expect.objectContaining({ video: { frameRate: { ideal: 30 } } }),
    );
  });
});

describe("describeTrackFormat", () => {
  it("reports what the camera is actually producing", () => {
    const track = fakeTrack("video", "cam-1", { width: 1920, height: 1080, frameRate: 29.97 });

    expect(describeTrackFormat(track)).toEqual({ width: 1920, height: 1080, frameRate: 30 });
  });

  it("returns nothing for audio or for a camera that reports no dimensions", () => {
    expect(describeTrackFormat(fakeTrack("audio"))).toBeNull();
    expect(describeTrackFormat(fakeTrack("video"))).toBeNull();
    expect(describeTrackFormat(null)).toBeNull();
  });
});

describe("stopStream", () => {
  it("stops every track", () => {
    const video = fakeTrack("video");
    const audio = fakeTrack("audio");

    stopStream(fakeStream([video, audio]));

    expect(video.stop).toHaveBeenCalled();
    expect(audio.stop).toHaveBeenCalled();
  });

  it("tolerates a null stream", () => {
    expect(() => stopStream(null)).not.toThrow();
  });
});

/**
 * Broadcast audio and frame-rate capture (docs/decisions/0017-broadcast-quality.md).
 */
describe("capture quality", () => {
  it("asks the microphone for 48 kHz stereo", async () => {
    // Chrome hands back whatever the device offers unless asked. 48 kHz stereo is what Opus carries
    // natively and what every platform ingests, so nothing downstream has to resample.
    const getUserMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("audio")]));
    installMediaDevices({ getUserMedia } as Partial<MediaDevices>);

    await requestMicrophoneTrack();

    const audio = (getUserMedia.mock.calls[0]?.[0] as MediaStreamConstraints)
      .audio as MediaTrackConstraints;
    expect(audio.sampleRate).toEqual({ ideal: 48000 });
    expect(audio.channelCount).toEqual({ ideal: 2 });
  });

  it("keeps music mode free of processing even with the broadcast constraints", () => {
    // Regression guard: asking for 48 kHz stereo must not drag the processing chain in with it.
    const constraints = audioConstraints("music");

    expect(constraints.echoCancellation).toBe(false);
    expect(constraints.noiseSuppression).toBe(false);
    expect(constraints.autoGainControl).toBe(false);
  });

  it("captures a screen at the frame rate asked for", async () => {
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("video")]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    await requestScreenCapture("gameplay", 120);

    expect(getDisplayMedia).toHaveBeenCalledWith(
      expect.objectContaining({ video: { frameRate: { ideal: 120 } } }),
    );
  });

  it("falls back to the mode's own rate when none is given", async () => {
    const getDisplayMedia = vi.fn().mockResolvedValue(fakeStream([fakeTrack("video")]));
    installMediaDevices({ getDisplayMedia } as Partial<MediaDevices>);

    await requestScreenCapture("gameplay");

    expect(getDisplayMedia).toHaveBeenCalledWith(
      expect.objectContaining({ video: { frameRate: { ideal: 60 } } }),
    );
  });

  it("offers 120 but is explicit that only 60 survives", () => {
    // The option exists because a 120 Hz display should be able to send what it has. Saying so
    // matters more than offering it: browsers encode WebRTC at up to 60, and so does every
    // platform this publishes to.
    expect(SCREEN_FRAME_RATES).toContain(120);
    expect(MAX_DELIVERED_FRAME_RATE).toBe(60);
  });
});
