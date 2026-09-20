using LiveStream.Application.Abstractions;
using LiveStream.Application.Ai;
using LiveStream.Domain.Ai;
using LiveStream.Domain.Common;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>
/// The AI job lifecycle (implementation/phase-6-ai-live-operations.md).
///
/// Every rule here exists because AI calls cost money and fail in ways the platform does not
/// control: an attempt limit, a distinction between failures worth retrying and failures that are
/// final, and a backoff long enough to be worth waiting for.
/// </summary>
public class AiJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static AiJob Queue(AiJobKind kind = AiJobKind.SessionRecap) =>
        AiJob.Queue(Guid.NewGuid(), Guid.NewGuid(), kind, Guid.NewGuid(), Now);

    [Fact]
    public void A_new_job_is_queued_and_due_immediately()
    {
        var job = Queue();

        Assert.Equal(AiJobStatus.Queued, job.Status);
        Assert.Equal(Now, job.NextAttemptAt);
        Assert.Equal(0, job.AttemptCount);
        Assert.True(job.IsActive);
    }

    [Fact]
    public void An_unknown_kind_is_refused()
    {
        Assert.Throws<DomainException>(() =>
            AiJob.Queue(Guid.NewGuid(), Guid.NewGuid(), (AiJobKind)99, Guid.NewGuid(), Now));
    }

    [Fact]
    public void Starting_counts_an_attempt()
    {
        var job = Queue();

        job.Start(Now);

        Assert.Equal(AiJobStatus.Running, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(Now, job.StartedAt);
    }

    [Fact]
    public void A_running_job_cannot_be_started_again()
    {
        // The guard that stops one job being billed twice if two workers race the same row.
        var job = Queue();
        job.Start(Now);

        Assert.Throws<DomainException>(() => job.Start(Now));
    }

    [Fact]
    public void A_retryable_failure_goes_back_to_the_queue_with_a_delay()
    {
        var job = Queue();
        job.Start(Now);

        job.Fail("AI_RATE_LIMITED", "Slow down.", retryable: true, Now);

        Assert.Equal(AiJobStatus.Queued, job.Status);
        Assert.True(job.NextAttemptAt > Now);
        Assert.Null(job.CompletedAt);
        Assert.Equal("AI_RATE_LIMITED", job.ErrorCode);
    }

    [Fact]
    public void A_failure_that_is_not_retryable_is_final_on_the_first_attempt()
    {
        // A refusal or a malformed request does not become correct on the third try; retrying just
        // spends money to be told the same thing.
        var job = Queue();
        job.Start(Now);

        job.Fail("AI_REFUSED", "Declined.", retryable: false, Now);

        Assert.Equal(AiJobStatus.Failed, job.Status);
        Assert.Equal(Now, job.CompletedAt);
    }

    [Fact]
    public void A_job_is_abandoned_after_the_attempt_limit()
    {
        var job = Queue();

        for (var attempt = 0; attempt < AiJob.MaxAttempts; attempt++)
        {
            job.Start(job.NextAttemptAt);
            job.Fail("AI_PROVIDER_UNAVAILABLE", "Down.", retryable: true, job.NextAttemptAt);
        }

        Assert.Equal(AiJobStatus.Failed, job.Status);
        Assert.Equal(AiJob.MaxAttempts, job.AttemptCount);
        Assert.False(job.IsActive);
    }

    [Fact]
    public void The_backoff_grows_between_attempts()
    {
        // Minutes rather than seconds: nothing is waiting on this, and the failures worth retrying
        // are provider outages and rate limits.
        Assert.True(AiJob.RetryDelay(2) > AiJob.RetryDelay(1));
        Assert.True(AiJob.RetryDelay(3) > AiJob.RetryDelay(2));
        Assert.True(AiJob.RetryDelay(1) >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Success_records_what_produced_the_answer_and_over_what_range()
    {
        // docs/13-ai-features.md: "AI output must be traceable to the session/time range used."
        var job = Queue();
        job.Start(Now);

        var from = Now.AddMinutes(-30);
        job.Succeed("It went well.", """{"summary":"It went well."}""", "claude-opus-5", 1200, 400,
            from, Now, Now);

        Assert.Equal(AiJobStatus.Succeeded, job.Status);
        Assert.Equal("claude-opus-5", job.ModelId);
        Assert.Equal(1200, job.InputTokens);
        Assert.Equal(from, job.SourceRangeStart);
        Assert.Equal(Now, job.SourceRangeEnd);
    }

    [Fact]
    public void Success_clears_the_failure_from_an_earlier_attempt()
    {
        // Otherwise a job that succeeded on its second try still reports the first one's error, and
        // the list view shows a green job with a red reason.
        var job = Queue();
        job.Start(Now);
        job.Fail("AI_RATE_LIMITED", "Slow down.", retryable: true, Now);
        job.Start(job.NextAttemptAt);

        job.Succeed("Fine.", "{}", "claude-opus-5", 10, 10, null, null, Now);

        Assert.Null(job.ErrorCode);
        Assert.Null(job.ErrorMessage);
    }

    [Fact]
    public void A_queued_job_can_be_cancelled_and_a_running_one_cannot()
    {
        var queued = Queue();
        queued.Cancel(Now);
        Assert.Equal(AiJobStatus.Cancelled, queued.Status);

        var running = Queue();
        running.Start(Now);
        Assert.Throws<DomainException>(() => running.Cancel(Now));
    }
}

public class AiPricingTests
{
    [Fact]
    public void A_known_model_is_priced_from_published_rates()
    {
        // One million input tokens at $5, one million output at $25.
        Assert.Equal(30.00m, AiPricing.EstimateUsd("claude-opus-5", 1_000_000, 1_000_000));
    }

    [Fact]
    public void A_small_job_is_not_rounded_away_to_zero()
    {
        // Rounding to cents would report every ordinary job as free.
        var cost = AiPricing.EstimateUsd("claude-opus-5", 2_000, 500);

        Assert.NotNull(cost);
        Assert.True(cost > 0m);
    }

    [Fact]
    public void An_unknown_model_reports_nothing_rather_than_zero()
    {
        // A job showing $0.00 reads as free, and a model whose price we do not know is not free.
        Assert.Null(AiPricing.EstimateUsd("some-future-model", 1000, 1000));
        Assert.Null(AiPricing.EstimateUsd(null, 1000, 1000));
    }
}

public class AiOptionsTests
{
    [Fact]
    public void Everything_is_off_until_a_deployment_turns_it_on()
    {
        // A deployment should have to choose to send its session data to a model.
        var options = new AiOptions();

        Assert.False(options.Enabled);
        Assert.False(options.IsFeatureEnabled(AiJobKind.SessionRecap));
    }

    [Fact]
    public void A_feature_can_be_switched_off_on_its_own()
    {
        // The phase's acceptance criteria require features to be independently disableable.
        var options = new AiOptions { Enabled = true };
        options.Features[nameof(AiJobKind.Chapters)] = false;

        Assert.True(options.IsFeatureEnabled(AiJobKind.SessionRecap));
        Assert.False(options.IsFeatureEnabled(AiJobKind.Chapters));
    }

    [Fact]
    public void The_master_switch_overrides_every_feature()
    {
        var options = new AiOptions { Enabled = false };
        options.Features[nameof(AiJobKind.SessionRecap)] = true;

        Assert.False(options.IsFeatureEnabled(AiJobKind.SessionRecap));
    }
}

public class AiTimelineSamplingTests
{
    private static AiTimelineEntry Entry(int index) =>
        new(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero).AddSeconds(index), index, "Session",
            $"Event{index}", null);

    [Fact]
    public void A_short_timeline_is_passed_through_whole()
    {
        var timeline = Enumerable.Range(0, 10).Select(Entry).ToList();

        Assert.Same(timeline, AiJobRunner.Sample(timeline, 400));
    }

    [Fact]
    public void A_long_timeline_keeps_both_ends()
    {
        // Truncating to the first N would throw away the last hour of a four-hour broadcast, which
        // is exactly the part a recap needs.
        var timeline = Enumerable.Range(0, 5_000).Select(Entry).ToList();

        var sampled = AiJobRunner.Sample(timeline, 100);

        Assert.True(sampled.Count <= 100);
        Assert.Same(timeline[0], sampled[0]);
        Assert.Same(timeline[^1], sampled[^1]);
    }

    [Fact]
    public void Sampling_preserves_order()
    {
        var timeline = Enumerable.Range(0, 1_000).Select(Entry).ToList();

        var sampled = AiJobRunner.Sample(timeline, 50);

        Assert.Equal(sampled.OrderBy(entry => entry.At), sampled);
    }

    [Fact]
    public void An_empty_budget_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(AiJobRunner.Sample(Enumerable.Range(0, 10).Select(Entry).ToList(), 0));
    }
}
