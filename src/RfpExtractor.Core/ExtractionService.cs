using RfpExtractor.Core.Models;
using RfpExtractor.Core.Pipeline;

namespace RfpExtractor.Core;

/// <summary>
/// Single public entry point. Stateless and idempotent; handles docx/pdf/xlsx.
/// Safe to expose directly as an LLM tool (returns the object, never writes files).
/// </summary>
public sealed class ExtractionService
{
    private readonly PipelineRouter _router;

    public ExtractionService(PipelineRouter router) { _router = router; }

    public async Task<ExtractionResult> ExtractAsync(
        string filePath, Strategy strategy = Strategy.Both, int dpi = 200, CancellationToken ct = default)
        => (await _router.RunAsync(filePath, new ExtractionOptions(strategy, dpi), ct)).Merged;
}
