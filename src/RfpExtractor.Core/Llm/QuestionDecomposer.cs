using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Pipeline;

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
/// the extraction model to split as it reads. Runs ONCE over the deduplicated set. Data-entry table
/// cells are atomic by nature (one cell = one value): they get the deterministic baseline tag but
/// are NOT sent to the LLM — on a table-heavy document that skips ~80% of the questions.
///
/// Batches run CONCURRENTLY under <see cref="ExtractionOptions.MaxParallel"/> and each batch gets the
/// same retry-×3 policy as the extraction legs (<see cref="Resilience"/>) — a transient gateway blip
/// must not silently leave a batch un-split, because that quietly deflates <c>answer_slots</c>
/// between otherwise-identical runs. A batch that still fails keeps its deterministic baseline
/// (single part, category Other) and is reported in the returned warnings.
/// </summary>
public sealed class QuestionDecomposer : IQuestionDecomposer
{
    private const int BatchSize = 40;   // smaller than a full chunk: decomposition emits more per item
    private readonly AIAgent _agent;

    public QuestionDecomposer(IChatClient chat, ModelProfile? profile = null)
    {
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(DecomposeResult), serializerOptions: Json.Json.Options);
        _agent = StructuredAgent.Create(chat, "question-decomposer", Prompts.Prompts.Decompose,
            schema, "decomposition", "Atomic parts + retrieval tags per printed question.",
            profile ?? ModelProfile.Default);
    }

    public async Task<IReadOnlyList<string>> DecomposeAsync(
        ExtractionResult result, ExtractionOptions options, CancellationToken ct)
    {
        // Only applicant-facing questions are tagged; internal sections are skipped.
        var applicant = result.Questions
            .Select((q, i) => (q, i))
            .Where(x => x.q.Audience == Audience.Applicant)
            .ToList();
        if (applicant.Count == 0) return Array.Empty<string>();

        // Deterministic baseline for EVERY applicant question first (single ask, no parts, derived
        // format) — so nothing is left without a retrieval hint even when the LLM is skipped or fails.
        foreach (var (q, i) in applicant)
            result.Questions[i] = q with { Retrieval = Baseline(q), Parts = new() };

        // A data-entry table cell is ONE cell / ONE value: it cannot decompose, so it keeps the
        // baseline and is NOT sent to the LLM. On a table-heavy document (e.g. the EQDP: 304 of 379
        // questions are cells) that is ~80% of the questions, so skipping them cuts the decompose
        // cost several-fold. Only body prompts + document requests are enriched.
        var targets = applicant.Where(x => x.q.Source != QuestionSource.TableCell).ToList();
        if (targets.Count == 0) return Array.Empty<string>();

        var batches = targets.Chunk(BatchSize).ToList();
        var skipped = applicant.Count - targets.Count;
        options.OnProgress?.Invoke($"decompose: {targets.Count} narrative question(s) -> {batches.Count} batch(es)"
            + (skipped > 0 ? $" (baseline-only for {skipped} table cell(s))" : "") + "; splitting...");

        var warnings = new ConcurrentBag<string>();
        using var sem = new SemaphoreSlim(options.MaxParallel);

        // Batches are independent (disjoint question indices) -> fan out like the extraction legs.
        await Task.WhenAll(batches.Select(async (batch, b) =>
        {
            await sem.WaitAsync(ct);
            try
            {
                // LLM decomposition + tags — retried; a final failure keeps the baseline + warns.
                var decomp = await Resilience.TryAsync(
                    () => CallModelAsync(batch, ct),
                    $"decompose batch {b + 1}/{batches.Count}", warnings,
                    options.RetryDelay, options.OnProgress, ct);
                if (decomp is null) return;

                foreach (var (q, i) in batch)
                {
                    if (!decomp.TryGetValue(q.QuestionId, out var dq) || dq.Parts.Count == 0) continue;
                    var cur = result.Questions[i];

                    // A document request / upload is one deliverable — TAG it but never SPLIT it by the
                    // periods/funds it lists. (Table cells never reach here — they are filtered above —
                    // but the guard keeps that invariant even if that filter is ever relaxed.)
                    var single = dq.Parts.Count == 1
                        || cur.Source == QuestionSource.TableCell
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
            finally { sem.Release(); }
        }));

        return warnings.ToList();
    }

    private async Task<Dictionary<string, DecomposedQuestion>> CallModelAsync(
        (Question q, int i)[] batch, CancellationToken ct)
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

        var json = await StructuredAgent.RunJsonAsync(_agent, new ChatMessage(ChatRole.User, payload), ct);
        var parsed = JsonSerializer.Deserialize<DecomposeResult>(json, Json.Json.Options)
            ?? throw new InvalidOperationException("Decomposition response deserialized to null.");

        return parsed.Questions
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.First());
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
