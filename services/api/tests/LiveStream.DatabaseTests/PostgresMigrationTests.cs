using LiveStream.Domain.Media;
using LiveStream.Domain.Sessions;
using LiveStream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace LiveStream.DatabaseTests;

/// <summary>
/// Verifies the production schema against a real PostgreSQL instance by applying the committed EF
/// migrations — the one thing the SQLite-backed suites cannot prove
/// (docs/decisions/0006-testing-and-database-providers.md).
/// </summary>
public sealed class PostgresMigrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("livestream")
            .WithUsername("livestream")
            .WithPassword("livestream")
            .Build();

        await _postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [RequiresDockerFact]
    public async Task Migrations_apply_cleanly_to_an_empty_database()
    {
        await using var context = CreateContext();

        await context.Database.MigrateAsync();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.NotEmpty(applied);

        var pending = await context.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);
    }

    [RequiresDockerFact]
    public async Task Migrations_are_idempotent_when_applied_twice()
    {
        await using var first = CreateContext();
        await first.Database.MigrateAsync();

        await using var second = CreateContext();
        await second.Database.MigrateAsync();

        Assert.Empty(await second.Database.GetPendingMigrationsAsync());
    }

    [RequiresDockerFact]
    public async Task The_migrated_schema_contains_every_phase_1_table()
    {
        // Model-vs-migration drift is checked in CI with
        // `dotnet ef migrations has-pending-model-changes`, which is the supported tool for it.
        // This test verifies the applied schema itself.
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var expectedTables = new[]
        {
            "users", "workspaces", "workspace_members", "refresh_tokens",
            "live_sessions", "live_session_health", "live_session_events",
            "ingest_credentials", "recordings",
        };

        var actualTables = await context.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();

        foreach (var table in expectedTables)
        {
            Assert.Contains(table, actualTables);
        }
    }

    [RequiresDockerFact]
    public async Task A_full_session_round_trips_through_the_real_schema()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var session = LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), "Postgres round trip", "Description",
            LiveSessionVisibility.Public, recordingEnabled: true, DateTimeOffset.UtcNow);

        session.TransitionTo(LiveSessionStatus.Preparing, DateTimeOffset.UtcNow);
        session.TransitionTo(LiveSessionStatus.Ready, DateTimeOffset.UtcNow);

        var (credential, _) = IngestCredential.Issue(session.Id, session.CreatedByUserId, session.MediaPathName,
            IngestCredentialScope.Publish, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        session.AddIngestCredential(credential);

        context.LiveSessions.Add(session);
        await context.SaveChangesAsync();

        await using var verify = CreateContext();
        var reloaded = await verify.LiveSessions
            .Include(s => s.Health)
            .Include(s => s.Events)
            .Include(s => s.IngestCredentials)
            .SingleAsync(s => s.Id == session.Id);

        Assert.Equal(LiveSessionStatus.Ready, reloaded.Status);
        Assert.Equal("Postgres round trip", reloaded.Title);
        Assert.NotNull(reloaded.Health);
        Assert.NotEmpty(reloaded.Events);
        Assert.Single(reloaded.IngestCredentials);
    }

    [RequiresDockerFact]
    public async Task The_unique_media_path_index_is_enforced_by_postgres()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var first = LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), "First", null,
            LiveSessionVisibility.Private, false, DateTimeOffset.UtcNow);
        context.LiveSessions.Add(first);
        await context.SaveChangesAsync();

        var duplicate = LiveSession.Create(Guid.NewGuid(), Guid.NewGuid(), "Duplicate", null,
            LiveSessionVisibility.Private, false, DateTimeOffset.UtcNow);
        typeof(LiveSession).GetProperty(nameof(LiveSession.MediaPathName))!
            .SetValue(duplicate, first.MediaPathName);

        context.LiveSessions.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private AppDbContext CreateContext()
    {
        var connectionString = _postgres?.GetConnectionString()
                               ?? throw new InvalidOperationException("PostgreSQL container is not running.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .Options;

        return new AppDbContext(options);
    }
}
