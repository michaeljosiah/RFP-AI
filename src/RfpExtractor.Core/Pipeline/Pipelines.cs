using System.Collections.Concurrent;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Reconciliation;
using RfpExtractor.Core.Validation;

namespace RfpExtractor.Core.Pipeline;

public enum Strategy { Vision, Text, Both }

/// <param name="MaxParallel">Concurrent LLM calls (tune to the GenCore rate limit).</param>
/// <param name="TextChunkChars">Max markdown chars per text-leg LLM call (~6k tokens). Bounding each
/// request is the primary GenCore-timeout mitigation.</param>
/// <param name="MaxCellsPerSheet">Cap on cells sent per worksheet payload; excess is truncated with a warning.</param>
/// <param name="OnProgress">Optional progress callback ("vision page 3: done", ...).</param>
public sealed record ExtractionOptions(
    Strategy Strategy = Strategy.Both,
    int Dpi = 200,
    int MaxParallel = 4,
    int TextChunkChars = 24_000,
    int MaxCellsPerSheet = 4_000,
    Action<string>? OnProgress = null)
{
    /// <summary>Base delay between retry attempts (attempt N waits N × this). Tests set Zero.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Run the LLM fuzzy-match pass over reconciliation leftovers (paraphrase duplicates).
    /// One extra LLM call per document; CLI flag --no-fuzzy disables it.</summary>
    public bool FuzzyReconcile { get; init; } = true;

    /// <summary>Fires once per completed unit (page / text chunk / sheet) with that unit's result —
    /// lets a UI stream discovered questions in real time. Leg is "vision" | "text" | "grid".</summary>
    public Action<string, ExtractionResult>? OnPartialResult { get; init; }

    /// <summary>Run the post-reconciliation decomposition pass (printed questions -> atomic parts +
    /// retrieval tags). CLI flag --no-decompose disables it, leaving printed-level questions.</summary>
    public bool Decompose { get; init; } = true;
}

/// <summary>Per-call retry + partial-failure tolerance: with 60+ LLM calls per large document, a
/// transient gateway blip must cost one retry, not the whole run. One policy for EVERY best-effort
/// LLM call (extraction legs AND the decomposition pass) so failure behaviour is uniform.</summary>
internal static class Resilience
{
    internal const int MaxAttempts = 3;

    /// <summary>Retries up to <see cref="MaxAttempts"/>; on final failure adds a warning and returns
    /// <c>null</c> so the caller supplies its own harmless fallback.</summary>
    internal static async Task<T?> TryAsync<T>(
        Func<Task<T>> call, string label, ConcurrentBag<string> warnings,
        TimeSpan retryDelay, Action<string>? onProgress, CancellationToken ct) where T : class
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var result = await call();
                onProgress?.Invoke($"{label}: done");
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                onProgress?.Invoke($"{label}: attempt {attempt} failed ({ex.Message}); retrying");
                await Task.Delay(retryDelay * attempt, ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"{label} failed after {MaxAttempts} attempts: {ex.Message}");
                onProgress?.Invoke($"{label}: FAILED - continuing without it");
                return null;
            }
        }
    }

    internal static async Task<ExtractionResult> SafeExtractAsync(
        Func<Task<ExtractionResult>> call, string label,
        ConcurrentBag<string> warnings, ExtractionOptions opts, CancellationToken ct)
        => await TryAsync(call, label, warnings, opts.RetryDelay, opts.OnProgress, ct)
           ?? new ExtractionResult();   // empty result stitches harmlessly
}

/// <summary>The shared tail of every pipeline run: collected warnings + invariant check + metrics.</summary>
internal static class ResultFinalizer
{
    internal static ReconciledResult Finalize(ReconciledResult result, ConcurrentBag<string> warnings)
    {
        foreach (var w in warnings) result.Report.Warnings.Add(w);
        foreach (var e in InvariantValidator.Validate(result.Merged)) result.Report.Warnings.Add(e);
        ReportMetrics.Populate(result.Report, result.Merged);
        return result;
    }
}

/// <summary>Word / PDF: vision leg + chunked structured-text leg run CONCURRENTLY, then reconciled.</summary>
public sealed class DocumentPipeline
{
    private readonly IDocumentRenderer _renderer;
    private readonly IStructuredTextExtractor _text;
    private readonly ILlmExtractor _llm;
    private readonly IReconciler _reconciler;

