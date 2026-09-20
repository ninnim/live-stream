namespace LiveStream.Domain.Identity;

public enum UserStatus
{
    Active = 0,
    Suspended = 1,
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stored lower-cased; uniqueness is enforced on this value.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Output of the configured password hasher. Never logged or returned by the API.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<WorkspaceMember> Memberships { get; set; } = [];
}
