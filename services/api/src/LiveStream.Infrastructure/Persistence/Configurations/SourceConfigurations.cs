using LiveStream.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveStream.Infrastructure.Persistence.Configurations;

public sealed class SessionSourceConfiguration : IEntityTypeConfiguration<SessionSource>
{
    public void Configure(EntityTypeBuilder<SessionSource> builder)
    {
        builder.ToTable("session_sources");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.DisplayName).HasMaxLength(80).IsRequired();
        builder.Property(s => s.MediaPathName).HasMaxLength(64).IsRequired();

        // Both secrets are SHA-256 hex: fixed width, and the plaintext is never stored.
        builder.Property(s => s.PairingCodeHash).HasMaxLength(64);
        builder.Property(s => s.DeviceTokenHash).HasMaxLength(64);

        builder.Property(s => s.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(s => s.Version).IsConcurrencyToken();

        builder.HasMany(s => s.Events)
            .WithOne(e => e.SessionSource!)
            .HasForeignKey(e => e.SessionSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Events).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Every claim and every device request looks a source up by one of these hashes, so both
        // must be index seeks rather than scans. Unique because a collision would mean one secret
        // authenticating two devices.
        builder.HasIndex(s => s.PairingCodeHash)
            .IsUnique()
            .HasDatabaseName("ix_session_sources_pairing_code_hash");

        builder.HasIndex(s => s.DeviceTokenHash)
            .IsUnique()
            .HasDatabaseName("ix_session_sources_device_token_hash");

        builder.HasIndex(s => new { s.LiveSessionId, s.CreatedAt })
            .HasDatabaseName("ix_session_sources_session_created");

        // The presence reconciler resolves a source from a gateway path on every poll.
        builder.HasIndex(s => s.MediaPathName).HasDatabaseName("ix_session_sources_media_path");

        builder.HasIndex(s => s.Status).HasDatabaseName("ix_session_sources_status");
    }
}

public sealed class SourceEventConfiguration : IEntityTypeConfiguration<SourceEvent>
{
    public void Configure(EntityTypeBuilder<SourceEvent> builder)
    {
        builder.ToTable("source_events");
        builder.HasKey(e => e.Id);

        // Generated on insert, for the same reason as the other event tables: a child appended to
        // the aggregate with its key already set is tracked as Modified, producing an UPDATE
        // against a row that does not exist.
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.Property(e => e.Detail).HasMaxLength(1000);
        builder.Property(e => e.CorrelationId).HasMaxLength(64);

        builder.HasIndex(e => new { e.SessionSourceId, e.CreatedAt })
            .HasDatabaseName("ix_source_events_source_created");
    }
}