    public DocumentPipeline(IDocumentRenderer renderer, IStructuredTextExtractor text,
                            ILlmExtractor llm, IReconciler reconciler)
    { _renderer = renderer; _text = text; _llm = llm; _reconciler = reconciler; }

    public async Task<ReconciledResult> RunAsync(string path, ExtractionOptions opts, CancellationToken ct)
    {
        var warnings = new ConcurrentBag<string>();
        using var sem = new SemaphoreSlim(opts.MaxParallel);   // shared cap across both legs

        // The legs are independent until reconciliation -> run them concurrently
        // (wall clock = max(vision, text) instead of vision + text).
        // Task.Run matters here: the engine adapters do their work SYNCHRONOUSLY inside
        // Task-returning methods (Telerik render, Open XML parse), so calling the legs directly
        // would run the whole document render inline before the text leg even starts. Wrapping
        // each leg pushes its synchronous prefix onto the pool so both start immediately.
        var visionTask = opts.Strategy is Strategy.Vision or Strategy.Both
            ? Task.Run(() => RunVisionLegAsync(path, opts, sem, warnings, ct), ct)
            : Task.FromResult(new ExtractionResult());
        var textTask = opts.Strategy is Strategy.Text or Strategy.Both
            ? Task.Run(() => RunTextLegAsync(path, opts, sem, warnings, ct), ct)
            : Task.FromResult((Result: new ExtractionResult(), Tables: (IReadOnlyList<TableStructure>)Array.Empty<TableStructure>()));

        await Task.WhenAll(visionTask, textTask);
        var vision = visionTask.Result;
        var (text, groundTruth) = textTask.Result;

        ReconciledResult result;
        if (opts.Strategy == Strategy.Vision) result = ReconciledResult.FromSingle(vision);
        else if (opts.Strategy == Strategy.Text) result = ReconciledResult.FromSingle(text);
        else
        {
            opts.OnProgress?.Invoke("reconcile: matching legs...");
            result = await _reconciler.ReconcileAsync(primary: text, secondary: vision, groundTruth, opts.FuzzyReconcile, ct);
        }

        return ResultFinalizer.Finalize(result, warnings);
    }

    private async Task<ExtractionResult> RunVisionLegAsync(
        string path, ExtractionOptions opts, SemaphoreSlim sem, ConcurrentBag<string> warnings, CancellationToken ct)
    {
        opts.OnProgress?.Invoke("vision: rendering pages...");
        var pages = await _renderer.RenderToImagesAsync(path, opts.Dpi, ct);
        opts.OnProgress?.Invoke($"vision: {pages.Count} page(s) rendered; extracting...");

        var per = await Task.WhenAll(pages.Select(async p =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var r = await Resilience.SafeExtractAsync(
                    () => _llm.ExtractFromImageAsync(p, ct), $"vision page {p.PageNumber}", warnings, opts, ct);
                opts.OnPartialResult?.Invoke("vision", r);
                return r;
            }
            finally { sem.Release(); }
        }));
        return ResultMerger.StitchPages(per);
    }

    private async Task<(ExtractionResult Result, IReadOnlyList<TableStructure> Tables)> RunTextLegAsync(
        string path, ExtractionOptions opts, SemaphoreSlim sem, ConcurrentBag<string> warnings, CancellationToken ct)
    {
        var structured = await _text.ExtractAsync(path, ct);
        if (string.IsNullOrWhiteSpace(structured.Markdown))
            return (new ExtractionResult(), structured.Tables);   // e.g. scanned PDF -> vision-only

        // Chunk so no single request generates an unbounded response (GenCore timeout mitigation),
        // and fan the chunks out in parallel like vision pages.
        var chunks = MarkdownChunker.Chunk(structured.Markdown, opts.TextChunkChars);
        opts.OnProgress?.Invoke($"text: {structured.Markdown.Length} chars -> {chunks.Count} chunk(s); extracting...");

        var per = await Task.WhenAll(chunks.Select(async (chunk, i) =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var r = await Resilience.SafeExtractAsync(
                    () => _llm.ExtractFromTextAsync(chunk, i + 1, ct), $"text chunk {i + 1}/{chunks.Count}", warnings, opts, ct);
                opts.OnPartialResult?.Invoke("text", r);
                return r;
            }
            finally { sem.Release(); }
        }));
        return (ResultMerger.StitchPages(per), structured.Tables);
    }
}

