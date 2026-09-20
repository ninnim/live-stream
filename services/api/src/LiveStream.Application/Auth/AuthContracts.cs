namespace LiveStream.Application.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName, string? WorkspaceName);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record AuthResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    CurrentUserResponse User);

public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<WorkspaceMembershipResponse> Workspaces);

public sealed record WorkspaceMembershipResponse(Guid WorkspaceId, string Name, string Role, bool IsOwner);

/// <summary>Issues signed access tokens. Implemented over JWT in the infrastructure layer.</summary>
public interface IAccessTokenIssuer
{
    (string Token, int ExpiresInSeconds) Issue(Guid userId, string email);
}

/// <summary>Password hashing boundary, so the algorithm can be upgraded without touching auth logic.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>Verifies a password; <c>NeedsRehash</c> signals the stored hash uses outdated parameters.</summary>
    (bool Verified, bool NeedsRehash) Verify(string hash, string password);
}
