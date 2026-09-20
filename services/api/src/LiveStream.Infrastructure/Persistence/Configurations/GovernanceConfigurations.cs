using LiveStream.Domain.Governance;
using LiveStream.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveStream.Infrastructure.Persistence.Configurations;

/// <summary>
/// Persistence for tenant governance (implementation/phase-7-scale-security-and-globalization.md).
/// </summary>
public sealed class WorkspaceLimitsConfiguration : IEntityTypeConfiguration<WorkspaceLimits>
{
    public void Configure(EntityTypeBuilder<WorkspaceLimits> builder)
    {
        builder.ToTable("workspace_limits");

        // The workspace id is the key: two limit rows for one workspace would make "what is this
        // tenant allowed" a question with two answers.
        builder.HasKey(limits => limits.WorkspaceId);

        builder.Property(limits => limits.Plan).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(limits => limits.ResidencyRegion).HasMaxLength(32);

        builder.HasOne(limits => limits.Workspace)
            .WithOne()
            .HasForeignKey<WorkspaceLimits>(limits => limits.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// The leader lease table. Tiny, hot, and read by every background loop on every tick, so it is
/// keyed by the lease name and holds nothing else.
/// </summary>
public sealed class RuntimeLeaseConfiguration : IEntityTypeConfiguration<RuntimeLease>
{
    public void Configure(EntityTypeBuilder<RuntimeLease> builder)
    {
        builder.ToTable("runtime_leases");
        builder.HasKey(lease => lease.Name);

        builder.Property(lease => lease.Name).HasMaxLength(64).IsRequired();
        builder.Property(lease => lease.OwnerId).HasMaxLength(128).IsRequired();
        builder.Property(lease => lease.Version).IsConcurrencyToken();
    }
}

public sealed class WorkspaceSsoConnectionConfiguration : IEntityTypeConfiguration<WorkspaceSsoConnection>
{
    public void Configure(EntityTypeBuilder<WorkspaceSsoConnection> builder)
    {
        builder.ToTable("workspace_sso_connections");
        builder.HasKey(connection => connection.Id);

        builder.Property(connection => connection.Protocol).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(connection => connection.Issuer).HasMaxLength(512).IsRequired();
        builder.Property(connection => connection.ClientId).HasMaxLength(256).IsRequired();
        builder.Property(connection => connection.ClientSecretCiphertext).HasMaxLength(4096).IsRequired();
        builder.Property(connection => connection.DefaultRole).HasConversion<string>().HasMaxLength(24).IsRequired();

        builder.HasOne(connection => connection.Workspace)
            .WithOne()
            .HasForeignKey<WorkspaceSsoConnection>(connection => connection.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(connection => connection.WorkspaceId).IsUnique();
    }
}

public sealed class WorkspaceSsoDomainConfiguration : IEntityTypeConfiguration<WorkspaceSsoDomain>
{
    public void Configure(EntityTypeBuilder<WorkspaceSsoDomain> builder)
    {
        builder.ToTable("workspace_sso_domains");
        builder.HasKey(domain => domain.Id);

        builder.Property(domain => domain.Domain).HasMaxLength(253).IsRequired();

        builder.HasOne(domain => domain.Connection)
            .WithMany(connection => connection.Domains)
            .HasForeignKey(domain => domain.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Globally unique, and that is a security control rather than a tidiness one: whoever holds
        // a domain decides who may sign in with an address in it, so it cannot be held twice.
        builder.HasIndex(domain => domain.Domain).IsUnique();
    }
}

public sealed class UserIdentityConfiguration : IEntityTypeConfiguration<UserIdentity>
{
    public void Configure(EntityTypeBuilder<UserIdentity> builder)
    {
        builder.ToTable("user_identities");
        builder.HasKey(identity => identity.Id);

        builder.Property(identity => identity.Issuer).HasMaxLength(512).IsRequired();
        builder.Property(identity => identity.Subject).HasMaxLength(256).IsRequired();

        builder.HasOne(identity => identity.User)
            .WithMany()
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One platform account per provider subject. Without this, a second row could silently
        // point the same external identity at a different user.
        builder.HasIndex(identity => new { identity.Issuer, identity.Subject }).IsUnique();
    }
}
