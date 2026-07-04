using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Reconciliation;

/// <summary>
/// Renders the reconciled question list at the requested output <see cref="Granularity"/>. The pipeline
/// now extracts at the PRINTED level and a deterministic decomposition pass fills <c>Parts</c> on
/// compound questions — so the reconciled list IS the canonical HYBRID form. This is an output-only
/// presentation:
///  - <c>Hybrid</c>: unchanged — printed questions with atomic <c>Parts</c> nested (each part tagged).
///  - <c>Bundled</c>: printed questions; the parts flattened to <c>sub_questions</c> strings.
///  - <c>Atomic</c>: each part becomes its own top-level question (single-ask questions pass through).
/// </summary>
public static class GranularityView
{
    /// <summary>Total atomic asks = sum of parts (a partless question counts as one).</summary>
    public static int AtomicCount(IReadOnlyList<Question> questions) =>
        questions.Sum(q => q.Parts.Count > 0 ? q.Parts.Count : 1);

    public static ExtractionResult Apply(ExtractionResult canonical, Granularity mode)
    {
        if (mode == Granularity.Hybrid) return canonical;   // the reconciled list is already hybrid

        var n = 0;
        var rendered = new List<Question>();
        foreach (var q in canonical.Questions)
        {
            if (q.Parts.Count == 0)
            {
                rendered.Add(q with { QuestionId = $"Q{++n:D3}", SubQuestions = new(), Parts = new() });
            }
            else if (mode == Granularity.Bundled)
            {
                rendered.Add(q with
                {
                    QuestionId = $"Q{++n:D3}",
                    SubQuestions = q.Parts.Select(p => p.QuestionText).ToList(),
                    Parts = new(),
                });
            }
            else // Atomic: expand each part into its own question
            {
                foreach (var p in q.Parts)
                    rendered.Add(q with
                    {
                        QuestionId = $"Q{++n:D3}",
                        AnswerTarget = p.AnswerTarget,
                        QuestionText = p.QuestionText,
                        AnswerType = p.AnswerType,
                        Retrieval = p.Retrieval,
                        SubQuestions = new(),
                        Parts = new(),
                    });
            }
        }
        return canonical with { Questions = rendered };
    }
}
