import { describe, expect, it, vi } from "vitest";
import {
  type CapabilityInputs,
  type EncodingProbe,
  type RungSupport,
  chooseStartingQuality,
  codecFamily,
  probeMachine,
} from "@/lib/media/capability";

function rung(
  quality: RungSupport["quality"],
  frameRate: number,
  overrides: Partial<RungSupport> = {},
): RungSupport {
  return { quality, frameRate, supported: true, smooth: true, powerEfficient: false, ...overrides };
}

function inputs(overrides: Partial<CapabilityInputs> = {}): CapabilityInputs {
  return {
    cores: 8,
    hardwareCodecs: [],
    probed: true,
    rungs: [rung("1080p", 60), rung("1080p", 30), rung("720p", 60), rung("720p", 30), rung("540p", 30)],
    ...overrides,
  };
}

describe("chooseStartingQuality", () => {
  it("takes the best rung a GPU will accelerate", () => {
    const choice = chooseStartingQuality(
      inputs({
        rungs: [
          rung("1080p", 60, { powerEfficient: true }),
          rung("1080p", 30, { powerEfficient: true }),
          rung("720p", 30, { powerEfficient: true }),
        ],
      }),
    );

    expect(choice).toMatchObject({ quality: "1080p", screenFrameRate: 60 });
    expect(choice.reason).toMatch(/graphics card/i);
  });

  it("decides on hardware acceleration, not on the browser's optimism", () => {
    // Measured in a container with no GPU at all, Chrome reports `smooth: true` for 1080p60
    // software encoding. It is a claim that the codec can run, not a load test of this machine —
    // so believing it would start every laptop at the rung most likely to cook it.
    const choice = chooseStartingQuality(
      inputs({
        cores: 4,
        rungs: [rung("1080p", 60, { smooth: true }), rung("720p", 30, { smooth: true })],
      }),
    );

    expect(choice.quality).not.toBe("1080p");
  });

  it("allows software 1080p on a machine with cores to spare — but not at 60", () => {
    // Software 1080p60 is where a machine without a hardware encoder falls over, and it falls
    // over live rather than here.
    const choice = chooseStartingQuality(inputs({ cores: 16 }));

    expect(choice).toMatchObject({ quality: "1080p", screenFrameRate: 30 });
    expect(choice.reason).toMatch(/16 CPU cores/);
  });

  it("starts a modest machine at 720p", () => {
    const choice = chooseStartingQuality(inputs({ cores: 8 }));

    expect(choice).toMatchObject({ quality: "720p", screenFrameRate: 30 });
  });

  it("starts a weak machine lower still", () => {
    const choice = chooseStartingQuality(inputs({ cores: 2 }));

    expect(choice).toMatchObject({ quality: "540p", screenFrameRate: 30 });
    expect(choice.reason).toMatch(/2 CPU cores/);
  });

  it("keeps the platform default when the browser will not answer", () => {
    // Inventing a worse default from no evidence would make this a downgrade for everybody whose
    // browser lacks the API.
    const choice = chooseStartingQuality(inputs({ probed: false, rungs: [] }));

    expect(choice).toMatchObject({ quality: "720p", screenFrameRate: 30 });
    expect(choice.reason).toMatch(/does not report/i);
  });

  it("ignores rungs the browser says it cannot encode at all", () => {
    const choice = chooseStartingQuality(
      inputs({
        cores: 16,
        rungs: [rung("1080p", 60, { supported: false }), rung("720p", 30), rung("540p", 30)],
      }),
    );

    expect(choice.quality).toBe("720p");
  });

  it("names its evidence, because the settings visibly moved", () => {
    // Somebody who finds the quality picker on a different rung than last time deserves to know
    // why without going looking.
    for (const cores of [2, 8, 16]) {
      expect(chooseStartingQuality(inputs({ cores })).reason.length).toBeGreaterThan(20);
    }
  });
});

