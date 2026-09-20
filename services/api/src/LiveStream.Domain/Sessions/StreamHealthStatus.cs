namespace LiveStream.Domain.Sessions;

/// <summary>Coarse, user-facing stream quality classification.</summary>
public enum StreamHealthStatus
{
    Unknown = 0,
    Good = 1,
    Fair = 2,
    Poor = 3,
}
