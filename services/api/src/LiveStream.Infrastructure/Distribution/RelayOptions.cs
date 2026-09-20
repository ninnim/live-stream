using System.ComponentModel.DataAnnotations;

namespace LiveStream.Infrastructure.Distribution;

/// <summary>
/// Address of the egress relay service, bound from <c>Distribution:Relay</c>.
///
/// Private, like the media gateway control API: the relay must never be reachable from the public
/// internet, because a request to it carries a stream key.
/// </summary>
public sealed class RelayOptions
{
    public const string SectionName = "Distribution:Relay";

    /// <summary>Internal base URL, e.g. <c>http://relay:8080</c>.</summary>
    [Required]
    public string BaseUrl { get; set; } = "http://relay:8080";

    /// <summary>
    /// Shared secret presented on every relay call. Distinct from the media gateway secret on
    /// purpose: the two services have different blast radii, and reusing one secret means a
    /// compromise of either grants the other.
    /// </summary>
    [Required]
    [MinLength(16)]
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>
    /// Kept short so a stalled relay cannot hold a request thread while a session is starting.
    /// Starting a relay is a process spawn, not a network round trip to a platform.
    /// </summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 10;
}
