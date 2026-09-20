using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveStream.Domain.Common;

namespace LiveStream.Infrastructure.Distribution.Adapters;

/// <summary>
/// Shared HTTP handling for provider APIs.
///
/// Its real job is error normalization. Providers disagree about almost everything — status codes,
/// error envelopes, what counts as a quota problem — and the orchestrator must not learn any of it
/// (implementation/phase-2: "Provider-specific errors are normalized into stable internal error
/// codes").
/// </summary>
internal static class ProviderHttp
{
    /// <summary>
    /// Maps a failed provider response onto an internal code and a retry decision.
    ///
    /// The distinction that matters is retryable versus not. Retrying a rejected credential burns
    /// the retry budget and can get an account rate-limited, whereas giving up on a transient 503
    /// ends distribution for the rest of the broadcast.
    /// </summary>
    public static (string ErrorCode, bool Retryable) Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => (ErrorCodes.DestinationAuthFailed, false),
        HttpStatusCode.Forbidden => (ErrorCodes.ProviderQuotaExceeded, false),
        HttpStatusCode.NotFound => (ErrorCodes.ProviderApiError, false),
        HttpStatusCode.Conflict => (ErrorCodes.ProviderApiError, false),
        HttpStatusCode.TooManyRequests => (ErrorCodes.ProviderQuotaExceeded, false),
        HttpStatusCode.BadRequest => (ErrorCodes.ProviderApiError, false),
        >= HttpStatusCode.InternalServerError => (ErrorCodes.ProviderApiError, true),
        HttpStatusCode.RequestTimeout => (ErrorCodes.ProviderApiError, true),
        _ => (ErrorCodes.ProviderApiError, true),
    };

    /// <summary>
    /// Extracts a short, safe message from a provider error body.
    ///
    /// Bounded and stripped of structure because it ends up in a database column and on an
    /// operator screen; an unbounded provider payload belongs in neither.
    /// </summary>
    public static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return $"The platform returned {(int)response.StatusCode}.";
            }

            // Both Google and Meta nest a human-readable message under an "error" object.
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(message.GetString());
                    }

                    if (error.TryGetProperty("error_description", out var description)
                        && description.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(description.GetString());
                    }
                }
                else if (error.ValueKind == JsonValueKind.String)
                {
                    return Truncate(error.GetString());
                }
            }

            if (root.TryGetProperty("error_description", out var topLevelDescription)
                && topLevelDescription.ValueKind == JsonValueKind.String)
            {
                return Truncate(topLevelDescription.GetString());
            }

            return $"The platform returned {(int)response.StatusCode}.";
        }
        catch (JsonException)
        {
            // A non-JSON body is usually an HTML error page; the status code is the useful part.
            return $"The platform returned {(int)response.StatusCode}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"The platform returned {(int)response.StatusCode}.";
        }
    }

    public static async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return payload.ValueKind == JsonValueKind.Undefined ? null : payload;
    }

    public static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string Truncate(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "The platform rejected the request.";
        }

        return trimmed.Length <= 400 ? trimmed : trimmed[..400];
    }
}
