namespace LiveStream.Domain.Common;

/// <summary>
/// Raised when a domain invariant is violated. Carries a stable <see cref="ErrorCodes"/> value
/// plus a user-safe message; never carries secrets.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>An attempted Live Session state change that the state machine forbids.</summary>
public sealed class InvalidStateTransitionException : DomainException
{
    public InvalidStateTransitionException(string from, string to)
        : base(ErrorCodes.InvalidStateTransition, $"Cannot move a live session from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public string From { get; }

    public string To { get; }
}
