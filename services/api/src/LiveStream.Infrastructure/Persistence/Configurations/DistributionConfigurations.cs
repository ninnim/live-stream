using LiveStream.Domain.Distribution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveStream.Infrastructure.Persistence.Configurations;

public sealed class StreamDestinationConfiguration : IEntityTypeConfiguration<StreamDestination>
{
    public void Configure(EntityTypeBuilder<StreamDestination> builder)
    {
        builder.ToTable("stream_destinations");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.DisplayName).HasMaxLength(120).IsRequired();
        builder.Property(d => d.IngestUrl).HasMaxLength(1000);
        builder.Property(d => d.ResolvedIngestUrl).HasMaxLength(1000);
        builder.Property(d => d.ExternalBroadcastId).HasMaxLength(200);
        builder.Property(d => d.WatchUrl).HasMaxLength(1000);
        builder.Property(d => d.LastErrorCode).HasMaxLength(64);
        builder.Property(d => d.LastErrorMessage).HasMaxLength(500);

        // Ciphertext is longer than the secret it wraps: base64 of nonce + payload + tag, plus the
        // version and key id prefix. Sized for a generous stream key rather than a typical one.
        builder.Property(d => d.StreamKeyCipher).HasMaxLength(2048);
        builder.Property(d => d.ResolvedStreamKeyCipher).HasMaxLength(2048);

        builder.Property(d => d.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(d => d.CredentialMode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(d => d.Version).IsConcurrencyToken();

        builder.HasMany(d => d.Events)
            .WithOne(e => e.StreamDestination!)
            .HasForeignKey(e => e.StreamDestinationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(d => d.Events).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne(d => d.LiveSession)
            .WithMany()
            .HasForeignKey(d => d.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not Cascade: disconnecting an account must not silently delete the destinations
        // configured against it. The service refuses the disconnect or the operator removes them.
        builder.HasOne(d => d.ProviderAccount)
            .WithMany(a => a!.Destinations)
            .HasForeignKey(d => d.ProviderAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(d => d.ProviderAccount).UsePropertyAccessMode(PropertyAccessMode.Property);

        // Studio lists a session's destinations on every poll.
        builder.HasIndex(d => new { d.LiveSessionId, d.CreatedAt })
            .HasDatabaseName("ix_stream_destinations_session_created");

        // The orchestrator scans by status every reconcile tick.
        builder.HasIndex(d => d.Status).HasDatabaseName("ix_stream_destinations_status");
    }
}

public sealed class DestinationEventConfiguration : IEntityTypeConfiguration<DestinationEvent>
{
    public void Configure(EntityTypeBuilder<DestinationEvent> builder)
    {
        builder.ToTable("destination_events");
        builder.HasKey(e => e.Id);

        // Generated on insert, for the same reason as live_session_events: an appended child
        // discovered with its key already set is tracked as Modified, producing an UPDATE against a
        // row that does not exist.
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.Property(e => e.Detail).HasMaxLength(1000);
        builder.Property(e => e.CorrelationId).HasMaxLength(64);

        builder.HasIndex(e => new { e.StreamDestinationId, e.CreatedAt })
            .HasDatabaseName("ix_destination_events_destination_created");
    }
}

public sealed class ProviderAccountConfiguration : IEntityTypeConfiguration<ProviderAccount>
{
    public void Configure(EntityTypeBuilder<ProviderAccount> builder)
    {
        builder.ToTable("provider_accounts");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ExternalAccountId).HasMaxLength(200).IsRequired();
        builder.Property(a => a.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Scopes).HasMaxLength(1000).IsRequired();
        builder.Property(a => a.LastErrorCode).HasMaxLength(64);
        builder.Property(a => a.LastErrorMessage).HasMaxLength(500);

        // Refresh tokens are the longest secrets stored anywhere in the system.
        builder.Property(a => a.AccessTokenCipher).HasMaxLength(8192).IsRequired();
        builder.Property(a => a.RefreshTokenCipher).HasMaxLength(8192);

        builder.Property(a => a.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Navigation(a => a.Destinations).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Re-linking the same channel updates the existing row rather than creating a duplicate.
        builder.HasIndex(a => new { a.WorkspaceId, a.Provider, a.ExternalAccountId })
            .IsUnique()
            .HasDatabaseName("ix_provider_accounts_workspace_provider_external");
    }
}
