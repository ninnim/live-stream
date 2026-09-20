using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LiveStream.Relay;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
});

builder.Services.AddOptions<RelayServiceOptions>()
    .Bind(builder.Configuration.GetSection(RelayServiceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IEncoderTrial, FfmpegEncoderTrial>();
builder.Services.AddSingleton<ISourceInspector, FfprobeSourceInspector>();
builder.Services.AddSingleton<EncoderProbe>();
builder.Services.AddSingleton<RelaySupervisor>();
builder.Services.AddHealthChecks();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Probed once, before the first request, and logged. Doing it lazily would put a subprocess
// in the path of the first broadcast of the day, and doing it per relay would put one in the
// path of every broadcast.
await app.Services.GetRequiredService<EncoderProbe>().SelectAsync(CancellationToken.None);

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

// -------------------------------------------------------------------------------------------
// Relay control API.
//
// Private by construction: every route requires the shared secret, and the service is never
// published to the host. A request here carries a stream key in its body.
// -------------------------------------------------------------------------------------------
var relays = app.MapGroup("/relays");

relays.MapPost("/", (
    StartRelayRequest request,
    HttpContext http,
    RelaySupervisor supervisor,
    IOptions<RelayServiceOptions> options,
    ILoggerFactory loggerFactory) =>
{
    if (!IsAuthorized(http, options.Value))
    {
        return Results.Unauthorized();
    }

    var validation = Validate(request);
    if (validation is not null)
    {
        // The message names the missing field only. Echoing the request would put the stream key
        // into the response body and, from there, into whatever logs the error.
        loggerFactory.CreateLogger("Relay").LogWarning("Rejected relay start: {Reason}", validation);
        return Results.BadRequest(new { error = validation });
    }

    return supervisor.TryStart(request, out var state, out var failureReason)
        ? Results.Ok(state)
        : Results.Problem(failureReason, statusCode: StatusCodes.Status503ServiceUnavailable);
});

relays.MapDelete("/{destinationId:guid}", async (
    Guid destinationId,
    HttpContext http,
    RelaySupervisor supervisor,
    IOptions<RelayServiceOptions> options) =>
{
    if (!IsAuthorized(http, options.Value))
    {
        return Results.Unauthorized();
    }

    // Stopping something already gone is the desired end state, so this is deliberately not a 404.
    await supervisor.StopAsync(destinationId);
    return Results.NoContent();
});

relays.MapGet("/{destinationId:guid}", (
    Guid destinationId,
    HttpContext http,
    RelaySupervisor supervisor,
    IOptions<RelayServiceOptions> options) =>
{
    if (!IsAuthorized(http, options.Value))
    {
        return Results.Unauthorized();
    }

    var state = supervisor.Get(destinationId);
    return state is null ? Results.NotFound() : Results.Ok(state);
});

relays.MapGet("/", (
    HttpContext http,
    RelaySupervisor supervisor,
    IOptions<RelayServiceOptions> options) =>
    IsAuthorized(http, options.Value)
        ? Results.Ok(supervisor.List())
        : Results.Unauthorized());

app.Run();

static string? Validate(StartRelayRequest request)
{
    if (request.DestinationId == Guid.Empty)
    {
        return "destinationId is required.";
    }

    if (string.IsNullOrWhiteSpace(request.MediaPathName))
    {
        return "mediaPathName is required.";
    }

    if (string.IsNullOrWhiteSpace(request.SourceCredential))
    {
        return "sourceCredential is required.";
    }

    if (string.IsNullOrWhiteSpace(request.TargetStreamKey))
    {
        return "targetStreamKey is required.";
    }

    if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target)
        || target.Scheme is not ("rtmp" or "rtmps"))
    {
        // Re-validated here even though the control plane already checked it. This value becomes an
        // encoder output argument, so accepting an arbitrary scheme would let a compromised or
        // misconfigured caller write to a file path instead of a platform.
        return "targetUrl must be an absolute rtmp:// or rtmps:// URL.";
    }

    // The path segment is used to build the source URL, so it must not be able to climb out of it.
    if (request.MediaPathName.Contains('/') || request.MediaPathName.Contains('\\')
        || request.MediaPathName.Contains(".."))
    {
        return "mediaPathName must be a single path segment.";
    }

    return null;
}

static bool IsAuthorized(HttpContext http, RelayServiceOptions options)
{
    var presented = http.Request.Headers["X-Internal-Auth"].FirstOrDefault();

    if (string.IsNullOrEmpty(presented))
    {
        return false;
    }

    // Fixed-time comparison so the secret cannot be recovered by timing the endpoint.
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(presented),
        Encoding.UTF8.GetBytes(options.SharedSecret));
}

/// <summary>Exposed so tests can host the relay with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
