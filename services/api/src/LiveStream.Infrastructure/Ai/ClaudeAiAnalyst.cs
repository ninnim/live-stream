using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Ai;
using LiveStream.Domain.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveStream.Infrastructure.Ai;

/// <summary>
/// The Claude-backed analyst.
///
/// The only place in the platform that talks to a model. Everything above it works against
/// <see cref="IAiAnalyst"/>, so a different provider — or none — changes this file and nothing else.
///
/// Three rules shape it:
///
/// The prompt carries the session's own timeline and nothing else. No user identities, no
/// credentials, no stream keys. What is not assembled cannot leak.
///
/// The response is a schema-constrained JSON object rather than prose to be parsed. Free text that
/// a regular expression turns into structure is a failure mode waiting for an unusual session.
///
/// Failures are classified into retryable and not. A rate limit deserves another attempt; a refusal
/// or a malformed request does not, and retrying it three times just spends money to be told the
/// same thing.
/// </summary>
public sealed class ClaudeAiAnalyst : IAiAnalyst
{
    private readonly AiOptions _options;
    private readonly ILogger<ClaudeAiAnalyst> _logger;
    private readonly AnthropicClient? _client;

    public ClaudeAiAnalyst(IOptions<AiOptions> options, ILogger<ClaudeAiAnalyst> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _client = new AnthropicClient { ApiKey = _options.ApiKey };
        }
    }

    public bool IsConfigured => _client is not null;

    public async Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new AiProviderException("AI_NOT_CONFIGURED",
                "No AI provider is configured for this deployment.", retryable: false);
        }

        var prompt = AiPrompts.For(request.Kind);

        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = 16000,
                // Adaptive thinking: these are judgement tasks over a messy timeline, and the model
                // decides for itself how much reasoning each one needs.
                Thinking = new ThinkingConfigAdaptive(),
                System = prompt.System,
                OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat { Schema = prompt.Schema },
                },
                Messages = [new() { Role = Role.User, Content = BuildUserMessage(request) }],
            }, cancellationToken: cancellationToken);

            // A policy decline arrives as a 200 with this stop reason, so it has to be checked
            // before the content is read — otherwise it looks like an empty answer.
            if (response.StopReason == "refusal")
            {
                throw new AiProviderException("AI_REFUSED",
                    "The model declined to analyse this session.", retryable: false);
            }

            var json = string.Concat(response.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .Select(block => block.Text));

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new AiProviderException("AI_EMPTY_RESPONSE",
                    "The model returned nothing to record.", retryable: true);
            }

            return new AiAnalysisResult(
                ExtractSummary(json),
                json,
                _options.Model,
                (int)response.Usage.InputTokens,
                (int)response.Usage.OutputTokens);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new AiProviderException("AI_RATE_LIMITED",
                "The AI provider is rate limiting this workspace.", retryable: true, ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new AiProviderException("AI_PROVIDER_UNAVAILABLE",
                "The AI provider is temporarily unavailable.", retryable: true, ex);
        }
        catch (AnthropicApiException ex)
        {
            // Everything else from the API is our fault — a bad request, a missing model, an
            // invalid key. Retrying spends money to be told the same thing.
            _logger.LogError(ex, "AI request rejected for kind {Kind}", request.Kind);

            throw new AiProviderException("AI_REQUEST_REJECTED",
                "The AI provider rejected the request.", retryable: false, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException("AI_NETWORK_ERROR",
                "Could not reach the AI provider.", retryable: true, ex);
        }
    }

    /// <summary>
    /// Renders the session for the model.
    ///
    /// Plain text rather than JSON: the timeline is a sequence of events with times, and a model
    /// reads that more reliably as a list than as nested objects. Offsets are included because
    /// "sixty-two seconds in" is what a chapter marker needs, and wall-clock times are not.
    /// </summary>
    private static string BuildUserMessage(AiAnalysisRequest request)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Session title: {request.SessionTitle}");

        if (!string.IsNullOrWhiteSpace(request.SessionDescription))
        {
            builder.AppendLine($"Description: {request.SessionDescription}");
        }

        builder.AppendLine(request.StartedAt is null
            ? "This session never went live."
            : $"Went live at {request.StartedAt:u}.");

        if (request.DurationSeconds is { } duration)
        {
            builder.AppendLine($"Duration: {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}.");
        }

        builder.AppendLine();
        builder.AppendLine($"Timeline ({request.Timeline.Count} entries):");

        foreach (var entry in request.Timeline)
        {
            var offset = entry.OffsetSeconds is { } seconds && seconds >= 0
                ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss")
                : "--:--:--";

            builder.Append($"[{offset}] {entry.Category}/{entry.Type}");
            if (!string.IsNullOrWhiteSpace(entry.Detail)) builder.Append($" — {entry.Detail}");
            builder.AppendLine();
        }

        if (request.Timeline.Count == 0)
        {
            builder.AppendLine("(no events were recorded for this session)");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Pulls the one-line answer out for the list view.
    ///
    /// Every schema in <see cref="AiPrompts"/> has a top-level `summary`, so this is a contract
    /// rather than a guess — but a response that somehow lacks one yields an empty summary rather
    /// than throwing away an otherwise good result.
    /// </summary>
    private static string ExtractSummary(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty("summary", out var summary)
                   && summary.ValueKind == JsonValueKind.String
                ? summary.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// The prompts and the schemas their answers must fit.
///
/// Kept together because they are one thing: the schema is what makes the answer usable, and the
/// prompt is what makes it true. Both are versioned with the code rather than configured, so an
/// answer can be traced to the instructions that produced it.
/// </summary>
public static class AiPrompts
{
    public sealed record Prompt(string System, Dictionary<string, JsonElement> Schema);

    private const string SharedRules = """
        You are analysing the operational record of a live video broadcast on a streaming platform.

        You are given the session's own event timeline: state changes, device connections and
        disconnections, reconnections, and the status of the platforms it was being restreamed to.
        You are NOT given the content of the broadcast — you cannot see or hear it.

        Rules:
        - Describe only what the timeline supports. Never infer what was said or shown.
        - If the timeline is too sparse to answer well, say so in the summary rather than
          inventing detail.
        - Refer to times using the offsets given in square brackets.
        - Be concise and concrete. An operator is reading this to decide what to do next.
        """;

    public static Prompt For(AiJobKind kind) => kind switch
    {
        AiJobKind.SessionRecap => new Prompt(
            $"""
             {SharedRules}

             Produce a recap of how the broadcast ran: when it started and ended, which devices took
             part, where it was restreamed, and anything notable that happened along the way.
             """,
            Schema(
                ("summary", "string", "One sentence describing how the broadcast went."),
                ("narrative", "string", "A few short paragraphs recapping the session in order."),
                ("notableMoments", "string[]", "Points worth an operator's attention, each with its offset."))),

        AiJobKind.StreamQualityReview => new Prompt(
            $"""
             {SharedRules}

             Assess the technical quality of this broadcast. Identify what went wrong, how severe it
             was, and what the operator should change before the next show. Distinguish problems on
             the broadcaster's side from problems at a destination platform. If nothing went wrong,
             say so plainly rather than manufacturing concerns.
             """,
            Schema(
                ("summary", "string", "One sentence verdict on the broadcast's technical quality."),
                ("issues", "string[]", "Each problem found, with its offset and severity."),
                ("recommendations", "string[]", "Concrete changes to make before the next broadcast."))),

        AiJobKind.Chapters => new Prompt(
            $"""
             {SharedRules}

             Propose chapter markers for the recording, based on what the timeline shows changing.
             Only propose a chapter where the timeline gives a reason for one. Fewer, well-founded
             chapters are better than many speculative ones.
             """,
            Schema(
                ("summary", "string", "One sentence describing the shape of the broadcast."),
                ("chapters", "chapter[]", "Chapter markers in order."))),

        _ => throw new AiProviderException("AI_UNKNOWN_KIND", $"No prompt for {kind}.", retryable: false),
    };

    /// <summary>
    /// Builds the JSON schema the response must satisfy.
    ///
    /// Hand-built rather than reflected from a C# type: the schema is part of the prompt's meaning,
    /// and the descriptions in it are instructions the model reads.
    /// </summary>
    private static Dictionary<string, JsonElement> Schema(params (string Name, string Type, string Description)[] fields)
    {
        var properties = new Dictionary<string, object>();

        foreach (var (name, type, description) in fields)
        {
            properties[name] = type switch
            {
                "string" => new { type = "string", description },
                "string[]" => new { type = "array", description, items = new { type = "string" } },
                "chapter[]" => new
                {
                    type = "array",
                    description,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            offsetSeconds = new { type = "number", description = "Seconds from the start of the broadcast." },
                            title = new { type = "string", description = "A short chapter title." },
                            reason = new { type = "string", description = "The timeline entry this chapter is based on." },
                        },
                        required = new[] { "offsetSeconds", "title", "reason" },
                        additionalProperties = false,
                    },
                },
                _ => throw new InvalidOperationException($"Unsupported schema field type '{type}'."),
            };
        }

        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(properties),
            ["required"] = JsonSerializer.SerializeToElement(fields.Select(field => field.Name).ToArray()),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
    }
}
