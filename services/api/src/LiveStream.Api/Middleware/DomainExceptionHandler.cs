using LiveStream.Application.Abstractions;
using LiveStream.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LiveStream.Api.Middleware;

/// <summary>
/// Translates domain failures into RFC 9457 problem responses carrying a stable
/// <c>errorCode</c> (MASTER_BLUEPRINT.md §34). User-facing messages come from the domain and are
/// written to be actionable; unexpected exceptions never leak their detail to clients.
/// </summary>
public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, errorCode, message) = Classify(exception);

        // Exception handlers are singletons, so the request-scoped correlation context is resolved
        // per request rather than injected — capturing it in the constructor would be a captive
        // dependency and fails container validation.
        var correlationId = httpContext.RequestServices.GetService<ICorrelationContext>()?.CorrelationId
                            ?? httpContext.TraceIdentifier;

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled request failure correlationId={CorrelationId} path={Path}",
                correlationId, httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation(
                "Request rejected errorCode={ErrorCode} status={StatusCode} correlationId={CorrelationId} path={Path}",
                errorCode, statusCode, correlationId, httpContext.Request.Path);
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = message,
            Type = $"https://docs.livestream.local/errors/{errorCode}",
            Instance = httpContext.Request.Path,
            Extensions =
            {
                ["errorCode"] = errorCode,
                ["correlationId"] = correlationId,
            },
        };

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    private static (int StatusCode, string ErrorCode, string Message) Classify(Exception exception) => exception switch
    {
        InvalidStateTransitionException ex => (StatusCodes.Status409Conflict, ex.ErrorCode, ex.Message),

        DomainException ex => (MapStatusCode(ex.ErrorCode), ex.ErrorCode, ex.Message),

        // Anything unrecognised is a bug: log it fully, but tell the client nothing about internals.
        _ => (StatusCodes.Status500InternalServerError, "LIVE_000_UNEXPECTED",
            "Something went wrong on our side. Please try again."),
    };

    private static int MapStatusCode(string errorCode) => errorCode switch
    {
        ErrorCodes.SessionNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.PermissionDenied => StatusCodes.Status403Forbidden,
        ErrorCodes.AuthenticationFailed => StatusCodes.Status401Unauthorized,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.SessionNotReady => StatusCodes.Status409Conflict,
        ErrorCodes.InvalidStateTransition => StatusCodes.Status409Conflict,
        ErrorCodes.ConcurrencyConflict => StatusCodes.Status409Conflict,
        ErrorCodes.CredentialExpired => StatusCodes.Status401Unauthorized,
        ErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorCodes.MediaGatewayUnavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorCodes.StreamStartFailed => StatusCodes.Status502BadGateway,

        // Distribution. An external platform refusing or failing is a gateway problem, not the
        // caller's; our own relay being down is an availability problem.
        ErrorCodes.DestinationRejected => StatusCodes.Status502BadGateway,
        ErrorCodes.DestinationUnavailable => StatusCodes.Status502BadGateway,
        ErrorCodes.DestinationAuthFailed => StatusCodes.Status502BadGateway,
        ErrorCodes.ProviderApiError => StatusCodes.Status502BadGateway,
        ErrorCodes.ProviderQuotaExceeded => StatusCodes.Status502BadGateway,
        ErrorCodes.RelayUnavailable => StatusCodes.Status503ServiceUnavailable,

        // Well-formed requests that current state forbids.
        ErrorCodes.ProviderAccountUnavailable => StatusCodes.Status409Conflict,
        ErrorCodes.DestinationLimitReached => StatusCodes.Status409Conflict,
        ErrorCodes.SourceLimitReached => StatusCodes.Status409Conflict,

        // A stored credential that cannot be read is our misconfiguration, not the caller's input,
        // so it must surface as a server error and be logged as one.
        ErrorCodes.SecretProtectionFailed => StatusCodes.Status500InternalServerError,

        // Multi-device.
        ErrorCodes.SourceNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.AiJobNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.AiUnavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorCodes.AiFeatureDisabled => StatusCodes.Status409Conflict,
        ErrorCodes.SceneNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.DeviceNotAuthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.PairingCodeInvalid => StatusCodes.Status400BadRequest,

        // Scale, security and globalization.
        //
        // A plan limit is 409 rather than 429: a rate limit clears by waiting, and telling a
        // client to retry a request that can never succeed until the plan changes would have
        // it retry forever.
        ErrorCodes.PlanLimitReached => StatusCodes.Status409Conflict,
        ErrorCodes.WorkspaceNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.RegionNotAvailable => StatusCodes.Status421MisdirectedRequest,
        ErrorCodes.SsoNotConfigured => StatusCodes.Status404NotFound,
        ErrorCodes.SsoFailed => StatusCodes.Status401Unauthorized,
        ErrorCodes.RecordingExpired => StatusCodes.Status410Gone,

        _ => StatusCodes.Status400BadRequest,
    };
}