/// <summary>Excel: grid leg (authoritative, sheets in parallel) + optional vision cross-check.</summary>
public sealed class SpreadsheetPipeline
{
    private readonly ISpreadsheetExtractor _grid;
    private readonly IDocumentRenderer _renderer;
    private readonly ILlmExtractor _llm;
    private readonly IReconciler _reconciler;

    public SpreadsheetPipeline(ISpreadsheetExtractor grid, IDocumentRenderer renderer,
                               ILlmExtractor llm, IReconciler reconciler)
    { _grid = grid; _renderer = renderer; _llm = llm; _reconciler = reconciler; }

    public async Task<ReconciledResult> RunAsync(string path, ExtractionOptions opts, CancellationToken ct)
    {
        var warnings = new ConcurrentBag<string>();
        using var sem = new SemaphoreSlim(opts.MaxParallel);

        var wb = await _grid.ExtractAsync(path, ct);
        opts.OnProgress?.Invoke($"grid: {wb.Sheets.Count} sheet(s); extracting...");

        var perSheet = await Task.WhenAll(wb.Sheets.Select(async sheet =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var payload = BuildSheetPayload(sheet, opts, warnings);
                var r = await Resilience.SafeExtractAsync(
                    () => _llm.ExtractFromGridAsync(payload, ct), $"sheet '{sheet.Name}'", warnings, opts, ct);
                opts.OnPartialResult?.Invoke("grid", r);
                return r;
            }
            finally { sem.Release(); }
        }));
        var gridResult = perSheet.Length == 0 ? new ExtractionResult() : ResultMerger.StitchPages(perSheet);

        if (opts.Strategy != Strategy.Both)
            return ResultFinalizer.Finalize(ReconciledResult.FromSingle(gridResult), warnings);

        // optional vision cross-check (xlsx -> pdf -> image)
        var pages = await _renderer.RenderToImagesAsync(path, opts.Dpi, ct);
        var per = await Task.WhenAll(pages.Select(async p =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var r = await Resilience.SafeExtractAsync(
                    () => _llm.ExtractFromImageAsync(p, ct), $"vision page {p.PageNumber}", warnings, opts, ct);
                opts.OnPartialResult?.Invoke("vision", r);
                return r;
            }
            finally { sem.Release(); }
        }));
        var vision = ResultMerger.StitchPages(per);

        opts.OnProgress?.Invoke("reconcile: matching legs...");
        var result = await _reconciler.ReconcileAsync(primary: gridResult, secondary: vision,
            Array.Empty<TableStructure>(), opts.FuzzyReconcile, ct);
        return ResultFinalizer.Finalize(result, warnings);
    }

    /// <summary>
    /// Compact LLM payload: non-empty cells as {address,text}, empty cells (the answer candidates)
    /// as a bare address list — far fewer prompt tokens than full objects per empty cell. Serialized
    /// WITHOUT indentation (indentation is token waste). Cell count is capped with a warning.
    /// </summary>
    public static string BuildSheetPayload(SheetGrid sheet, ExtractionOptions opts, ConcurrentBag<string> warnings)
    {
        IReadOnlyList<GridCell> cells = sheet.Cells;
        if (cells.Count > opts.MaxCellsPerSheet)
        {
            warnings.Add($"Sheet '{sheet.Name}': {cells.Count} cells exceed the {opts.MaxCellsPerSheet}-cell cap; " +
                         "payload truncated in row order. Raise MaxCellsPerSheet to cover the full sheet.");
            cells = cells.Take(opts.MaxCellsPerSheet).ToList();
        }

        var payload = new
        {
            sheet = sheet.Name,
            cells = cells.Where(c => !c.IsEmpty).Select(c => new { address = c.Address, text = c.Text }),
            empty_cells = cells.Where(c => c.IsEmpty).Select(c => c.Address),
        };
        return System.Text.Json.JsonSerializer.Serialize(payload, Json.Json.Compact);
    }
}

/// <summary>Selects the pipeline by file extension.</summary>
public sealed class PipelineRouter
{
    private readonly DocumentPipeline _doc;
    private readonly SpreadsheetPipeline _sheet;

    public PipelineRouter(DocumentPipeline doc, SpreadsheetPipeline sheet) { _doc = doc; _sheet = sheet; }

    public Task<ReconciledResult> RunAsync(string path, ExtractionOptions opts, CancellationToken ct)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".xlsx" or ".xlsm" or ".xls"
            ? _sheet.RunAsync(path, opts, ct)
            : _doc.RunAsync(path, opts, ct);
    }
}
