using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Auth;
using LiveStream.Application.Common;
using LiveStream.Application.Distribution;
using LiveStream.Application.Governance;
using LiveStream.Application.Media;
using LiveStream.Application.Recordings;
using LiveStream.Application.Sessions;
using LiveStream.Application.Ai;
using LiveStream.Application.Sources;
using LiveStream.Application.Studio;
using LiveStream.Infrastructure.Auth;
using LiveStream.Infrastructure.Distribution;
using LiveStream.Infrastructure.Email;
using LiveStream.Infrastructure.Distribution.Adapters;
using LiveStream.Infrastructure.Media;
using LiveStream.Infrastructure.Ai;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Recordings;
using LiveStream.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LiveStream.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence, media, auth, and the application services.
    /// Options are validated on start so a deployment with a missing signing key or media URL fails
    /// immediately rather than midway through someone's broadcast.
    /// </summary>
    public static IServiceCollection AddLiveStreamInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<MediaMtxOptions>()
            .Bind(configuration.GetSection(MediaMtxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<IceOptions>()
            .Bind(configuration.GetSection(IceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RecordingStorageOptions>()
            .Bind(configuration.GetSection(RecordingStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<LiveSessionOptions>()
            .Bind(configuration.GetSection(LiveSessionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DistributionOptions>()
            .Bind(configuration.GetSection(DistributionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SourceOptions>()
            .Bind(configuration.GetSection(SourceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RuntimeOptions>()
            .Bind(configuration.GetSection(RuntimeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<GovernanceOptions>()
            .Bind(configuration.GetSection(GovernanceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<CostOptions>()
            .Bind(configuration.GetSection(CostOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The redirect URI has to match what is registered at every identity provider, so it
        // defaults to the same origin the join links use rather than to a second place to
        // configure the web app's address.
        services.AddOptions<SsoOptions>()
            .Bind(configuration.GetSection(SsoOptions.SectionName))
            .PostConfigure(options =>
            {
                if (configuration.GetSection($"{SsoOptions.SectionName}:WebAppBaseUrl").Value is null
                    && configuration["JoinLink:BaseUrl"] is { Length: > 0 } joinLinkBaseUrl)
                {
                    options.WebAppBaseUrl = joinLinkBaseUrl;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Same reasoning as the SSO redirect above: a reset link has to point at the web app, and
        // that address is already configured once for join links.
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .PostConfigure(options =>
            {
                if (configuration.GetSection($"{EmailOptions.SectionName}:WebAppBaseUrl").Value is null
                    && configuration["JoinLink:BaseUrl"] is { Length: > 0 } joinLinkBaseUrl)
                {
                    options.WebAppBaseUrl = joinLinkBaseUrl;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PasswordResetOptions>()
            .Bind(configuration.GetSection(PasswordResetOptions.SectionName))
            .PostConfigure(options =>
            {
                if (configuration.GetSection($"{PasswordResetOptions.SectionName}:WebAppBaseUrl").Value is null
                    && configuration["JoinLink:BaseUrl"] is { Length: > 0 } joinLinkBaseUrl)
                {
                    options.WebAppBaseUrl = joinLinkBaseUrl;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RelayOptions>()
            .Bind(configuration.GetSection(RelayOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ProvidersOptions>()
            .Bind(configuration.GetSection(ProvidersOptions.SectionName));

        // ValidateDataAnnotations does not run IValidatableObject, so the key material check is
        // wired explicitly. A deployment with an unusable encryption key must fail at startup, not
        // when someone first saves a stream key.
        services.AddOptions<SecretProtectionOptions>()
            .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !options.Validate(new ValidationContext(options)).Any(),
                "Secrets configuration is invalid: PrimaryKeyId must name a configured 32-byte base64 key.")
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<AppDbContext>(builder => builder.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));
        }

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton<IRecordingStore, FileSystemRecordingStore>();

        services.AddMediaMtxGateway();

        services.AddScoped<AuthService>();
        services.AddScoped<PasswordResetService>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddScoped<LiveSessionAuthorizationService>();
        services.AddScoped<LiveSessionService>();
        services.AddScoped<LiveSessionReconciler>();
        services.AddScoped<IngestCredentialService>();
        services.AddScoped<RecordingService>();
        services.AddScoped<SourceService>();
        services.AddScoped<SourceReconciler>();
        services.AddScoped<StudioService>();
        services.AddScoped<AiJobService>();
        services.AddScoped<AiJobRunner>();

        services.AddLiveStreamGovernance();

        // The only place a model is reached. Everything above works against IAiAnalyst, so a
        // deployment with no key gets a provider that says so rather than one that pretends.
        services.AddSingleton<IAiAnalyst, ClaudeAiAnalyst>();

        services.AddLiveStreamDistribution();

        return services;
    }

    /// <summary>
    /// <summary>
    /// Tenant governance, leader election, retention, and enterprise single sign-on (Phase 7).
    /// </summary>
    private static void AddLiveStreamGovernance(this IServiceCollection services)
    {
        services.AddScoped<WorkspaceAuthorizationService>();
        services.AddScoped<TenantLimitService>();
        services.AddScoped<RetentionService>();
        services.AddScoped<UsageService>();
        services.AddScoped<WorkspaceGovernanceService>();
        services.AddScoped<SsoService>();
        services.AddScoped<ISsoLoginStateStore, DistributedCacheSsoLoginStateStore>();

        // Discovery documents and JWKS are fetched from an issuer the workspace names, so the
        // timeout is short: a slow provider must not hold a sign-in request open indefinitely.
        services.AddHttpClient(OidcClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("livestream-platform/1.0");
        });

        services.AddSingleton<IOidcClient, OidcClient>();

        // Whether background loops need a lease is a deployment decision: a single instance
        // does not, and pays a database write per tick for the privilege.
        services.AddScoped<ILeaseCoordinator>(sp =>
            sp.GetRequiredService<IOptions<RuntimeOptions>>().Value.LeaderElection
                ? ActivatorUtilities.CreateInstance<DatabaseLeaseCoordinator>(sp)
                : ActivatorUtilities.CreateInstance<SingleInstanceLeaseCoordinator>(sp));
    }

    /// <summary>
    /// adapter itself; nothing in the application layer changes.
    /// </summary>
    private static void AddLiveStreamDistribution(this IServiceCollection services)
    {
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        // Memory-backed unless a distributed cache is registered; see the OAuth state store.
        services.AddDistributedMemoryCache();
        services.AddScoped<IOAuthStateStore, DistributedCacheOAuthStateStore>();

        AddProviderHttpClient(services, YouTubeAdapter.HttpClientName);
        AddProviderHttpClient(services, FacebookAdapter.HttpClientName);

        // Registered as singletons: adapters hold configuration and an HTTP client factory, never
        // per-request state.
        services.AddSingleton<IDestinationProviderAdapter, CustomRtmpAdapter>();
        services.AddSingleton<IDestinationProviderAdapter, TwitchAdapter>();
        services.AddSingleton<IDestinationProviderAdapter, TikTokAdapter>();
        services.AddSingleton<IDestinationProviderAdapter, YouTubeAdapter>();
        services.AddSingleton<IDestinationProviderAdapter, FacebookAdapter>();
        services.AddSingleton<IDestinationProviderRegistry, DestinationProviderRegistry>();

        services.AddHttpClient<IStreamRelay, HttpStreamRelay>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<RelayOptions>>().Value;

            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            // Server-side only, exactly like the media gateway control credentials: this header
            // never appears in any API response.
            client.DefaultRequestHeaders.Add("X-Internal-Auth", options.SharedSecret);
        });

        services.AddScoped<DestinationService>();
        services.AddScoped<ProviderAccountService>();
        services.AddScoped<DestinationOrchestrator>();

        // The coordinator resolves to the same scoped orchestrator instance, so a session lifecycle
        // call and a subsequent operator call in the same request share one change tracker.
        services.AddScoped<IDistributionCoordinator>(sp => sp.GetRequiredService<DestinationOrchestrator>());
    }

    private static void AddProviderHttpClient(IServiceCollection services, string name) =>
        services.AddHttpClient(name, client =>
        {
            // Provider APIs are called while a broadcast is starting, so a slow one must not hold
            // the start path open indefinitely.
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("livestream-platform/1.0");
        });

    private static void AddMediaMtxGateway(this IServiceCollection services)
    {
        services.AddHttpClient<IMediaGateway, MediaMtxGateway>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MediaMtxOptions>>().Value;

            client.BaseAddress = new Uri(options.ControlApiUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.ControlApiTimeoutSeconds);

            // Control API credentials stay on the server; they are never part of any API response.
            if (!string.IsNullOrWhiteSpace(options.ControlApiUser))
            {
                var raw = $"{options.ControlApiUser}:{options.ControlApiPassword}";
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
            }
        });
    }
}
