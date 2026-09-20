import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AiPanel } from "@/components/studio/AiPanel";
import { useAiJobs } from "@/hooks/useAiJobs";
import type { AiCapabilities, AiJob } from "@/lib/types";

const { aiMock } = vi.hoisted(() => ({
  aiMock: {
    capabilities: vi.fn(),
    jobs: vi.fn(),
    request: vi.fn(),
    cancel: vi.fn(),
  },
}));

vi.mock("@/lib/api/ai", () => ({ aiApi: aiMock }));

const SESSION_ID = "11111111-1111-1111-1111-111111111111";

function buildCapabilities(overrides: Partial<AiCapabilities> = {}): AiCapabilities {
  return {
    configured: true,
    features: [
      {
        kind: "SessionRecap",
        displayName: "Session recap",
        description: "What happened during the broadcast.",
        enabled: true,
      },
      {
        kind: "StreamQualityReview",
        displayName: "Stream quality review",
        description: "What went wrong, and what to change.",
        enabled: true,
      },
      {
        kind: "Chapters",
        displayName: "Chapters",
        description: "Chapter markers for the recording.",
        enabled: false,
      },
    ],
    ...overrides,
  };
}

function buildJob(overrides: Partial<AiJob> = {}): AiJob {
  return {
    id: "job-1",
    liveSessionId: SESSION_ID,
    kind: "SessionRecap",
    status: "Succeeded",
    attemptCount: 1,
    maxAttempts: 3,
    requestedAt: new Date().toISOString(),
    startedAt: new Date().toISOString(),
    completedAt: new Date().toISOString(),
    nextAttemptAt: null,
    summary: "The broadcast ran for twelve minutes without interruption.",
    resultJson: '{"summary":"The broadcast ran for twelve minutes without interruption."}',
    errorCode: null,
    errorMessage: null,
    modelId: "claude-opus-5",
    inputTokens: 1200,
    outputTokens: 350,
    estimatedCostUsd: 0.014750,
    sourceRangeStart: new Date().toISOString(),
    sourceRangeEnd: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

/** Renders the panel wired to the real hook, so the two are exercised together. */
function Harness() {
  const ai = useAiJobs(SESSION_ID);
  return <AiPanel ai={ai} />;
}

beforeEach(() => {
  vi.clearAllMocks();
  aiMock.capabilities.mockResolvedValue(buildCapabilities());
  aiMock.jobs.mockResolvedValue([]);
});

describe("AiPanel", () => {
  it("says nothing at all when the deployment has no AI provider", async () => {
    // A row of dead buttons suggests something is broken, when the feature was simply never
    // turned on.
    aiMock.capabilities.mockResolvedValue(buildCapabilities({ configured: false, features: [] }));

    const { container } = render(<Harness />);

    await waitFor(() => expect(aiMock.capabilities).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it("offers the features this deployment has switched on", async () => {
    render(<Harness />);

    expect(await screen.findByRole("button", { name: "Session recap" })).toBeEnabled();
    expect(await screen.findByRole("button", { name: "Stream quality review" })).toBeEnabled();
    // Independently disableable is a phase requirement, and the UI has to honour it.
    expect(await screen.findByRole("button", { name: "Chapters" })).toBeDisabled();
  });

  it("queues work and shows it without waiting for a result", async () => {
    aiMock.request.mockResolvedValue(
      buildJob({ id: "job-2", status: "Queued", summary: null, resultJson: null, completedAt: null }),
    );

    render(<Harness />);
    await userEvent.click(await screen.findByRole("button", { name: "Session recap" }));

    expect(aiMock.request).toHaveBeenCalledWith(SESSION_ID, "SessionRecap");
    expect(await screen.findByText("Queued")).toBeInTheDocument();
  });

  it("shows what produced a finished answer and what it cost", async () => {
    // docs/13-ai-features.md requires output to be traceable, and a feature whose price is
    // invisible is one nobody budgets for.
    aiMock.jobs.mockResolvedValue([buildJob()]);

    render(<Harness />);

    // Scoped to the summary line: the same sentence also appears inside the full-result JSON, and
    // an unscoped match would pass on either.
    expect(
      await screen.findByText("The broadcast ran for twelve minutes without interruption.", {
        selector: "p",
      }),
    ).toBeInTheDocument();

    expect(await screen.findByText(/claude-opus-5/)).toBeInTheDocument();
    expect(await screen.findByText(/\$0\.0147/)).toBeInTheDocument();
  });

  it("explains a failure and says a retry is coming", async () => {
    aiMock.jobs.mockResolvedValue([
      buildJob({
        status: "Queued",
        attemptCount: 1,
        summary: null,
        resultJson: null,
        errorCode: "AI_PROVIDER_UNAVAILABLE",
        errorMessage: "The AI provider is temporarily unavailable.",
        nextAttemptAt: new Date(Date.now() + 30_000).toISOString(),
      }),
    ]);

    render(<Harness />);

    expect(await screen.findByText(/temporarily unavailable/)).toBeInTheDocument();
    expect(await screen.findByText(/attempt 2 of 3/)).toBeInTheDocument();
  });

  it("does not offer a retry message for a job that has finally failed", async () => {
    aiMock.jobs.mockResolvedValue([
      buildJob({
        status: "Failed",
        attemptCount: 1,
        summary: null,
        resultJson: null,
        errorCode: "AI_REFUSED",
        errorMessage: "The model declined to analyse this session.",
      }),
    ]);

    render(<Harness />);

    expect(await screen.findByText(/declined to analyse/)).toBeInTheDocument();
    expect(screen.queryByText(/attempt 2 of 3/)).not.toBeInTheDocument();
  });

  it("offers to cancel only a job that has not started", async () => {
    aiMock.jobs.mockResolvedValue([
      buildJob({ id: "queued", status: "Queued", summary: null, resultJson: null }),
      buildJob({ id: "done", status: "Succeeded" }),
    ]);

    render(<Harness />);

    await screen.findByText("Queued");
    expect(screen.getAllByRole("button", { name: "Cancel" })).toHaveLength(1);
  });

  it("surfaces a refusal to queue rather than failing silently", async () => {
    aiMock.request.mockRejectedValue(new Error("nope"));

    render(<Harness />);
    await userEvent.click(await screen.findByRole("button", { name: "Session recap" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/could not start/i);
  });
});
