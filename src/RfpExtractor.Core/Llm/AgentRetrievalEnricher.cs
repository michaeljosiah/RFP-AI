using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Llm;

/// <summary>Structured-output envelope for the decompose+tag pass.</summary>
public sealed record DecomposeResult
{
    public List<DecomposedQuestion> Questions { get; init; } = new();
}

public sealed record DecomposedQuestion
{
    public string Id { get; init; } = "";
    public List<DecomposedPart> Parts { get; init; } = new();
}

public sealed record DecomposedPart
{
    public string QuestionText { get; init; } = "";
    public string AnswerType { get; init; } = "text";     // string, parsed tolerantly to the enum
    public string Category { get; init; } = "other";      // string so an off-list value degrades to Other
    public string? Units { get; init; }
    public bool RequiresExternalInput { get; init; } = true;
    public string? AiComment { get; init; }
}

/// <summary>
/// Post-reconciliation DECOMPOSE + TAG pass over each applicant-facing (printed-level) question:
///  - splits a COMPOUND printed prompt into its atomic asks (a single-ask question yields one part),
///  - tags each part with category / expected-format / units / external-input flag / AI comment.
/// A question with &gt;1 part gets its atomic breakdown in <c>Parts</c> (retrieval lives on the parts,
/// the parent is the answer box); a single-ask question carries its own <c>Retrieval</c> and no parts.
/// This makes the atomic breakdown DETERMINISTIC (a focused per-question task) instead of relying on
/// the extraction model to split as it reads. Runs ONCE over the deduplicated set. Best-effort: an
/// LLM/gateway failure leaves the deterministic baseline (single part, category Other) intact. Streams
/// like the other agents so GenCore idle timeouts cannot cut it. (Interface name kept for wiring stability.)
/// </summary>
public sealed class AgentRetrievalEnricher : IRetrievalEnricher
{
    private const int BatchSize = 40;   // smaller than the enricher's 60: decomposition emits more per item
    private readonly AIAgent _agent;

