using LiveStream.Domain.Media;
using LiveStream.Domain.Recordings;
using LiveStream.Domain.Sessions;
using LiveStream.Domain.Studio;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveStream.Infrastructure.Persistence.Configurations;

public sealed class LiveSessionConfiguration : IEntityTypeConfiguration<LiveSession>
{
    public void Configure(EntityTypeBuilder<LiveSession> builder)
    {
        builder.ToTable("live_sessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Title).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(2000);
        builder.Property(s => s.MediaPathName).HasMaxLength(64).IsRequired();
        builder.Property(s => s.LastErrorCode).HasMaxLength(64);
        builder.Property(s => s.LastErrorMessage).HasMaxLength(500);

        // Enums are persisted as text: readable in the database and stable if numbering ever changes.
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Visibility).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(s => s.Version).IsConcurrencyToken();

        builder.HasOne(s => s.Health)
            .WithOne(h => h!.LiveSession)
            .HasForeignKey<LiveSessionHealth>(h => h.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Events)
            .WithOne(e => e.LiveSession!)
            .HasForeignKey(e => e.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Recordings)
            .WithOne(r => r.LiveSession!)
            .HasForeignKey(r => r.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.IngestCredentials)
            .WithOne(c => c.LiveSession!)
            .HasForeignKey(c => c.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Branding)
            .WithOne(b => b!.LiveSession)
            .HasForeignKey<SessionBranding>(b => b.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Scenes)
            .WithOne(s => s.LiveSession!)
            .HasForeignKey(s => s.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Sources)
            .WithOne(s => s.LiveSession!)
            .HasForeignKey(s => s.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // The aggregate exposes read-only collections, so EF reads and writes the backing fields.
        builder.Navigation(s => s.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.Recordings).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.IngestCredentials).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.Sources).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.Scenes).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.Branding).IsRequired();
        builder.Navigation(s => s.Health).IsRequired();

        // Dashboard listing: sessions for a workspace, newest first, optionally filtered by status.
        builder.HasIndex(s => new { s.WorkspaceId, s.Status, s.CreatedAt })
            .HasDatabaseName("ix_live_sessions_workspace_status_created");

        // The media gateway resolves auth callbacks by path, on every publish attempt.
        builder.HasIndex(s => s.MediaPathName).IsUnique().HasDatabaseName("ix_live_sessions_media_path");

        // The health monitor scans by status every poll interval.
        builder.HasIndex(s => s.Status).HasDatabaseName("ix_live_sessions_status");
    }
}

public sealed class LiveSessionHealthConfiguration : IEntityTypeConfiguration<LiveSessionHealth>
{
    public void Configure(EntityTypeBuilder<LiveSessionHealth> builder)
    {
        builder.ToTable("live_session_health");
        builder.HasKey(h => h.LiveSessionId);

        builder.Property(h => h.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(h => h.LastErrorCode).HasMaxLength(64);
    }
}

public sealed class LiveSessionEventConfiguration : IEntityTypeConfiguration<LiveSessionEvent>
{
    public void Configure(EntityTypeBuilder<LiveSessionEvent> builder)
    {
        builder.ToTable("live_session_events");
        builder.HasKey(e => e.Id);

        // Generated on insert. Events reach the context through the aggregate's collection, and a
        // child discovered with its key already populated is tracked as Modified (an UPDATE against
        // a row that does not exist) instead of Added.
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.Property(e => e.Detail).HasMaxLength(1000);
        builder.Property(e => e.CorrelationId).HasMaxLength(64);

        builder.HasIndex(e => new { e.LiveSessionId, e.CreatedAt })
            .HasDatabaseName("ix_live_session_events_session_created");
    }
}

public sealed class IngestCredentialConfiguration : IEntityTypeConfiguration<IngestCredential>
{
    public void Configure(EntityTypeBuilder<IngestCredential> builder)
    {
        builder.ToTable("ingest_credentials");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.MediaPathName).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();

        // Every media publish attempt looks a credential up by hash; this must be an index seek.
        builder.HasIndex(c => c.TokenHash).IsUnique().HasDatabaseName("ix_ingest_credentials_token_hash");
        builder.HasIndex(c => new { c.LiveSessionId, c.ExpiresAt })
            .HasDatabaseName("ix_ingest_credentials_session_expires");
    }
}

public sealed class RecordingConfiguration : IEntityTypeConfiguration<Recording>
{
    public void Configure(EntityTypeBuilder<Recording> builder)
    {
        builder.ToTable("recordings");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(r => r.MediaFormat).HasMaxLength(32).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.FailureReason).HasMaxLength(500);

        builder.HasIndex(r => new { r.LiveSessionId, r.Status })
            .HasDatabaseName("ix_recordings_session_status");

        // The retention sweep's only query: recordings that are Ready and past their expiry.
        // Without this it is a table scan every hour, over the table that grows fastest.
        builder.HasIndex(r => new { r.Status, r.ExpiresAt })
            .HasDatabaseName("ix_recordings_status_expires_at");
    }
}
