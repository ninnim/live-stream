using System.ComponentModel.DataAnnotations;

namespace LiveStream.Application.Common;

/// <summary>
/// Tunables for multi-device contribution. Bound from the <c>Sources</c> configuration section and
/// validated on startup.
/// </summary>
public sealed class SourceOptions
{
    public const string SectionName = "Sources";

    /// <summary>
    /// How long a pairing code stays redeemable.
    ///
    /// Short by design. A code is only eight characters so it can be typed on a phone, and a short
    /// window plus single use plus rate limiting — not length — is what makes guessing impractical
    /// (docs/05-multi-device.md: "Pairing codes expire").
    /// </summary>
    [Range(30, 3600)]
    public int PairingCodeLifetimeSeconds { get; set; } = 600;

    /// <summary>
    /// How long a paired device stays authenticated before it must pair again.
    ///
    /// Long enough to cover a full broadcast without interrupting a camera operator mid-show, short
    /// enough that a forgotten phone stops being a way in by the next day.
    /// </summary>
    [Range(300, 172800)]
    public int DeviceTokenLifetimeSeconds { get; set; } = 43200;

    /// <summary>Maximum sources per session, including the studio. An abuse and resource control.</summary>
    [Range(1, 50)]
    public int MaxSourcesPerSession { get; set; } = 8;

    /// <summary>
    /// Lifetime of the read credential issued so the control room can preview a source. Very short:
    /// it is requested per preview and is the only thing standing between a private session's
    /// second camera and anyone who learns its path.
    /// </summary>
    [Range(30, 3600)]
    public int PreviewCredentialLifetimeSeconds { get; set; } = 300;

    /// <summary>
    /// How long a paired-but-silent source is shown as connected after media stops.
    /// Matches the session recovery window in spirit: a phone in a tunnel has not left the show.
    /// </summary>
    [Range(5, 600)]
    public int SourcePresenceGraceSeconds { get; set; } = 20;

    public TimeSpan PairingCodeLifetime => TimeSpan.FromSeconds(PairingCodeLifetimeSeconds);

    public TimeSpan DeviceTokenLifetime => TimeSpan.FromSeconds(DeviceTokenLifetimeSeconds);

    public TimeSpan PreviewCredentialLifetime => TimeSpan.FromSeconds(PreviewCredentialLifetimeSeconds);

    public TimeSpan SourcePresenceGrace => TimeSpan.FromSeconds(SourcePresenceGraceSeconds);
}
