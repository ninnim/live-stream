using System.ComponentModel.DataAnnotations;
using LiveStream.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace LiveStream.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Whether to actually post anything.
    ///
    /// Off by default, and that is the honest default for a platform that ships without a mail
    /// server. What it must never do is fail quietly: with this off, every message is written to
    /// the log in full, so a developer resetting their own password finds the link in
    /// <c>docker compose logs api</c> rather than finding nothing and wondering which of the two
    /// halves is broken.
    /// </summary>
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>
    /// STARTTLS on the submission port, which is what almost every relay expects. Set false only
    /// for a relay on a trusted network that does not offer TLS at all.
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    public string? Username { get; set; }

    /// <summary>A secret. Comes from the environment and is never logged.</summary>
    public string? Password { get; set; }

    [Required]
    public string FromAddress { get; set; } = "no-reply@localhost";

    public string FromName { get; set; } = "Live Studio";

    /// <summary>
    /// Where the links in an email point. Defaults to the same origin join links use rather than
    /// being a second place to configure the web app's address.
    /// </summary>
    [Required]
    public string WebAppBaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>
    /// How long to wait on the mail server.
    ///
    /// Short on purpose: this is awaited inside an HTTP request, and a mail server that has stopped
    /// answering must not turn a password-reset request into a timeout for the person making it.
    /// </summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Posts mail over SMTP, or logs it when no mail server is configured.
///
/// One class rather than two implementations behind a flag, because the fallback is not a different
/// strategy — it is the same message, delivered to the only place available. Splitting them would
/// mean a developer's log and a production inbox could drift apart in content.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Host))
        {
            // The whole body, deliberately. A reset link in a development log is the difference
            // between the feature being testable without a mail server and not.
            logger.LogInformation(
                "Email is not configured; message not sent.\nTo: {To}\nSubject: {Subject}\n\n{Body}",
                message.ToAddress, message.Subject, message.TextBody);

            return false;
        }

        try
        {
            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
            mime.To.Add(MailboxAddress.Parse(message.ToAddress));
            mime.Subject = message.Subject;

            mime.Body = new BodyBuilder
            {
                TextBody = message.TextBody,
                HtmlBody = message.HtmlBody,
            }.ToMessageBody();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var client = new SmtpClient();

            await client.ConnectAsync(
                _options.Host,
                _options.Port,
                _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                timeout.Token);

            if (!string.IsNullOrEmpty(_options.Username))
            {
                // An empty password is a real configuration: some relays authenticate on the
                // username alone. Null is not, and would fail inside MailKit rather than here.
                await client.AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, timeout.Token);
            }

            await client.SendAsync(mime, timeout.Token);
            await client.DisconnectAsync(quit: true, timeout.Token);

            logger.LogInformation("Email sent to {To} subject={Subject}", message.ToAddress, message.Subject);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            // Never rethrown. A mail server being down must not turn into a 500 on a request the
            // caller has already been told to treat as accepted — and the exception carries the
            // recipient, never the message body, so nothing secret reaches the log.
            logger.LogError(ex, "Sending email to {To} failed", message.ToAddress);
            return false;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Sending email to {To} timed out after {Seconds}s",
                message.ToAddress, _options.TimeoutSeconds);
            return false;
        }
    }
}
