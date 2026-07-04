using RfpExtractor.Core.Llm;
using Xunit;

namespace RfpExtractor.Tests;

public class ModelCapabilitiesTests
{
    // GPT-5 and o-series reasoning models reject any non-default temperature (HTTP 400) — we must
    // omit the parameter (null) for them.
    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5-mini")]
    [InlineData("gpt5")]
    [InlineData("azure-gpt-5.5")]   // deployment-name prefix still caught
    [InlineData("o1")]
    [InlineData("o1-preview")]
    [InlineData("o3-mini")]
    [InlineData("o4-mini")]
    [InlineData("prod-o3-vision")]
    [InlineData("GPT-5.5")]         // case-insensitive
    [InlineData("claude-sonnet-5")] // newer Claude models deprecate temperature
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-haiku-4-5-20251001")]
    public void Reasoning_and_gpt5_models_omit_temperature(string model)
        => Assert.Null(ModelCapabilities.TemperatureFor(model));

    // Everything else pins 0 for deterministic extraction. Note gpt-4o must NOT be mistaken for an
    // o-series model (the "o" follows a digit, not the other way round).
    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4o-mini")]
    [InlineData("gpt-4-turbo")]
    [InlineData("chatgpt-4o-latest")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_models_use_zero_temperature(string? model)
        => Assert.Equal(0f, ModelCapabilities.TemperatureFor(model));

    // Claude's beta client mishandles a native JSON-schema response format; everything else supports it.
    [Theory]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-opus-4-8", false)]
    [InlineData("CLAUDE-HAIKU-4-5", false)]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-5.5", true)]
    [InlineData("o3-mini", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void Native_json_schema_supported_except_claude(string? model, bool expected)
        => Assert.Equal(expected, ModelCapabilities.SupportsNativeJsonSchema(model));

    // Claude REQUIRES max_tokens and its thinking draws from the same budget — the SDK's small
    // default gets consumed by reasoning alone or truncates the JSON. Use each family's real ceiling
    // (per platform.claude.com/docs: 128k Opus/Sonnet/Fable/Mythos, 64k Haiku) so no cap is too small.
    [Theory]
    [InlineData("claude-sonnet-5", 128_000)]
    [InlineData("claude-opus-4-8", 128_000)]
    [InlineData("claude-fable-5", 128_000)]
    [InlineData("claude-mythos-preview", 128_000)]
    [InlineData("claude-haiku-4-5-20251001", 64_000)]
    [InlineData("CLAUDE-HAIKU-4-5", 64_000)]
    [InlineData("gpt-4o", null)]
    [InlineData("gpt-5.5", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Claude_gets_its_real_output_ceiling(string? model, int? expected)
        => Assert.Equal(expected, ModelCapabilities.MaxOutputTokensFor(model));

    // Thinking models (GPT-5 / o-series / Claude) get smaller text chunks so a single response's
    // JSON stays within the output budget; other models keep the requested size.
    [Theory]
    [InlineData("claude-sonnet-4-6", 24000, 8000)]
    [InlineData("gpt-5", 24000, 8000)]
    [InlineData("o3-mini", 24000, 8000)]
    [InlineData("claude-sonnet-5", 6000, 6000)]   // never grows a smaller requested size
    [InlineData("gpt-4o", 24000, 24000)]          // non-thinking: unchanged
    [InlineData("gpt-4o-mini", 24000, 24000)]
    [InlineData(null, 24000, 24000)]
    public void Thinking_models_get_smaller_chunks(string? model, int requested, int expected)
        => Assert.Equal(expected, ModelCapabilities.TextChunkCharsFor(model, requested));
}
