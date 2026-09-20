using LiveStream.Domain.Studio;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveStream.Infrastructure.Persistence.Configurations;

/// <summary>
/// Persistence for the studio's look: branding and prepared scenes
/// (docs/07-live-studio.md, implementation/phase-5-professional-live-studio.md).
/// </summary>
public sealed class SessionBrandingConfiguration : IEntityTypeConfiguration<SessionBranding>
{
    public void Configure(EntityTypeBuilder<SessionBranding> builder)
    {
        builder.ToTable("session_branding");
        builder.HasKey(b => b.LiveSessionId);

        // Held inline rather than in object storage, deliberately: a same-origin image is the only
        // kind that can be drawn into the composition canvas without tainting it, and a tainted
        // canvas cannot be captured at all. See SessionBranding for the full reasoning.
        builder.Property(b => b.LogoDataUri).HasMaxLength(SessionBranding.MaxLogoBytes);
        builder.Property(b => b.LogoPosition).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(b => b.AccentColor).HasMaxLength(7).IsRequired();
    }
}

public sealed class SessionSceneConfiguration : IEntityTypeConfiguration<SessionScene>
{
    public void Configure(EntityTypeBuilder<SessionScene> builder)
    {
        builder.ToTable("session_scenes");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(SessionScene.MaxNameLength).IsRequired();
        builder.Property(s => s.Layout).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(s => s.LowerThirdTitle).HasMaxLength(SessionScene.MaxCaptionLength);
        builder.Property(s => s.LowerThirdSubtitle).HasMaxLength(SessionScene.MaxCaptionLength);

        // Source ids are stored without a foreign key on purpose: a scene has to outlive the camera
        // it names, so that recalling it can say the camera has gone rather than disappearing with
        // it. `LiveSession.ForgetSourceInScenes` clears them when a source is revoked.
        builder.HasIndex(s => new { s.LiveSessionId, s.Position });
    }
}
