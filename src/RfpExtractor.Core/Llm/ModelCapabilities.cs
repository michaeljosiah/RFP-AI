using System.Text.RegularExpressions;

namespace RfpExtractor.Core.Llm;

/// <summary>Provider-independent facts about model families that affect how we call them.</summary>
public static class ModelCapabilities
{
    // Models that reject an explicit sampling temperature:
    //  - GPT-5 family (gpt-5, gpt-5.5, ...) and o-series reasoning models (o1, o3, o4, ...): the API
    //    returns HTTP 400 (unsupported_value) unless temperature is the default 1.
    //  - Newer Claude models: return HTTP 400 "temperature is deprecated for this model".
    // Match tolerantly so deployment-name prefixes (e.g. "prod-o3-vision", "azure-gpt-5.5") are caught.
    private static readonly Regex FixedTemperatureModel =
        new(@"gpt-?5|claude|(^|[^a-z0-9])o\d", RegexOptions.Compiled);

    /// <summary>
    /// The sampling temperature to send, or <c>null</c> to omit the parameter entirely. Returns null
    /// for models that only accept the default / reject temperature (GPT-5, o-series, Claude);
    /// otherwise 0 for deterministic extraction.
    /// </summary>
    public static float? TemperatureFor(string? model)
    {
        var m = (model ?? "").ToLowerInvariant().Replace(" ", "");
        return FixedTemperatureModel.IsMatch(m) ? null : 0f;
    }

    /// <summary>
    /// Whether a model accepts a NATIVE JSON-schema response format (ChatOptions.ResponseFormat).
    /// OpenAI / GenCore / Azure do. Claude's beta IChatClient mishandles it (returns an empty
    /// response), so for Claude we put the schema in the prompt instead and parse JSON from the text.
    /// </summary>
    public static bool SupportsNativeJsonSchema(string? model) =>
        !(model ?? "").ToLowerInvariant().Contains("claude");

    /// <summary>
    /// Default max output tokens to request, or null to keep the provider default. Anthropic REQUIRES
    /// max_tokens, and Claude's adaptive thinking draws from the SAME budget — a cap that's too small
    /// gets consumed by reasoning alone ("blocks=TextReasoningContent") or truncates the JSON mid-way.
    /// Set to each model family's actual max output ceiling on the standard (non-batch) Messages API —
    /// confirmed via platform.claude.com/docs: 128k for Opus/Sonnet/Fable/Mythos, 64k for Haiku — so
    /// truncation from an undersized cap cannot happen regardless of how much a call thinks. No beta
    /// header is needed for these; only the 300k Batch API ceiling requires one, and we don't use batch.
    /// </summary>
    public static int? MaxOutputTokensFor(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (!m.Contains("claude") && !m.Contains("opus") && !m.Contains("sonnet")
            && !m.Contains("haiku") && !m.Contains("fable") && !m.Contains("mythos")) return null;
        return m.Contains("haiku") ? 64_000 : 128_000;
    }

    /// <summary>
    /// Text-leg chunk size, capping <paramref name="requested"/> for THINKING models (GPT-5 / o-series /
    /// Claude — the same families that reject an explicit temperature). These spend part of their output
    /// budget on reasoning, and atomic granularity yields ~100 questions from a full 24k-char chunk, so a
    /// single response overflows the budget and truncates the JSON mid-array. A smaller chunk bounds each
    /// response's question count (more calls, but each one completes and parses).
    /// </summary>
    public static int TextChunkCharsFor(string? model, int requested) =>
        FixedTemperatureModel.IsMatch((model ?? "").ToLowerInvariant()) ? Math.Min(requested, 8_000) : requested;
}
