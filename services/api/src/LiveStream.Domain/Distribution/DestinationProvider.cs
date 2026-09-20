namespace LiveStream.Domain.Distribution;

/// <summary>
/// External platforms a Live Session can be republished to (docs/06-multi-platform-distribution.md).
///
/// The enum is deliberately small and stable: provider-specific behaviour lives in adapters, never
/// in the session domain model. Adding a platform must not require a change to session logic.
/// </summary>
public enum DestinationProvider
{
    /// <summary>Any RTMP/RTMPS endpoint supplied by the operator. Needs no provider adapter.</summary>
    CustomRtmp = 0,

    YouTube = 1,

    Facebook = 2,

    TikTok = 3,

    Twitch = 4,
}

/// <summary>
/// How a destination obtains the RTMP target it publishes to.
///
/// The distinction matters operationally: a stream key is supplied once and stays valid, whereas a
/// linked account mints a fresh broadcast — and therefore a fresh key — for every session.
/// </summary>
public enum DestinationCredentialMode
{
    /// <summary>Operator pasted an ingest URL and stream key from the platform's own dashboard.</summary>
    StreamKey = 0,

    /// <summary>Destination is bound to an OAuth-linked <see cref="ProviderAccount"/>.</summary>
    LinkedAccount = 1,
}
