namespace LiveStream.Application.Ai;

/// <summary>
/// Turns token counts into an approximate cost.
///
/// Every AI job spends real money, and a platform that can run them without ever showing what they
/// cost is one whose bill arrives as a surprise. This is presented as an estimate — it is a
/// published list price applied to reported token counts, and it knows nothing about caching
/// discounts, batch pricing, or a negotiated rate.
///
/// Pure and table-driven so it can be tested, and so an unknown model returns null rather than a
/// confidently wrong number.
/// </summary>
public static class AiPricing
{
    /// <summary>US dollars per million tokens, as published. Keyed by exact model id.</summary>
    private static readonly Dictionary<string, (decimal InputPerMillion, decimal OutputPerMillion)> Rates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5"] = (5.00m, 25.00m),
            ["claude-sonnet-5"] = (2.00m, 10.00m),
            ["claude-haiku-4-5"] = (1.00m, 5.00m),
        };

    /// <summary>
    /// Estimated cost in US dollars, or null when the model is not one this table knows.
    ///
    /// Null rather than zero: a job that shows £0.00 reads as free, and a model whose price we do
    /// not know is not free.
    /// </summary>
    public static decimal? EstimateUsd(string? modelId, int inputTokens, int outputTokens)
    {
        if (modelId is null || !Rates.TryGetValue(modelId, out var rate))
        {
            return null;
        }

        var input = Math.Max(0, inputTokens) / 1_000_000m * rate.InputPerMillion;
        var output = Math.Max(0, outputTokens) / 1_000_000m * rate.OutputPerMillion;

        // Six places: a single small job costs a fraction of a cent, and rounding to cents would
        // report every one of them as zero.
        return Math.Round(input + output, 6);
    }
}
