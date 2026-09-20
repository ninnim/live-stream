using LiveStream.Application.Common;
using LiveStream.Domain.Common;
using LiveStream.Domain.Distribution;
using Xunit;

namespace LiveStream.UnitTests;

/// <summary>Behaviour of the destination aggregate, independent of any transport or persistence.</summary>
public class StreamDestinationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();

    private static StreamDestination Create(string ingestUrl = "rtmp://live.example.test/app") =>
        StreamDestination.CreateWithStreamKey(SessionId, DestinationProvider.CustomRtmp, "Partner",
            ingestUrl, "cipher", Actor, Now);

    // -----------------------------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The scheme check is a security boundary, not formatting: this value becomes an argument to an
    /// encoder process, so a file or http scheme would be a way to make it write somewhere else.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://example.test/live")]
    [InlineData("https://example.test/live")]
    [InlineData("srt://example.test:9000")]
    [InlineData("/tmp/output.flv")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Rejects_non_rtmp_ingest_urls(string ingestUrl)
    {
        var exception = Assert.Throws<DomainException>(() => Create(ingestUrl));
        Assert.Equal(ErrorCodes.ValidationFailed, exception.ErrorCode);
    }

    [Theory]
    [InlineData("rtmp://live.example.test/app")]
    [InlineData("rtmps://live-api-s.facebook.com:443/rtmp")]
    public void Accepts_rtmp_and_rtmps(string ingestUrl)
    {
        var destination = Create(ingestUrl);
        Assert.Equal(ingestUrl, destination.IngestUrl);
    }

    [Fact]
    public void Rejects_an_empty_display_name() =>
        Assert.Throws<DomainException>(() => StreamDestination.CreateWithStreamKey(SessionId,
            DestinationProvider.CustomRtmp, "   ", "rtmp://a.test/app", "cipher", Actor, Now));

    [Fact]
    public void Rejects_an_empty_stream_key() =>
        Assert.Throws<DomainException>(() => StreamDestination.CreateWithStreamKey(SessionId,
            DestinationProvider.CustomRtmp, "Partner", "rtmp://a.test/app", "  ", Actor, Now));

    // -----------------------------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Starts_idle_and_enabled()
    {
        var destination = Create();

        Assert.Equal(DestinationStatus.Idle, destination.Status);
        Assert.True(destination.Enabled);
        Assert.True(destination.IsStartable);
        Assert.Contains(destination.Events, e => e.Type == DestinationEventType.Created);
    }

    [Fact]
    public void Transition_records_an_event_and_moves_state()
    {
        var destination = Create();

        destination.TransitionTo(DestinationStatus.Preparing, Now);
        destination.TransitionTo(DestinationStatus.Connecting, Now.AddSeconds(1));
        destination.TransitionTo(DestinationStatus.Live, Now.AddSeconds(2));

        Assert.Equal(DestinationStatus.Live, destination.Status);
        Assert.Equal(Now.AddSeconds(2), destination.StartedAt);
        Assert.Contains(destination.Events, e => e.ToStatus == DestinationStatus.Live);
    }

    /// <summary>Duplicate relay reports must not append a second event or reset timers.</summary>
    [Fact]
    public void Repeating_the_current_state_is_a_no_op()
    {
        var destination = Create();
        destination.TransitionTo(DestinationStatus.Preparing, Now);

        var eventCount = destination.Events.Count;
        destination.TransitionTo(DestinationStatus.Preparing, Now.AddSeconds(5));

        Assert.Equal(eventCount, destination.Events.Count);
        Assert.Equal(Now, destination.StateEnteredAt);
    }

    [Fact]
    public void Illegal_transitions_throw()
    {
        var destination = Create();
        Assert.Throws<InvalidStateTransitionException>(() =>
            destination.TransitionTo(DestinationStatus.Live, Now));
    }

    // -----------------------------------------------------------------------------------------
    // Retry
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Scheduling_a_retry_records_the_attempt_and_the_due_time()
    {
        var destination = Create();
        destination.TransitionTo(DestinationStatus.Preparing, Now);
        destination.TransitionTo(DestinationStatus.Connecting, Now);

        destination.ScheduleRetry(Now, TimeSpan.FromSeconds(10), ErrorCodes.DestinationUnavailable, "dropped");

        Assert.Equal(DestinationStatus.Retrying, destination.Status);
        Assert.Equal(1, destination.AttemptCount);
        Assert.Equal(Now.AddSeconds(10), destination.NextRetryAt);
        Assert.False(destination.IsRetryDue(Now.AddSeconds(9)));
        Assert.True(destination.IsRetryDue(Now.AddSeconds(10)));
        Assert.Contains(destination.Events, e => e.Type == DestinationEventType.RetryScheduled);
    }

    [Fact]
    public void Exhausting_retries_ends_in_error_with_the_cause_recorded()
    {
        var destination = Create();
        destination.TransitionTo(DestinationStatus.Preparing, Now);
        destination.TransitionTo(DestinationStatus.Connecting, Now);
        destination.ScheduleRetry(Now, TimeSpan.FromSeconds(5), ErrorCodes.DestinationUnavailable, "dropped");

        destination.ExhaustRetries(Now.AddMinutes(1), ErrorCodes.DestinationUnavailable, "gave up");

        Assert.Equal(DestinationStatus.Error, destination.Status);
        Assert.Equal(ErrorCodes.DestinationUnavailable, destination.LastErrorCode);
        Assert.Null(destination.NextRetryAt);
        Assert.Contains(destination.Events, e => e.Type == DestinationEventType.RetryExhausted);
    }

    /// <summary>Going live clears the previous error so a recovered destination does not look broken.</summary>
    [Fact]
    public void Reaching_live_clears_the_last_error()
    {
        var destination = Create();
        destination.TransitionTo(DestinationStatus.Preparing, Now);
        destination.TransitionTo(DestinationStatus.Connecting, Now);
        destination.ScheduleRetry(Now, TimeSpan.FromSeconds(5), ErrorCodes.DestinationUnavailable, "dropped");
        destination.TransitionTo(DestinationStatus.Connecting, Now.AddSeconds(6));
        destination.TransitionTo(DestinationStatus.Live, Now.AddSeconds(7));

        Assert.Null(destination.LastErrorCode);
        Assert.Null(destination.LastErrorMessage);
        Assert.Null(destination.NextRetryAt);
    }

    // -----------------------------------------------------------------------------------------
    // Per-run state
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A destination is reused across sessions, so anything belonging to the previous broadcast
    /// must be gone before the next one starts — most importantly the resolved key.
    /// </summary>
    [Fact]
    public void Reset_clears_everything_from_the_previous_run()
    {
        var destination = Create();
        destination.TransitionTo(DestinationStatus.Preparing, Now);
        destination.ApplyResolvedTarget("rtmp://live.example.test/app", "resolved-cipher", "broadcast-1",
            "https://example.test/watch", Now);
        destination.TransitionTo(DestinationStatus.Connecting, Now);
        destination.TransitionTo(DestinationStatus.Live, Now);
        destination.RecordBytesSent(50_000, Now);

        destination.ResetForRun(Now.AddHours(1));

        Assert.Null(destination.ResolvedStreamKeyCipher);
        Assert.Null(destination.ResolvedIngestUrl);
        Assert.Null(destination.ExternalBroadcastId);
        Assert.Null(destination.WatchUrl);
        Assert.Null(destination.StartedAt);
        Assert.Equal(0, destination.AttemptCount);
        Assert.Equal(0, destination.BytesSent);

        // The configured key survives: it is the destination's identity, not the run's.
        Assert.Equal("cipher", destination.StreamKeyCipher);
    }

    /// <summary>The resolved-target event records the endpoint, never the key.</summary>
    [Fact]
    public void Resolved_target_event_does_not_contain_the_key()
    {
        var destination = Create();
        destination.TransitionTo(DestinationStatus.Preparing, Now);
        destination.ApplyResolvedTarget("rtmp://live.example.test/app?sig=abc", "super-secret-cipher", null, null, Now);

        var resolved = destination.Events.Single(e => e.Type == DestinationEventType.TargetResolved);

        Assert.DoesNotContain("super-secret-cipher", resolved.Detail);
        Assert.DoesNotContain("sig=abc", resolved.Detail);
        Assert.Contains("redacted", resolved.Detail);
    }

    [Fact]
    public void Byte_counter_never_goes_backwards()
    {
        var destination = Create();
        destination.RecordBytesSent(1000, Now);
        destination.RecordBytesSent(400, Now.AddSeconds(1));

        Assert.Equal(1000, destination.BytesSent);
    }

    /// <summary>
    /// Each retry starts a fresh encoder counting from zero. Throughput must keep climbing across
    /// the reconnect rather than freezing until the new attempt overtakes the old total — which,
    /// after a long outage, is minutes of a stuck number.
    /// </summary>
    [Fact]
    public void Byte_counter_accumulates_across_retries()
    {
        var destination = Create();
        destination.TransitionTo(DestinationStatus.Preparing, Now);
        destination.ApplyResolvedTarget("rtmp://live.example.test/app", "cipher", null, null, Now);
        destination.TransitionTo(DestinationStatus.Connecting, Now);
        destination.TransitionTo(DestinationStatus.Live, Now);
        destination.RecordBytesSent(10_000, Now);

        // The platform drops and the orchestrator starts a second attempt.
        destination.ScheduleRetry(Now, TimeSpan.FromSeconds(5), ErrorCodes.DestinationUnavailable, "dropped");
        destination.TransitionTo(DestinationStatus.Preparing, Now.AddSeconds(5));
        destination.ApplyResolvedTarget("rtmp://live.example.test/app", "cipher", null, null, Now.AddSeconds(5));

        // A brand-new encoder reporting its own small total must add to what came before.
        destination.RecordBytesSent(500, Now.AddSeconds(6));

        Assert.Equal(10_500, destination.BytesSent);
    }

    // -----------------------------------------------------------------------------------------
    // Configuration guards
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Cannot_disable_a_running_destination()
    {
        var destination = Create();
        destination.TransitionTo(DestinationStatus.Preparing, Now);
        destination.TransitionTo(DestinationStatus.Connecting, Now);
        destination.TransitionTo(DestinationStatus.Live, Now);

        var exception = Assert.Throws<DomainException>(() => destination.SetEnabled(false, Now, Actor));
        Assert.Equal(ErrorCodes.SessionNotReady, exception.ErrorCode);
    }

    [Fact]
    public void Disabling_an_idle_destination_moves_it_to_disabled()
    {
        var destination = Create();
        destination.SetEnabled(false, Now, Actor);

        Assert.False(destination.Enabled);
        Assert.Equal(DestinationStatus.Disabled, destination.Status);
        Assert.False(destination.IsStartable);

        destination.SetEnabled(true, Now, Actor);

        Assert.True(destination.Enabled);
        Assert.Equal(DestinationStatus.Idle, destination.Status);
        Assert.True(destination.IsStartable);
    }

    [Fact]
    public void Linked_account_destinations_have_no_stream_key_to_change()
    {
        var destination = StreamDestination.CreateWithLinkedAccount(SessionId, DestinationProvider.YouTube,
            "Channel", Guid.NewGuid(), Actor, Now);

        Assert.Throws<DomainException>(() =>
            destination.UpdateStreamKey("rtmp://a.test/app", "cipher", Now, Actor));
    }

    // -----------------------------------------------------------------------------------------
    // Backoff
    // -----------------------------------------------------------------------------------------

    /// <summary>Doubling with a ceiling, so a long outage does not retry every five seconds forever.</summary>
    [Fact]
    public void Retry_backoff_doubles_and_then_holds_at_the_ceiling()
    {
        var options = new DistributionOptions
        {
            InitialRetryDelaySeconds = 5,
            MaxRetryDelaySeconds = 60,
        };

        Assert.Equal(TimeSpan.FromSeconds(5), options.RetryDelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(10), options.RetryDelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(20), options.RetryDelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(40), options.RetryDelayForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryDelayForAttempt(5));
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryDelayForAttempt(50));
    }

    /// <summary>A large attempt count must not overflow the shift into a negative or zero delay.</summary>
    [Fact]
    public void Retry_backoff_survives_absurd_attempt_counts()
    {
        var options = new DistributionOptions { InitialRetryDelaySeconds = 5, MaxRetryDelaySeconds = 120 };

        Assert.Equal(TimeSpan.FromSeconds(120), options.RetryDelayForAttempt(int.MaxValue));
        Assert.Equal(TimeSpan.FromSeconds(5), options.RetryDelayForAttempt(0));
        Assert.Equal(TimeSpan.FromSeconds(5), options.RetryDelayForAttempt(-1));
    }
}
