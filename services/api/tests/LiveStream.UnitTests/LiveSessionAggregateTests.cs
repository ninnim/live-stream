using LiveStream.Domain.Common;
using LiveStream.Domain.Sessions;
using Xunit;

namespace LiveStream.UnitTests;

public class LiveSessionAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static LiveSession NewSession(bool recordingEnabled = false) =>
        LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), "Product launch", "A description",
            LiveSessionVisibility.Public, recordingEnabled, Now);

    [Fact]
    public void Create_starts_in_draft_and_logs_a_created_event()
    {
        var session = NewSession();

        Assert.Equal(LiveSessionStatus.Draft, session.Status);
        Assert.Null(session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.Single(session.Events, e => e.Type is LiveSessionEventType.Created);
    }

    [Fact]
    public void Create_generates_an_unguessable_media_path_distinct_from_the_id()
    {
        var first = NewSession();
        var second = NewSession();

        Assert.StartsWith("ls_", first.MediaPathName, StringComparison.Ordinal);
        Assert.NotEqual(first.MediaPathName, second.MediaPathName);
        Assert.DoesNotContain(first.Id.ToString("N"), first.MediaPathName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_title(string title)
    {
        var exception = Assert.Throws<DomainException>(() =>
            LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), title, null, LiveSessionVisibility.Private, false, Now));

        Assert.Equal(ErrorCodes.ValidationFailed, exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_an_overlong_title()
    {
        var exception = Assert.Throws<DomainException>(() =>
            LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), new string('a', 201), null,
                LiveSessionVisibility.Private, false, Now));

        Assert.Equal(ErrorCodes.ValidationFailed, exception.ErrorCode);
    }

    [Fact]
    public void TransitionTo_records_an_event_for_every_state_change()
    {
        var session = NewSession();
        var eventsBefore = session.Events.Count;

        session.TransitionTo(LiveSessionStatus.Preparing, Now, reason: "preparing");
        session.TransitionTo(LiveSessionStatus.Ready, Now, reason: "ready");

        Assert.Equal(eventsBefore + 2, session.Events.Count);

        var transitions = session.Events.Where(e => e.ToStatus is not null).ToList();
        Assert.Contains(transitions, e => e.FromStatus == LiveSessionStatus.Draft && e.ToStatus == LiveSessionStatus.Preparing);
        Assert.Contains(transitions, e => e.FromStatus == LiveSessionStatus.Preparing && e.ToStatus == LiveSessionStatus.Ready);
    }

    [Fact]
    public void TransitionTo_the_current_state_is_a_no_op()
    {
        // Retried API calls and duplicated media callbacks must not fail or duplicate events.
        var session = NewSession();
        session.TransitionTo(LiveSessionStatus.Preparing, Now);
        var eventsAfterFirst = session.Events.Count;

        session.TransitionTo(LiveSessionStatus.Preparing, Now.AddSeconds(5));

        Assert.Equal(eventsAfterFirst, session.Events.Count);
        Assert.Equal(LiveSessionStatus.Preparing, session.Status);
    }

    [Fact]
    public void TransitionTo_rejects_an_illegal_target()
    {
        var session = NewSession();

        Assert.Throws<InvalidStateTransitionException>(() =>
            session.TransitionTo(LiveSessionStatus.Live, Now));

        Assert.Equal(LiveSessionStatus.Draft, session.Status);
    }

    [Fact]
    public void StartedAt_is_set_once_and_survives_a_reconnect()
    {
        var session = GoLive(out _);
        var firstStartedAt = session.StartedAt;

        session.TransitionTo(LiveSessionStatus.Reconnecting, Now.AddMinutes(5));
        session.TransitionTo(LiveSessionStatus.Live, Now.AddMinutes(6));

        // Session duration must measure the whole broadcast, not restart after every blip.
        Assert.Equal(firstStartedAt, session.StartedAt);
    }

    [Fact]
    public void EndedAt_is_set_when_the_session_reaches_a_terminal_state()
    {
        var session = GoLive(out var startedAt);
        session.TransitionTo(LiveSessionStatus.Stopping, startedAt.AddMinutes(10));
        session.TransitionTo(LiveSessionStatus.Ended, startedAt.AddMinutes(10));

        Assert.Equal(startedAt.AddMinutes(10), session.EndedAt);
    }

    [Fact]
    public void StateEnteredAt_tracks_the_current_state_only()
    {
        var session = NewSession();
        session.TransitionTo(LiveSessionStatus.Preparing, Now);
        session.TransitionTo(LiveSessionStatus.Ready, Now.AddSeconds(30));

        Assert.Equal(Now.AddSeconds(30), session.StateEnteredAt);
    }

    [Fact]
    public void Failure_details_are_stored_and_cleared_on_recovery()
    {
        var session = GoLive(out var startedAt);

        session.TransitionTo(LiveSessionStatus.Reconnecting, startedAt.AddMinutes(1),
            reason: "Ingest lost", errorCode: ErrorCodes.SourceDisconnected);

        Assert.Equal(ErrorCodes.SourceDisconnected, session.LastErrorCode);
        Assert.Equal("Ingest lost", session.LastErrorMessage);

        session.TransitionTo(LiveSessionStatus.Live, startedAt.AddMinutes(2), reason: "Reconnected");

        // Once the stream is healthy again the studio must stop showing a stale error.
        Assert.Null(session.LastErrorCode);
        Assert.Null(session.LastErrorMessage);
    }

    [Fact]
    public void UpdateDetails_is_refused_once_the_session_is_live()
    {
        var session = GoLive(out _);

        var exception = Assert.Throws<DomainException>(() =>
            session.UpdateDetails("New title", null, LiveSessionVisibility.Private, false, Now));

        Assert.Equal(ErrorCodes.SessionNotReady, exception.ErrorCode);
    }

    [Fact]
    public void RotateMediaPath_produces_a_different_path()
    {
        var session = NewSession();
        var original = session.MediaPathName;

        session.RotateMediaPath(Now.AddMinutes(1));

        Assert.NotEqual(original, session.MediaPathName);
    }

    private static LiveSession GoLive(out DateTimeOffset startedAt)
    {
        startedAt = Now.AddMinutes(1);
        var session = NewSession();
        session.TransitionTo(LiveSessionStatus.Preparing, Now);
        session.TransitionTo(LiveSessionStatus.Ready, Now);
        session.TransitionTo(LiveSessionStatus.Starting, Now);
        session.TransitionTo(LiveSessionStatus.Live, startedAt);
        return session;
    }
}
