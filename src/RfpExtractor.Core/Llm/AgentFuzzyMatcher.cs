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
/// rephrase question_text, so exact/verbatim matching leaves a tail). Built/run via
/// <see cref="StructuredAgent"/> (streaming, schema mode per <see cref="ModelProfile"/>).
/// </summary>
public sealed class AgentFuzzyMatcher : IFuzzyMatcher
{
    private readonly AIAgent _agent;

    public AgentFuzzyMatcher(IChatClient chat, ModelProfile? profile = null)
    {
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(MatchPairResult), serializerOptions: Json.Json.Options);
        _agent = StructuredAgent.Create(chat, "fuzzy-reconciler", Prompts.Prompts.FuzzyMatch,
            schema, "match_pairs", "Pairs of question ids that refer to the same printed question.",
            profile ?? ModelProfile.Default);
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

        var json = await StructuredAgent.RunJsonAsync(_agent, new ChatMessage(ChatRole.User, payload), ct);
        var result = JsonSerializer.Deserialize<MatchPairResult>(json, Json.Json.Options);
        return result?.Pairs ?? new List<MatchPair>();
    }
}
