using System.Text;
using LiveStream.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LiveStream.Api.Security;

/// <summary>
/// Builds JWT bearer validation from the validated <see cref="JwtOptions"/>.
///
/// Configured through the options system rather than read directly during startup so there is a
/// single validated source of truth for the signing key, and so hosts that layer configuration
/// (deployment overrides, integration tests) are honoured.
/// </summary>
public sealed class ConfigureJwtBearerOptions(IOptions<JwtOptions> jwtOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name is not JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _jwt.Issuer,
            ValidAudience = _jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Browsers cannot set headers on a WebSocket handshake, so SignalR passes the access
                // token as a query parameter. Only honoured for hub paths.
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    }
}

/// <summary>
/// Builds the studio's CORS policy from configuration at options-resolution time, for the same
/// reason as <see cref="ConfigureJwtBearerOptions"/>.
/// </summary>
public sealed class ConfigureCorsOptions(IConfiguration configuration) : IConfigureOptions<CorsOptions>
{
    public const string WebAppPolicy = "web-app";

    public void Configure(CorsOptions options)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        options.AddPolicy(WebAppPolicy, policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders(Middleware.CorrelationIdMiddleware.HeaderName));
    }
}
