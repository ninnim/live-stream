namespace LiveStream.Domain.Identity;

/// <summary>
/// Tenant boundary. Every Live Session belongs to exactly one workspace, and access is decided by
/// workspace membership (docs/11-security.md "Session isolation").
/// </summary>
public class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<WorkspaceMember> Members { get; set; } = [];
}