describe("probeMachine", () => {
  /** Answers per codec, so a machine that accelerates one and not another can be described. */
  function probeReturning(
    answers: Record<string, { supported?: boolean; smooth?: boolean; powerEfficient?: boolean }>,
  ): EncodingProbe {
    return vi.fn(async (configuration) => {
      const family = codecFamily(configuration.video.contentType);
      const answer = answers[family] ?? {};

      return {
        supported: answer.supported ?? true,
        smooth: answer.smooth ?? true,
        powerEfficient: answer.powerEfficient ?? false,
      };
    });
  }

  it("reports which codecs the GPU can encode", async () => {
    const capability = await probeMachine(
      probeReturning({ "video/H264": { powerEfficient: true } }),
      16,
    );

    expect(capability.hardwareCodecs).toEqual(["video/H264"]);
    expect(capability.recommended.quality).toBe("1080p");
  });

  it("finds hardware on a codec that is not H.264", async () => {
    // Some Intel and AMD parts accelerate VP9 and not H.264. Reporting only H.264 would leave
    // those machines encoding in software with a perfectly good encoder sitting idle.
    const capability = await probeMachine(
      probeReturning({ "video/VP9": { powerEfficient: true } }),
      4,
    );

    expect(capability.hardwareCodecs).toEqual(["video/VP9"]);

    // And the recommendation follows the hardware, not the core count.
    expect(capability.recommended.quality).toBe("1080p");
  });

  it("reports no hardware when nothing is accelerated", async () => {
    const capability = await probeMachine(probeReturning({}), 8);

    expect(capability.hardwareCodecs).toEqual([]);
    expect(capability.probed).toBe(true);
    expect(capability.recommended.quality).toBe("720p");
  });

  it("carries on when the browser has no media capabilities at all", async () => {
    // Safari and older browsers. They must still broadcast, on the platform default.
    const capability = await probeMachine(null, 8);

    expect(capability.probed).toBe(false);
    expect(capability.recommended).toMatchObject({ quality: "720p", screenFrameRate: 30 });
  });

  it("survives a browser that throws on a configuration it dislikes", async () => {
    // One refused configuration is not a refused browser.
    let calls = 0;
    const probe: EncodingProbe = async () => {
      calls += 1;
      if (calls % 2 === 0) throw new TypeError("unsupported configuration");
      return { supported: true, smooth: true, powerEfficient: false };
    };

    const capability = await probeMachine(probe, 8);

    expect(capability.probed).toBe(true);
    expect(capability.rungs.length).toBeGreaterThan(0);
  });

  it("treats a browser that refuses everything as unprobed", async () => {
    const probe: EncodingProbe = async () => {
      throw new TypeError("no");
    };

    const capability = await probeMachine(probe, 8);

    expect(capability.probed).toBe(false);
    expect(capability.recommended.quality).toBe("720p");
  });

  it("asks about the bitrates it would actually send", async () => {
    // Asking about 1080p60 at a trivial bitrate would get an answer about a broadcast nobody is
    // going to make.
    const probe: EncodingProbe = vi.fn(async () => ({
      supported: true,
      smooth: true,
      powerEfficient: false,
    }));

    await probeMachine(probe, 8);

    const asked = vi.mocked(probe).mock.calls.map(([configuration]) => configuration);
    const hardest = asked.find((call) => call.video.width === 1920 && call.video.framerate === 60);
    expect(hardest?.video.bitrate).toBeGreaterThanOrEqual(5_000_000);
  });

  it("asks as webrtc, not as a recording", async () => {
    // The answer differs: a browser can record a configuration it cannot send in real time.
    const probe: EncodingProbe = vi.fn(async () => ({
      supported: true,
      smooth: true,
      powerEfficient: false,
    }));

    await probeMachine(probe, 8);

    expect(vi.mocked(probe).mock.calls[0]?.[0]).toMatchObject({ type: "webrtc" });
  });
});
