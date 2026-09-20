using System.Text;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Ai;
using LiveStream.Domain.Distribution;
using LiveStream.Domain.Governance;
using LiveStream.Domain.Identity;
using LiveStream.Domain.Media;
using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sessions;
using LiveStream.Domain.Sources;
using LiveStream.Domain.Studio;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LiveStream.Infrastructure.Persistence;

/// <summary>
/// Transactional store for the control plane. Deliberately free of provider-specific column types
/// so the same model runs on PostgreSQL in production and SQLite in integration tests
/// (docs/decisions/0006-testing-and-database-providers.md).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();

    public DbSet<LiveSessionEvent> LiveSessionEvents => Set<LiveSessionEvent>();

    public DbSet<LiveSessionHealth> LiveSessionHealth => Set<LiveSessionHealth>();

    public DbSet<IngestCredential> IngestCredentials => Set<IngestCredential>();

    public DbSet<Recording> Recordings => Set<Recording>();

    public DbSet<StreamDestination> StreamDestinations => Set<StreamDestination>();

    public DbSet<DestinationEvent> DestinationEvents => Set<DestinationEvent>();

    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();

    public DbSet<SessionSource> SessionSources => Set<SessionSource>();

    public DbSet<SourceEvent> SourceEvents => Set<SourceEvent>();

    public DbSet<SessionBranding> SessionBranding => Set<SessionBranding>();

    public DbSet<SessionScene> SessionScenes => Set<SessionScene>();

    public DbSet<AiJob> AiJobs => Set<AiJob>();

    public DbSet<WorkspaceLimits> WorkspaceLimits => Set<WorkspaceLimits>();

    public DbSet<WorkspaceSsoConnection> SsoConnections => Set<WorkspaceSsoConnection>();

    public DbSet<WorkspaceSsoDomain> SsoDomains => Set<WorkspaceSsoDomain>();

    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();

    public DbSet<RuntimeLease> RuntimeLeases => Set<RuntimeLease>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplySnakeCaseNames(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Names columns, keys, and constraints in snake_case, matching the schema in
    /// docs/10-database-design.md and the explicit table names. Without this, EF would emit
    /// PascalCase columns inside snake_case tables — every hand-written query would then need
    /// quoted identifiers, and the schema would not match its own specification.
    /// </summary>
    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entityType.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName()));
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName()));
            }

            foreach (var index in entityType.GetIndexes())
            {
                // Indexes given an explicit name are already snake_case and pass through unchanged.
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()));
            }
        }
    }

    private static string? ToSnakeCase(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            // Insert a separator at a lower-to-upper boundary, and before the final capital of an
            // acronym that starts a new word — so "MediaPathName" and "PK_live" both behave.
            if (char.IsUpper(current) && i > 0 && name[i - 1] != '_'
                && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native date type and cannot ORDER BY a DateTimeOffset. EF's binary converter
        // stores them as sortable integers, which is the documented remedy. PostgreSQL orders
        // timestamptz natively, so the production mapping is left untouched.
        //
        // The provider is detected by name rather than with IsSqlite() to avoid taking a dependency
        // on the SQLite provider package in production code.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) is true)
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }

        base.ConfigureConventions(configurationBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        BumpConcurrencyTokens();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        BumpConcurrencyTokens();
        return base.SaveChanges();
    }

    /// <summary>
    /// Advances the optimistic concurrency token on every modified session and destination. The
    /// UPDATE then carries <c>WHERE version = @original</c>, so two writers racing each other cannot
    /// silently overwrite one another — the loser gets a concurrency conflict instead.
    ///
    /// Destinations need this as much as sessions do: the reconciler and an operator pressing stop
    /// can reach the same row at the same moment.
    /// </summary>
    private void BumpConcurrencyTokens()
    {
        foreach (var entry in ChangeTracker.Entries<LiveSession>())
        {
            if (entry.State is EntityState.Modified)
            {
                var version = entry.Property(e => e.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }

        foreach (var entry in ChangeTracker.Entries<StreamDestination>())
        {
            if (entry.State is EntityState.Modified)
            {
                var version = entry.Property(e => e.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }

        foreach (var entry in ChangeTracker.Entries<SessionSource>())
        {
            if (entry.State is EntityState.Modified)
            {
                var version = entry.Property(e => e.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }
    }
}
