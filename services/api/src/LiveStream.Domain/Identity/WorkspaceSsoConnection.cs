using LiveStream.Domain.Common;

namespace LiveStream.Domain.Identity;

public enum SsoProtocol
{
    /// <summary>OpenID Connect authorization code flow with PKCE.</summary>
    Oidc = 0,
}

/// <summary>
/// A workspace's enterprise identity provider.
///
/// The connection is configured by a workspace admin but is inert until an operator verifies at
/// least one email domain for it: whoever owns a domain controls sign-in for every address in it,
/// so an unverified claim on <c>example.com</c> would be an account-takeover primitive rather than
/// a configuration mistake (docs/11-security.md).
/// </summary>
public class WorkspaceSsoConnection
{
    /// <summary>Longest a sign-in may sit half-finished before its state is no longer accepted.</summary>
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

    public const int MaxDomains = 20;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>One connection per workspace, enforced by a unique index.</summary>
    public Guid WorkspaceId { get; set; }

    public SsoProtocol Protocol { get; set; } = SsoProtocol.Oidc;

    /// <summary>OIDC issuer, e.g. <c>https://login.microsoftonline.com/{tenant}/v2.0</c>.</summary>
    public string Issuer { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted with the platform secret protector, exactly like a destination stream key. Never
    /// projected into an API response and never logged.
    /// </summary>
    public string ClientSecretCiphertext { get; set; } = string.Empty;

    /// <summary>Admin switch. Off means sign-in falls back to password for everyone in the domain.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether a first-time user from a verified domain gets an account created for them. When off,
    /// only people already invited to the workspace can sign in through this connection.
    /// </summary>
    public bool JitProvisioning { get; set; } = true;

    /// <summary>
    /// Role granted to a JIT-provisioned member. Owner and Admin are rejected: an identity provider
    /// must not be able to mint someone who can then reconfigure the identity provider.
    /// </summary>
    public WorkspaceRole DefaultRole { get; set; } = WorkspaceRole.Host;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public Workspace? Workspace { get; set; }

    public ICollection<WorkspaceSsoDomain> Domains { get; set; } = [];

    /// <summary>Usable only when the admin has switched it on and an operator has verified a domain.</summary>
    public bool IsUsable => Enabled && Domains.Any(d => d.VerifiedAt is not null);

    public void Configure(string? issuer, string? clientId, WorkspaceRole defaultRole, bool enabled,
        bool jitProvisioning, DateTimeOffset now)
    {
        Issuer = NormalizeIssuer(issuer);
        ClientId = (clientId ?? string.Empty).Trim();

        if (ClientId.Length is 0 or > 256)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "A client ID is required and must be 256 characters or fewer.");
        }

        if (defaultRole is WorkspaceRole.Owner or WorkspaceRole.Admin)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Single sign-on cannot grant Owner or Admin. Promote those members deliberately.");
        }

        DefaultRole = defaultRole;
        Enabled = enabled;
        JitProvisioning = jitProvisioning;
        UpdatedAt = now;
    }

    /// <summary>
    /// Normalizes an OIDC issuer. HTTPS is required because the discovery document, the token
    /// endpoint, and the signing keys are all fetched from it — over plain HTTP, anyone on the path
    /// chooses who is allowed to sign in.
    /// </summary>
    public static string NormalizeIssuer(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');

        if (trimmed.Length is 0 or > 512
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "The issuer must be an https URL with no query string, e.g. https://login.example.com.");
        }

        return trimmed;
    }
}

/// <summary>
/// An email domain claimed by an SSO connection.
///
/// Globally unique, and inert until <see cref="VerifiedAt"/> is set by an operator. Verification is
/// deliberately not self-service: a workspace that could verify its own claim on a domain it does
/// not own could sign in as anyone with an address there.
/// </summary>
public class WorkspaceSsoDomain
{
    /// <summary>
    /// Assigned by the persistence layer on insert. Deliberately not pre-populated: domains are
    /// appended to the connection's collection, and change tracking treats a child discovered
    /// with a key already set as an existing row to UPDATE rather than a new row to INSERT.
    /// </summary>
    public Guid Id { get; set; }

    public Guid ConnectionId { get; set; }

    /// <summary>Lower-cased, no leading <c>@</c>. Unique across the whole platform.</summary>
    public string Domain { get; set; } = string.Empty;

    public DateTimeOffset? VerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public WorkspaceSsoConnection? Connection { get; set; }

    public static string Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

        if (trimmed.Length is 0 or > 253
            || !trimmed.Contains('.')
            || trimmed.StartsWith('.') || trimmed.EndsWith('.') || trimmed.Contains("..")
            || !trimmed.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"'{value}' is not a valid email domain. Use the part after the @, e.g. example.com.");
        }

        return trimmed;
    }

    /// <summary>The domain part of an email address, or <c>null</c> when there is not exactly one @.</summary>
    public static string? FromEmail(string? email)
    {
        var parts = (email ?? string.Empty).Trim().ToLowerInvariant().Split('@');
        return parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null;
    }
}

/// <summary>
/// A link between a platform user and an identity at an external provider.
///
/// Sign-in matches on <see cref="Subject"/>, never on email: an email address can be reassigned
/// inside a company, and matching on it would hand the new holder the previous holder's account.
/// </summary>
public class UserIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>Issuer that asserted this identity. Part of the uniqueness key with <see cref="Subject"/>.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The provider's stable subject claim. Opaque, and not a secret.</summary>
    public string Subject { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastLoginAt { get; set; }

    public User? User { get; set; }
}
