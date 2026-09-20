using System.ComponentModel.DataAnnotations;

namespace LiveStream.Application.Common;

/// <summary>
/// Where the browser comes back to after an identity provider has authenticated someone.
/// Bound from the <c>Sso</c> section.
/// </summary>
public sealed class SsoOptions
{
    public const string SectionName = "Sso";

    /// <summary>
    /// Origin of the web app, e.g. <c>https://studio.example.com</c>. The callback lands there, not
    /// on this API: tokens are returned in a response body, and putting them in a redirect URL
    /// would write them into browser history and every proxy log on the way.
    /// </summary>
    [Required]
    public string WebAppBaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>
    /// Path the provider redirects to. Combined with <see cref="WebAppBaseUrl"/> it forms the
    /// redirect URI, which must be registered identically at the provider.
    /// </summary>
    [Required]
    public string CallbackPath { get; set; } = "/sign-in/sso/callback";

    /// <summary>How long a half-finished sign-in stays valid.</summary>
    [Range(60, 1800)]
    public int LoginTimeoutSeconds { get; set; } = 600;

    public TimeSpan LoginTimeout => TimeSpan.FromSeconds(LoginTimeoutSeconds);

    /// <summary>The single redirect URI this deployment uses, for every workspace.</summary>
    public string RedirectUri =>
        $"{WebAppBaseUrl.TrimEnd('/')}/{CallbackPath.TrimStart('/')}";
}
