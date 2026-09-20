using System.ComponentModel.DataAnnotations;

namespace LiveStream.Application.Abstractions;

/// <summary>
/// ICE servers handed to broadcasters so their media can traverse NAT.
///
/// Served by the API rather than compiled into the browser bundle, for two reasons: infrastructure
/// addresses are not the UI's business (ai/architecture-rules.md), and TURN credentials are secrets
/// that must be issued per session alongside the ingest credential rather than shipped to everyone.
/// </summary>
public sealed class IceOptions
{
    public const string SectionName = "Media:Ice";

    public List<IceServerOptions> Servers { get; set; } = [];
}

public sealed class IceServerOptions
{
    /// <summary>One or more URLs for the same server, e.g. <c>stun:…</c> or <c>turn:…</c>.</summary>
    [Required]
    [MinLength(1)]
    public List<string> Urls { get; set; } = [];

    /// <summary>TURN username. Omitted for STUN.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// TURN credential. A secret: it is only ever returned alongside an authorized, rate-limited
    /// ingest credential, and never logged.
    /// </summary>
    public string? Credential { get; set; }
}
