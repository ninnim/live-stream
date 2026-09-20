using LiveStream.Application.Abstractions;

namespace LiveStream.Api.Middleware;

/// <summary>
/// Per-request correlation identifier. Accepts an inbound <c>X-Correlation-Id</c> so a trace can be
/// followed from the studio through the API into session events, and echoes it on the response
/// (docs/12-observability-and-reliability.md).
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlationContext)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();

        // Inbound values are attacker-controlled and end up in logs, so bound and sanitise them.
        var correlationId = IsAcceptable(incoming) ? incoming! : context.TraceIdentifier;
        correlationContext.Set(correlationId);

        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId,
                   ["RequestPath"] = context.Request.Path.Value ?? string.Empty,
               }))
        {
            await next(context);
        }
    }

    private static bool IsAcceptable(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
}

/// <summary>Scoped holder for the current request's correlation identifier.</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; private set; } = "-";

    public void Set(string correlationId) => CorrelationId = correlationId;
}
