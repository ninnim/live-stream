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

namespace LiveStream.Application.Abstractions;

/// <summary>
/// Persistence surface used by application services. EF Core is treated as the persistence
/// abstraction directly rather than wrapping it in repositories, which would add indirection
/// without adding a real integration boundary (ai/coding-rules.md rule 13).
/// </summary>
public interface IAppDbContext
{
    DbSet<User> Users { get; }

    DbSet<Workspace> Workspaces { get; }

    DbSet<WorkspaceMember> WorkspaceMembers { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<PasswordResetToken> PasswordResetTokens { get; }

    DbSet<LiveSession> LiveSessions { get; }

    DbSet<LiveSessionEvent> LiveSessionEvents { get; }

    DbSet<LiveSessionHealth> LiveSessionHealth { get; }

    DbSet<IngestCredential> IngestCredentials { get; }

    DbSet<Recording> Recordings { get; }

    DbSet<StreamDestination> StreamDestinations { get; }

    DbSet<DestinationEvent> DestinationEvents { get; }

    DbSet<ProviderAccount> ProviderAccounts { get; }

    DbSet<SessionSource> SessionSources { get; }

    DbSet<SourceEvent> SourceEvents { get; }

    DbSet<SessionBranding> SessionBranding { get; }

    DbSet<SessionScene> SessionScenes { get; }

    DbSet<AiJob> AiJobs { get; }

    DbSet<WorkspaceLimits> WorkspaceLimits { get; }

    DbSet<WorkspaceSsoConnection> SsoConnections { get; }

    DbSet<WorkspaceSsoDomain> SsoDomains { get; }

    DbSet<UserIdentity> UserIdentities { get; }

    DbSet<RuntimeLease> RuntimeLeases { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
