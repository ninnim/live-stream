using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using LiveStream.Domain.Common;

namespace LiveStream.Api.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Reads the authenticated user id, or <c>null</c> for anonymous callers.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);

        return Guid.TryParse(value, out var userId) ? userId : null;
    }

    /// <summary>
    /// Reads the authenticated user id or throws. Used by endpoints that require authentication,
    /// so a misconfigured pipeline fails loudly instead of treating the request as anonymous.
    /// </summary>
    public static Guid RequireUserId(this ClaimsPrincipal? principal) =>
        principal.GetUserId()
        ?? throw new DomainException(ErrorCodes.AuthenticationFailed, "You need to sign in to continue.");
}
