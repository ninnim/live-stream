namespace LiveStream.Application.Abstractions;

/// <summary>One message. Both bodies are always provided — see <see cref="IEmailSender"/>.</summary>
public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string TextBody,
    string HtmlBody);

/// <summary>
/// Outbound email.
///
/// Behind an interface for the usual reason — SMTP today, a transactional provider later — and for
/// one that matters more here: the application layer must be able to *fail to send* without that
/// changing what it did. A password reset that wrote its token and then could not post the email
/// has still consumed a rate-limit slot and still invalidated the previous token, and the caller
/// has to decide what to tell the user. Making delivery a dependency rather than a side effect is
/// what keeps that decision in one place.
///
/// Implementations must never throw for a delivery failure they cannot control. They report it, so
/// a mail server being down is a logged failure rather than a 500 on a sign-up.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends one message. Returns whether it was accepted for delivery — not whether it arrived,
    /// which nothing this side of the internet can know.
    /// </summary>
    Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
