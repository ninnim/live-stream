using System.Text.Json.Serialization;
using LiveStream.Api.BackgroundServices;
using LiveStream.Api.Endpoints;
using LiveStream.Api.Health;
using LiveStream.Api.Middleware;
using LiveStream.Api.Realtime;
using LiveStream.Api.Security;
using LiveStream.Application.Abstractions;
using LiveStream.Infrastructure;
using LiveStream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------------------------------------------------
// Logging — structured JSON so logs are queryable (docs/12-observability-and-reliability.md).
// --------------------------------------------------------------------------------------------
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
});

// --------------------------------------------------------------------------------------------
// Application services
// --------------------------------------------------------------------------------------------
builder.Services.AddLiveStreamInfrastructure(builder.Configuration);

builder.Services.AddOptions<InternalApiOptions>()
    .Bind(builder.Configuration.GetSection(InternalApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<JoinLinkOptions>()
    .Bind(builder.Configuration.GetSection(JoinLinkOptions.SectionName));

builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());
builder.Services.AddScoped<ILiveSessionNotifier, SignalRLiveSessionNotifier>();

builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Background loops. Each holds its own lease, so the work spreads across worker instances
// and never doubles up (docs/decisions/0015-scale-security-and-globalization.md).
builder.Services.AddHostedService<StreamHealthMonitor>();
builder.Services.AddHostedService<DestinationMonitor>();
builder.Services.AddHostedService<SourcePresenceMonitor>();
builder.Services.AddHostedService<AiJobWorker>();
builder.Services.AddHostedService<RetentionWorker>();

// Registered last so it stops first: hosted services shut down in reverse order, and
// readiness has to start failing before anything else begins tearing down.
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddHostedService<GracefulShutdownService>();

// Long enough for the drain window plus whatever is still in flight behind it.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));

builder.Services.AddLiveStreamRateLimiting(builder.Configuration);
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
// Readiness covers what this instance needs in order to serve a request: itself, and the
// database. The media gateway is reported separately — removing every API instance from the
// load balancer because the media plane is down would replace a degraded platform with an
// unreachable one.
builder.Services.AddHealthChecks()
    .AddCheck<DrainingHealthCheck>("draining", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<MediaGatewayHealthCheck>("media-gateway");

// --------------------------------------------------------------------------------------------
// Authentication and CORS
//
// Both are configured through the options system rather than by reading configuration during
// startup, so layered configuration sources are honoured and the validated options are the single
// source of truth for the signing key.
// --------------------------------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    // Paired devices authenticate with a source-scoped token resolved against the database, so an
    // operator's revocation takes effect on the device's very next request.
    .AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(
        DeviceAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IDeviceContext, DeviceContext>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(DeviceAuthorizationPolicies.PairedDevice, policy => policy
        .AddAuthenticationSchemes(DeviceAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .RequireClaim(DeviceAuthenticationHandler.SourceIdClaim));

builder.Services.AddCors();
builder.Services.AddSingleton<IConfigureOptions<CorsOptions>, ConfigureCorsOptions>();

// --------------------------------------------------------------------------------------------
// OpenTelemetry — traces and metrics for the session lifecycle (docs/12).
// --------------------------------------------------------------------------------------------
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("livestream-api"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        }
    });

var app = builder.Build();

// --------------------------------------------------------------------------------------------
// Pipeline
// --------------------------------------------------------------------------------------------
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors(ConfigureCorsOptions.WebAppPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness answers "is this process alive", and stays healthy right through a drain: an
// orchestrator that saw it fail would kill the process instead of letting it finish.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthEndpoints.WriteResponseAsync,
});

// Readiness answers "should this instance be sent traffic".
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthEndpoints.ReadyTag),
    ResponseWriter = HealthEndpoints.WriteResponseAsync,
});

// Everything, for dashboards. A degraded dependency is reported without failing readiness.
app.MapHealthChecks("/health/dependencies", new HealthCheckOptions
{
    ResponseWriter = HealthEndpoints.WriteResponseAsync,
});

app.MapAuthEndpoints();
app.MapLiveSessionEndpoints();
app.MapDestinationEndpoints();
app.MapSourceEndpoints();
app.MapStudioEndpoints();
app.MapAiEndpoints();
app.MapGovernanceEndpoints();
app.MapSsoEndpoints();
app.MapOperationsEndpoints();
app.MapMediaCallbackEndpoints();
app.MapHub<LiveSessionHub>("/hubs/live");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Applying migrations at startup keeps single-instance local and container runs simple. For
// multi-instance production, run migrations as a separate deploy step instead — see
// docs/troubleshooting/phase-1-local-setup.md.
if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.Run();

/// <summary>Exposed so integration tests can host the API with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
