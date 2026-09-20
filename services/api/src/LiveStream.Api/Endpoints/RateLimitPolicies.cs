using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using LiveStream.Api.Security;
using LiveStream.Domain.Common;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LiveStream.Api.Endpoints;

/// <summary>
/// Abuse-control limits (docs/11-security.md). Configurable because the right values depend on
/// deployment shape, and because a limit that blocks legitimate reconnects is worse than no limit.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Login/register/refresh attempts per minute. The classic brute-force target.</summary>
    [Range(1, 10000)]
    public int AuthenticationPerMinute { get; set; } = 10;

    /// <summary>Session creations per minute.</summary>
    [Range(1, 10000)]
    public int SessionCreationPerMinute { get; set; } = 20;

    /// <summary>
    /// Broadcaster credential issuances per minute. Sized for reconnects: a flapping network
    /// legitimately re-requests credentials, so this must not throttle recovery.
    /// </summary>
    [Range(1, 10000)]
    public int CredentialIssuancePerMinute { get; set; } = 60;

    /// <summary>
    /// Destination writes and account-linking attempts per minute. These handle long-lived platform
    /// secrets, so the limit is deliberately tighter than session editing.
    /// </summary>
    [Range(1, 10000)]
    public int DestinationManagementPerMinute { get; set; } = 30;

    /// <summary>Device invitations, renames and revocations per minute.</summary>
    [Range(1, 10000)]
    public int DeviceManagementPerMinute { get; set; } = 30;

    /// <summary>
    /// Pairing-code redemption attempts per minute, per address.
    ///
    /// The tightest limit in the system, and the only one that is load-bearing rather than
    /// hygienic: a pairing code is eight characters so it can be typed on a phone, and this limit —
    /// together with single use and a ten-minute expiry — is what makes guessing one impractical.
    /// </summary>
    [Range(1, 10000)]
    public int PairingAttemptsPerMinute { get; set; } = 10;
}

/// <summary>
/// Partitions by authenticated user where one exists, falling back to remote IP, so one noisy
/// tenant cannot exhaust another's budget. These are in-process limits; a multi-instance
/// deployment needs a distributed store (recorded as a known limitation).
/// </summary>
public static class RateLimitPolicies
{
    public const string Authentication = "auth";
    public const string SessionCreation = "session-creation";
    public const string CredentialIssuance = "credential-issuance";
    public const string DestinationManagement = "destination-management";
    public const string DeviceManagement = "device-management";
    public const string Pairing = "pairing";

    public static IServiceCollection AddLiveStreamRateLimiting(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    errorCode = ErrorCodes.RateLimited,
                    title = "Too many requests. Please wait a moment and try again.",
                }, cancellationToken);
            };

            AddFixedWindowPolicy(options, Authentication, o => o.AuthenticationPerMinute);
            AddFixedWindowPolicy(options, SessionCreation, o => o.SessionCreationPerMinute);
            AddFixedWindowPolicy(options, CredentialIssuance, o => o.CredentialIssuancePerMinute);
            AddFixedWindowPolicy(options, DestinationManagement, o => o.DestinationManagementPerMinute);
            AddFixedWindowPolicy(options, DeviceManagement, o => o.DeviceManagementPerMinute);
            AddFixedWindowPolicy(options, Pairing, o => o.PairingAttemptsPerMinute);
        });

        return services;
    }

    private static void AddFixedWindowPolicy(RateLimiterOptions options, string policyName,
        Func<RateLimitOptions, int> selectLimit) =>
        options.AddPolicy(policyName, context =>
        {
            var limits = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

            return RateLimitPartition.GetFixedWindowLimiter(
                $"{policyName}:{PartitionKey(context)}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = selectLimit(limits),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        });

    private static string PartitionKey(HttpContext context) =>
        context.User.GetUserId()?.ToString()
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";
}
