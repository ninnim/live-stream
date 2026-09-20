using System.Net.Http.Headers;
using System.Net.Http.Json;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Auth;
using LiveStream.Infrastructure.Persistence;
using LiveStream.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveStream.IntegrationTests;

/// <summary>
/// Hosts the real API — real routing, authentication, authorization, model binding, exception
/// handling and DI — against a real relational database.
///
/// Only the media plane and recording storage are substituted, because the media gateway is an
/// external process. Everything the control plane is responsible for is exercised for real.
/// </summary>
public class LiveStreamApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteTestDatabase _database = new();

    public FakeMediaGateway Media { get; } = new();

    public FakeRecordingStore RecordingStore { get; } = new();

    /// <summary>Stands in for the egress relay service, which supervises external encoder processes.</summary>
    public FakeStreamRelay Relay { get; } = new();

    /// <summary>Stands in for an enterprise identity provider, so SSO is exercised without one.</summary>
    public FakeOidcClient Oidc { get; } = new();

    /// <summary>Answers AI jobs without a model, so the pipeline is exercised without an API key.</summary>
    public FakeAiAnalyst Ai { get; } = new();

    /// <summary>
    /// Collects what would have been posted. Also the only way a test can read a reset token —
    /// which is the point: the plaintext exists nowhere else, not even in the database.
    /// </summary>
    public FakeEmailSender Email { get; } = new();

    /// <summary>
    /// Starts at real time because JWT validation uses the wall clock: a clock pinned to a fixed
    /// past date would make every freshly issued access token validate as already expired.
    /// Tests still control time by advancing this clock.
    /// </summary>
    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Host logs, so a failing request can be diagnosed and telemetry can be asserted on.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Throws with the server-side error when a request failed unexpectedly.</summary>
    public void ThrowOnServerError()
    {
        var failure = Logs.Failures.LastOrDefault();
        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Server logged an error: [{failure.Category}] {failure.Message}", failure.Exception);
        }
    }

    /// <summary>
    /// Asserts a successful response, surfacing the server-side exception and response body when it
    /// is not — otherwise every failure reads only as an opaque status code.
    /// </summary>
    public async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ThrowOnServerError();

        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"{(int)response.StatusCode} {response.StatusCode} from {response.RequestMessage?.RequestUri}: {body}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // The test environment does not enable container validation by default, which once let a
        // captive-dependency bug (a singleton capturing a scoped service) pass every test and then
        // crash the API on startup in a container. Validate explicitly so DI lifetime errors fail
        // here instead.
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // No Postgres connection string, so infrastructure registers no DbContext and the
            // SQLite one below is the only registration.
            ["ConnectionStrings:Postgres"] = string.Empty,
            ["Database:MigrateOnStartup"] = "false",
            ["Jwt:SigningKey"] = "integration-test-signing-key-that-is-long-enough-1234567890",
            ["Jwt:Issuer"] = "livestream-api",
            ["Jwt:Audience"] = "livestream-web",
            ["Jwt:AccessTokenLifetimeSeconds"] = "900",
            ["InternalApi:SharedSecret"] = InternalSecret,
            ["Media:MediaMtx:ControlApiUrl"] = "http://media.test:9997",
            ["Media:MediaMtx:PublicWebRtcUrl"] = "http://media.test:8889",
            ["Media:MediaMtx:PublicHlsUrl"] = "http://media.test:8888",
            ["Recording:RootPath"] = "./test-recordings",
            ["Media:Ice:Servers:0:Urls:0"] = "stun:stun.test:3478",

            // Distribution. The key is a fixed 32 zero bytes: deterministic, obviously a test value,
            // and it exercises the real AES-GCM protector rather than substituting a fake one.
            ["Secrets:PrimaryKeyId"] = "test",
            ["Secrets:Keys:test"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            ["Distribution:Relay:BaseUrl"] = "http://relay.test:8080",
            ["Distribution:Relay:SharedSecret"] = "integration-relay-secret-value",
            ["Distribution:MaxDestinationsPerSession"] = "5",
            ["Distribution:MaxRetryAttempts"] = "3",
            ["Distribution:InitialRetryDelaySeconds"] = "5",
            ["Distribution:ConnectTimeoutSeconds"] = "45",

            // Multi-device. The source limit is deliberately small so the limit test does not have
            // to invite eight cameras to prove it works.
            ["Sources:MaxSourcesPerSession"] = "4",
            ["Sources:PairingCodeLifetimeSeconds"] = "600",
            ["Sources:DeviceTokenLifetimeSeconds"] = "43200",
            ["Sources:SourcePresenceGraceSeconds"] = "20",
            ["JoinLink:BaseUrl"] = "http://studio.test",
            ["LiveSessions:RecoveryWindowSeconds"] = "120",
            ["LiveSessions:StartIngestTimeoutSeconds"] = "60",
            ["LiveSessions:HealthPollIntervalSeconds"] = "60",
            ["LiveSessions:MaxConcurrentSessionsPerWorkspace"] = "3",

            // Tests share one loopback address, so the production per-IP limits would throttle the
            // suite itself. Rate limiting is covered by its own test with a deliberately low limit.
            ["RateLimits:AuthenticationPerMinute"] = "10000",
            ["RateLimits:SessionCreationPerMinute"] = "10000",
            ["RateLimits:CredentialIssuancePerMinute"] = "10000",
            ["RateLimits:DestinationManagementPerMinute"] = "10000",
            ["RateLimits:DeviceManagementPerMinute"] = "10000",
            ["RateLimits:PairingAttemptsPerMinute"] = "10000",

            // AI on, with a fake analyst substituted below. The suite exercises the real job
            // pipeline — queueing, retries, permissions, results — without an API key.
            ["Ai:Enabled"] = "true",
            ["Ai:ApiKey"] = "test-key-not-used-by-the-fake",
            ["Ai:Model"] = "claude-opus-5",

            // Phase 7. The default plan matches the deployment limits configured above, so
            // plans are exercised without changing what any earlier phase test is allowed.
            ["Governance:DefaultPlan"] = "Pro",
            ["Governance:RetentionEnabled"] = "true",
            ["Runtime:Region"] = "eu-central",
            ["Runtime:LeaderElection"] = "false",
            ["Sso:WebAppBaseUrl"] = "http://studio.test",

            // Configured rates, so the cost report is exercised as an operator would see it.
            ["Costs:StreamingPerHour"] = "0.50",
            ["Costs:EgressPerGb"] = "0.08",
        }));

        builder.ConfigureServices(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_database.Connection));

            services.Replace(ServiceDescriptor.Singleton<IMediaGateway>(Media));
            services.Replace(ServiceDescriptor.Singleton<IRecordingStore>(RecordingStore));
            services.Replace(ServiceDescriptor.Singleton<IClock>(Clock));
            services.Replace(ServiceDescriptor.Singleton<IStreamRelay>(Relay));
            services.Replace(ServiceDescriptor.Singleton<IAiAnalyst>(Ai));
            services.Replace(ServiceDescriptor.Singleton<IOidcClient>(Oidc));
            services.Replace(ServiceDescriptor.Singleton<IEmailSender>(Email));

            // The health monitor is driven explicitly by tests so timing is deterministic.
            services.RemoveAll<IHostedService>();

            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddProvider(Logs);
            });
        });
    }

    public const string InternalSecret = "integration-internal-secret-value";

    public AppDbContext CreateDbContext() => _database.CreateContext();

    /// <summary>Registers a user and returns a client already authenticated as them.</summary>
    public async Task<AuthenticatedClient> CreateAuthenticatedClientAsync(string? email = null)
    {
        var client = CreateClient();
        email ??= $"user-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "a-sufficiently-long-password", "Test User", "Test Workspace"));

        if (!response.IsSuccessStatusCode)
        {
            ThrowOnServerError();
            response.EnsureSuccessStatusCode();
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>()
                   ?? throw new InvalidOperationException("Registration returned no body.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new AuthenticatedClient(client, auth);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _database.Dispose();
        }
    }
}

public sealed record AuthenticatedClient(HttpClient Client, AuthResponse Auth)
{
    public Guid UserId => Auth.User.Id;

    public Guid WorkspaceId => Auth.User.Workspaces[0].WorkspaceId;
}
