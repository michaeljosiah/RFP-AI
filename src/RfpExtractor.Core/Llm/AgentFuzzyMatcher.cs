using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Llm;

/// <summary>Structured-output envelope for the fuzzy matcher.</summary>
public sealed record MatchPairResult
{
    public List<MatchPair> Pairs { get; init; } = new();
}

/// <summary>
/// LLM-backed pairing of paraphrase duplicates the deterministic keys missed (each leg may
/// rephrase question_text, so exact/verbatim matching leaves a tail). Streams like the
/// extractors so GenCore idle timeouts cannot cut it.
/// </summary>
public sealed class AgentFuzzyMatcher : IFuzzyMatcher
{
    private readonly AIAgent _agent;

    /// <param name="temperature">
    /// Sampling temperature, or null to omit it — null for GPT-5 / o-series models that reject any
    /// non-default temperature (see <c>Wiring.TemperatureFor</c>); defaults to 0 for determinism.
    /// </param>
    /// <param name="nativeSchema">False for Claude — the schema goes in the prompt (see <c>ModelCapabilities.SupportsNativeJsonSchema</c>).</param>
    public AgentFuzzyMatcher(IChatClient chat, float? temperature = 0f, bool nativeSchema = true, int? maxOutputTokens = null)
    {
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(MatchPairResult), serializerOptions: Json.Json.Options);
        _agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Name = "fuzzy-reconciler",
            ChatOptions = new ChatOptions
            {
                Instructions = nativeSchema ? Prompts.Prompts.FuzzyMatch
                                            : Prompts.Prompts.FuzzyMatch + StructuredJson.SchemaInstruction(schema),
                Temperature = temperature,
                MaxOutputTokens = maxOutputTokens,
                ResponseFormat = nativeSchema
                    ? ChatResponseFormat.ForJsonSchema(schema, "match_pairs", "Pairs of question ids that refer to the same printed question.")
                    : null,
            },
        });
    }

    public async Task<IReadOnlyList<MatchPair>> MatchAsync(
        IReadOnlyList<Question> primary, IReadOnlyList<Question> secondary, CancellationToken ct)
    {
        object Slim(Question q) => new
        {
            id = q.QuestionId,
            verbatim = q.VerbatimSource,
            text = q.QuestionText,
            section = q.SectionPath,
        };
        var payload = JsonSerializer.Serialize(
            new { primary = primary.Select(Slim), secondary = secondary.Select(Slim) },
            Json.Json.Compact);

        var msg = new ChatMessage(ChatRole.User, payload);
        var sb = new StringBuilder();
        await foreach (var update in _agent.RunStreamingAsync(msg, cancellationToken: ct))
            sb.Append(update.Text);
        var raw = sb.ToString();
        if (string.IsNullOrWhiteSpace(raw)) raw = (await AgentResponseJson.FromAsync(_agent, msg, ct)).Json;

        var result = JsonSerializer.Deserialize<MatchPairResult>(StructuredJson.Payload(raw), Json.Json.Options);
        return result?.Pairs ?? new List<MatchPair>();
    }
}
