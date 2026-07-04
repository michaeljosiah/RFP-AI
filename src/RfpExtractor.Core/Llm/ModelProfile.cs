namespace RfpExtractor.Core.Llm;

/// <summary>
/// Everything call-shaping that follows from a model name, resolved ONCE via
/// <see cref="ModelCapabilities"/> and passed to each agent — instead of threading three loose
/// values through every constructor (the previous source of "wired one of them wrong" risk).
/// </summary>
/// <param name="Temperature">Sampling temperature, or null to omit (GPT-5 / o-series / Claude reject it).</param>
/// <param name="NativeJsonSchema">True to set a JSON-schema ResponseFormat; false (Claude) puts the schema in the prompt.</param>
/// <param name="MaxOutputTokens">Output-token budget, or null for the provider default (Claude needs its real ceiling).</param>
public sealed record ModelProfile(float? Temperature, bool NativeJsonSchema, int? MaxOutputTokens)
{
    /// <summary>Deterministic defaults for tests/fakes: temperature 0, native schema, provider budget.</summary>
    public static readonly ModelProfile Default = new(0f, true, null);

    public static ModelProfile For(string? model) => new(
        ModelCapabilities.TemperatureFor(model),
        ModelCapabilities.SupportsNativeJsonSchema(model),
        ModelCapabilities.MaxOutputTokensFor(model));
}
