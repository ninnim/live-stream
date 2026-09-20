using LiveStream.Domain.Sources;

namespace LiveStream.Application.Abstractions;

/// <summary>
/// The paired device making the current request, when there is one.
///
/// Deliberately distinct from the authenticated workspace user. A device is not a user: it holds a
/// short-lived, source-scoped token rather than an account, and its capabilities come from the role
/// the operator assigned to it (<see cref="SourcePermissions"/>) rather than from workspace
/// membership. Conflating the two is how a borrowed phone ends up with a producer's rights.
/// </summary>
public interface IDeviceContext
{
    /// <summary>The source this device is bound to, or <c>null</c> when the caller is not a device.</summary>
    Guid? SourceId { get; }

    Guid? LiveSessionId { get; }

    SourceRole? Role { get; }

    bool IsDevice => SourceId is not null;

    bool Allows(SourcePermission permission) => Role is { } role && SourcePermissions.Allows(role, permission);
}
