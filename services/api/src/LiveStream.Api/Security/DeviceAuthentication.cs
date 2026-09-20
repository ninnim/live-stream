using System.Security.Claims;
using System.Text.Encodings.Web;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Sources;
using LiveStream.Domain.Sources;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.Security;

/// <summary>
/// Authenticates a paired device by its source-scoped token.
///
/// Deliberately not JWT. A device token must die the instant an operator revokes it
/// (docs/05-multi-device.md: "Revocation is immediate"), and a self-contained bearer token cannot
/// do that without a denylist — it stays valid until it expires. This scheme resolves the token
/// against the database on every request, so revocation takes effect on the very next one.
///
/// The cost is a lookup per request. It is a single indexed seek on a hash, and correctness here is
/// worth more than saving it.
/// </summary>
public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    SourceService sources)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Device";

    /// <summary>Authorization scheme the device presents, e.g. <c>Authorization: Device abc123</c>.</summary>
    public const string HeaderScheme = "Device";

    public const string SourceIdClaim = "source_id";
    public const string SessionIdClaim = "session_id";
    public const string SourceRoleClaim = "source_role";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();

        // No result rather than a failure: this scheme runs alongside JWT, and a user's Bearer
        // token reaching here is not an error, it simply is not a device.
        if (string.IsNullOrWhiteSpace(header)
            || !header.StartsWith($"{HeaderScheme} ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[(HeaderScheme.Length + 1)..].Trim();
        var source = await sources.ResolveDeviceAsync(token, Context.RequestAborted);

        if (source is null)
        {
            // One message for expired, unknown and revoked alike: telling them apart would confirm
            // which tokens were ever real.
            return AuthenticateResult.Fail("The device token is not valid.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(SourceIdClaim, source.Id.ToString()),
            new Claim(SessionIdClaim, source.LiveSessionId.ToString()),
            new Claim(SourceRoleClaim, source.Role.ToString()),
            new Claim(ClaimTypes.NameIdentifier, source.Id.ToString()),
            new Claim(ClaimTypes.Name, source.DisplayName),
        ], SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

/// <summary>Reads the authenticated device out of the current request.</summary>
public sealed class DeviceContext(IHttpContextAccessor accessor) : IDeviceContext
{
    public Guid? SourceId => ReadGuid(DeviceAuthenticationHandler.SourceIdClaim);

    public Guid? LiveSessionId => ReadGuid(DeviceAuthenticationHandler.SessionIdClaim);

    public SourceRole? Role =>
        Enum.TryParse<SourceRole>(ReadClaim(DeviceAuthenticationHandler.SourceRoleClaim), out var role)
            ? role
            : null;

    private string? ReadClaim(string type) =>
        accessor.HttpContext?.User.FindFirst(type)?.Value;

    private Guid? ReadGuid(string type) =>
        Guid.TryParse(ReadClaim(type), out var value) ? value : null;
}

public static class DeviceAuthorizationPolicies
{
    /// <summary>
    /// Any paired device, whatever its role. Role-specific capability checks happen in the
    /// application layer against <see cref="SourcePermissions"/>, so a policy per role would only
    /// duplicate that rule in a second place where it could drift.
    /// </summary>
    public const string PairedDevice = "paired-device";
}