    public AgentRetrievalEnricher(IChatClient chat, float? temperature = 0f, bool nativeSchema = true, int? maxOutputTokens = null)
    {
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(DecomposeResult), serializerOptions: Json.Json.Options);
        _agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Name = "question-decomposer",
            ChatOptions = new ChatOptions
            {
                Instructions = nativeSchema ? Prompts.Prompts.Decompose
                                            : Prompts.Prompts.Decompose + StructuredJson.SchemaInstruction(schema),
                Temperature = temperature,
                MaxOutputTokens = maxOutputTokens,
                ResponseFormat = nativeSchema
                    ? ChatResponseFormat.ForJsonSchema(schema, "decomposition", "Atomic parts + retrieval tags per printed question.")
                    : null,
            },
        });
    }

    public async Task EnrichAsync(ExtractionResult result, CancellationToken ct)
    {
        // Only applicant-facing questions are decomposed/retrieved against; internal sections are skipped.
        var targets = result.Questions
            .Select((q, i) => (q, i))
            .Where(x => x.q.Audience == Audience.Applicant)
            .ToList();
        if (targets.Count == 0) return;

        for (int start = 0; start < targets.Count; start += BatchSize)
        {
            var batch = targets.Skip(start).Take(BatchSize).ToList();

            // 1) deterministic baseline: single ask, no parts, category Other, derived format.
            foreach (var (q, i) in batch)
                result.Questions[i] = q with { Retrieval = Baseline(q), Parts = new() };

            // 2) LLM decomposition + tags (best-effort).
            Dictionary<string, DecomposedQuestion>? decomp = null;
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    questions = batch.Select(x => new
                    {
                        id = x.q.QuestionId,
                        question = x.q.QuestionText,
                        section = x.q.SectionPath,
                        answer_type = x.q.AnswerType,
                    })
                }, Json.Json.Compact);

                var msg = new ChatMessage(ChatRole.User, payload);
                var sb = new StringBuilder();
                await foreach (var update in _agent.RunStreamingAsync(msg, cancellationToken: ct))
                    sb.Append(update.Text);
                var raw = sb.ToString();
                if (string.IsNullOrWhiteSpace(raw)) raw = (await AgentResponseJson.FromAsync(_agent, msg, ct)).Json;

                var parsed = JsonSerializer.Deserialize<DecomposeResult>(StructuredJson.Payload(raw), Json.Json.Options);
                decomp = parsed?.Questions
                    .Where(x => !string.IsNullOrEmpty(x.Id))
                    .GroupBy(x => x.Id)
                    .ToDictionary(g => g.Key, g => g.First());
            }
            catch (OperationCanceledException) { throw; }
            catch { decomp = null; }   // keep the baseline

            if (decomp is null) continue;

            foreach (var (q, i) in batch)
            {
                if (!decomp.TryGetValue(q.QuestionId, out var dq) || dq.Parts.Count == 0) continue;
                var cur = result.Questions[i];

                // A document request / upload is atomic by nature — one requested document, one part —
                // even if the model tried to split it by the periods/funds it mentions.
                var single = dq.Parts.Count == 1
                    || cur.Source == QuestionSource.DocumentRequest
                    || cur.AnswerType == AnswerType.DocumentUpload;
                if (single)
                {
                    // single ask -> tag the question itself, no parts.
                    result.Questions[i] = cur with { Retrieval = HintFor(dq.Parts[0], ExpectedFormatFor(cur)), Parts = new() };
                }
                else
                {
                    // compound -> atomic parts (each with its own retrieval); parent is the answer box.
                    var parts = dq.Parts.Select((p, k) => new QuestionPart
                    {
                        PartId = $"{cur.QuestionId}.{k + 1}",
                        AnswerTarget = $"{cur.AnswerTarget}-{k + 1}",
                        QuestionText = string.IsNullOrWhiteSpace(p.QuestionText) ? cur.QuestionText : p.QuestionText.Trim(),
                        AnswerType = ParseAnswerType(p.AnswerType, cur.AnswerType),
                        Retrieval = HintFor(p, ExpectedFormatFor(ParseAnswerType(p.AnswerType, cur.AnswerType), cur.Source)),
                    }).ToList();
                    result.Questions[i] = cur with { Retrieval = null, Parts = parts };
                }
            }
        }
    }

    private static RetrievalHint HintFor(DecomposedPart p, ExpectedFormat format) => new()
    {
        Category = ParseCategory(p.Category),
        ExpectedFormat = format,
        Units = string.IsNullOrWhiteSpace(p.Units) ? null : p.Units.Trim(),
        RequiresExternalInput = p.RequiresExternalInput,
        AiComment = string.IsNullOrWhiteSpace(p.AiComment) ? null : p.AiComment.Trim(),
    };

    private static RetrievalHint Baseline(Question q) => new()
    {
        Category = QuestionCategory.Other,
        ExpectedFormat = ExpectedFormatFor(q),
        RequiresExternalInput = true,   // safe default: assume RFP answers need the firm's own sources
    };

    public static ExpectedFormat ExpectedFormatFor(Question q) => ExpectedFormatFor(q.AnswerType, q.Source);

    /// <summary>A retrieval-oriented view of answer_type + source.</summary>
    public static ExpectedFormat ExpectedFormatFor(AnswerType type, QuestionSource source) => source switch
    {
        QuestionSource.DocumentRequest => ExpectedFormat.Document,
        QuestionSource.TableCell => ExpectedFormat.Value,
        _ => type switch
        {
            AnswerType.DocumentUpload => ExpectedFormat.Document,
            AnswerType.YesNo => ExpectedFormat.Boolean,
            AnswerType.Date => ExpectedFormat.Date,
            AnswerType.Number or AnswerType.Currency or AnswerType.Percentage => ExpectedFormat.Value,
            AnswerType.LongText => ExpectedFormat.Narrative,
            _ => ExpectedFormat.ShortText,
        },
    };

    /// <summary>Tolerant answer_type parse; unknown values fall back to <paramref name="fallback"/>.</summary>
    public static AnswerType ParseAnswerType(string? value, AnswerType fallback)
    {
        var norm = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var t in Enum.GetValues<AnswerType>())
            if (t.ToString().ToLowerInvariant() == norm) return t;
        return fallback;
    }

    /// <summary>Tolerant category parse: unknown / off-list values degrade to Other.</summary>
    public static QuestionCategory ParseCategory(string? value)
    {
        var norm = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var c in Enum.GetValues<QuestionCategory>())
            if (c.ToString().ToLowerInvariant() == norm) return c;
        return QuestionCategory.Other;
    }
}
