using LiveStream.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveStream.Infrastructure.Persistence.Configurations;

/// <summary>
/// Persistence for AI jobs (implementation/phase-6-ai-live-operations.md).
///
/// The job row is the whole observability surface for the feature, so every field the phase's
/// "observable" criterion implies — attempts, timings, tokens, model, failure — is stored rather
/// than being left to logs.
/// </summary>
public sealed class AiJobConfiguration : IEntityTypeConfiguration<AiJob>
{
    public void Configure(EntityTypeBuilder<AiJob> builder)
    {
        builder.ToTable("ai_jobs");
        builder.HasKey(job => job.Id);

        builder.Property(job => job.Kind).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(job => job.Summary).HasMaxLength(500);
        builder.Property(job => job.ErrorCode).HasMaxLength(64);
        builder.Property(job => job.ErrorMessage).HasMaxLength(1000);
        builder.Property(job => job.ModelId).HasMaxLength(64);

        builder.Property(job => job.Version).IsConcurrencyToken();

        builder.HasOne(job => job.LiveSession)
            .WithMany()
            .HasForeignKey(job => job.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // The worker's only query: queued jobs whose backoff has elapsed, oldest first.
        builder.HasIndex(job => new { job.Status, job.NextAttemptAt });

        // The control room's only query: this session's jobs, newest first.
        builder.HasIndex(job => new { job.LiveSessionId, job.RequestedAt });
    }
}
