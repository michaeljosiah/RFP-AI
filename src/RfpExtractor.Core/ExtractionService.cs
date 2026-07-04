using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Pipeline;
using RfpExtractor.Core.Reconciliation;

namespace RfpExtractor.Core;

/// <summary>
/// The single public entry point — owns the WHOLE flow, so hosts (CLI, serve UI, or the RFP
/// solution this ports into) never re-assemble pipeline steps themselves:
///
///   extract (dual-leg + reconcile, printed level)
///     -> decompose printed questions into atomic parts + retrieval tags (optional, best-effort)
///     -> report metrics (<c>answer_slots</c> becomes the atomic total; warnings surfaced).
///
/// The returned <see cref="ReconciledResult.Merged"/> is the canonical HYBRID form; render
/// questions.json at a chosen <see cref="Granularity"/> with <see cref="GranularityView.Apply"/>.
/// Stateless and idempotent; handles docx/pdf/xlsx.
/// </summary>
public sealed class ExtractionService
{
    private readonly PipelineRouter _router;
    private readonly IQuestionDecomposer? _decomposer;

    public ExtractionService(PipelineRouter router, IQuestionDecomposer? decomposer = null)
    {
        _router = router;
        _decomposer = decomposer;
    }

    public async Task<ReconciledResult> RunAsync(string filePath, ExtractionOptions options, CancellationToken ct = default)
    {
        var result = await _router.RunAsync(filePath, options, ct);

        if (options.Decompose && _decomposer is not null)
        {
            var warnings = await _decomposer.DecomposeAsync(result.Merged, options, ct);
            foreach (var w in warnings) result.Report.Warnings.Add(w);
            result.Report.AnswerSlots = GranularityView.AtomicCount(result.Merged.Questions);
        }
        return result;
    }

    /// <summary>Convenience overload (safe to expose directly as an LLM tool — returns the merged
    /// object, never writes files).</summary>
    public async Task<ExtractionResult> ExtractAsync(
        string filePath, Strategy strategy = Strategy.Both, int dpi = 200, CancellationToken ct = default)
        => (await RunAsync(filePath, new ExtractionOptions(strategy, dpi), ct)).Merged;
}
