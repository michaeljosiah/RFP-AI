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

    /// <summary>Max cells per grid LLM call. A sheet larger than this is split into ROW-BAND chunks so
    /// the model's response can't truncate (a big single-shot grid overflows the output budget and
    /// collapses to a near-empty result). Kept modest because a colour-dense chunk emits a question per
    /// answer cell. Excel analogue of <see cref="TextChunkChars"/>.</summary>
    public int GridChunkCells { get; init; } = 600;
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
        var legend = ColourGridBuilder.FindLegend(wb);

        // Each sheet takes ONE of two paths:
        //  - COLOUR-CODED: an LLM classifies which fills mark answers (a tiny, reliable task), then the
        //    answer cells are enumerated DETERMINISTICALLY (one question per coloured cell) — because an
        //    LLM won't exhaustively list hundreds of near-identical cells (the Allianz DDQ: 382 cells).
        //  - PLAIN grid: the LLM enumerates from the row-band chunks (small grids fit fine).
        var sheetResults = new List<ExtractionResult>();
        int done = 0;
        foreach (var sheet in wb.Sheets)
        {
            var answerColours = await ClassifyAnswerColoursAsync(sheet, legend, warnings, opts, ct);
            ExtractionResult r;
            if (answerColours.Count > 0)
            {
                r = ColourGridBuilder.Enumerate(sheet, answerColours);
            }
            else
            {
                var payloads = BuildSheetPayloads(sheet, opts, warnings);
                var perChunk = await Task.WhenAll(payloads.Select(async (p, i) =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        return await Resilience.SafeExtractAsync(() => _llm.ExtractFromGridAsync(p, ct),
                            $"sheet '{sheet.Name}' chunk {i + 1}/{payloads.Count}", warnings, opts, ct);
                    }
                    finally { sem.Release(); }
                }));
                r = perChunk.Length == 0 ? new ExtractionResult() : ResultMerger.StitchPages(perChunk);
            }
            opts.OnPartialResult?.Invoke("grid", r);
            opts.OnProgress?.Invoke($"grid sheet {++done}/{wb.Sheets.Count} '{sheet.Name}': done ({r.Questions.Count} cells)");
            sheetResults.Add(r);
        }
        var gridResult = sheetResults.Count == 0 ? new ExtractionResult() : ResultMerger.StitchPages(sheetResults);

        if (opts.Strategy != Strategy.Both)
            return ResultFinalizer.Finalize(ReconciledResult.FromSingle(gridResult), warnings);

        // optional vision cross-check (xlsx -> pdf -> image). The grid leg is AUTHORITATIVE for a
        // spreadsheet, so a render failure (an embedded image Telerik can't rasterize, an unsupported
        // feature) must never sink the run — warn and return the grid result alone.
        IReadOnlyList<PageImage> pages;
        try
        {
            pages = await _renderer.RenderToImagesAsync(path, opts.Dpi, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            warnings.Add($"Vision cross-check skipped: could not render the workbook to images ({ex.Message}). Grid extraction used alone.");
            return ResultFinalizer.Finalize(ReconciledResult.FromSingle(gridResult), warnings);
        }
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

    /// <summary>Classify a sheet's fill colours into answer colours (best-effort LLM call). Keeps only
    /// colours that mark enough cells to be a real answer column — so a legend's few example swatches
    /// don't get mistaken for answers. Empty result => treat the sheet as a plain grid.</summary>
    private async Task<IReadOnlyList<AnswerColour>> ClassifyAnswerColoursAsync(
        SheetGrid sheet, IReadOnlyList<string> legend, ConcurrentBag<string> warnings, ExtractionOptions opts, CancellationToken ct)
    {
        var profile = ColourGridBuilder.BuildColourProfile(sheet, legend);
        if (profile is null) return Array.Empty<AnswerColour>();   // no fills -> plain grid

        var colours = await Resilience.TryAsync(() => _llm.DetectAnswerColoursAsync(profile, ct),
            $"sheet '{sheet.Name}' colour scan", warnings, opts.RetryDelay, opts.OnProgress, ct);
        if (colours is null || colours.Count == 0) return Array.Empty<AnswerColour>();

        var counts = sheet.Cells.Where(c => c.Fill != null)
            .GroupBy(c => c.Fill!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return colours.Where(c => counts.GetValueOrDefault(c.Fill) >= 4).ToList();
    }

    /// <summary>Header rows carried into every chunk of a split sheet, so a chunked classic grid
    /// (columns = years, rows = metrics) keeps its column-header context for phrasing questions.</summary>
    private const int GridHeaderRows = 3;

    /// <summary>
    /// Compact LLM payload(s) for one sheet: non-empty cells as {address,text}, empty cells (the answer
    /// candidates) as a bare address list — far fewer prompt tokens than full objects per empty cell,
    /// serialized WITHOUT indentation (indentation is token waste). A sheet with more than
    /// <see cref="ExtractionOptions.GridChunkCells"/> cells is split into ROW-BAND chunks so the model's
    /// response can't truncate; each chunk after the first also carries the sheet's header rows for
    /// column context. Answer candidates (empty_cells) come ONLY from a chunk's own band, so no answer
    /// cell is ever emitted by two chunks. Total cell count is capped first (with a warning).
    /// </summary>
    public static IReadOnlyList<string> BuildSheetPayloads(SheetGrid sheet, ExtractionOptions opts, ConcurrentBag<string> warnings)
    {
        IReadOnlyList<GridCell> cells = sheet.Cells;
        if (cells.Count > opts.MaxCellsPerSheet)
        {
            warnings.Add($"Sheet '{sheet.Name}': {cells.Count} cells exceed the {opts.MaxCellsPerSheet}-cell cap; " +
                         "payload truncated in row order. Raise MaxCellsPerSheet to cover the full sheet.");
            cells = cells.Take(opts.MaxCellsPerSheet).ToList();
        }
        if (cells.Count == 0) return Array.Empty<string>();

        // Sheet-wide fill histogram (distinct fill colour -> total count + how many are empty). Carried
        // into EVERY chunk so the model can spot the answer colour(s) even when the sheet is chunked —
        // e.g. "E2EFDA x254 (254 empty)" reads as a manual-entry answer colour, "EAEAEA x575 (1 empty)"
        // as auto-generated. This is what makes a colour-coded DDQ extractable.
        var fillSummary = cells.Where(c => c.Fill != null)
            .GroupBy(c => c.Fill!)
            .Select(g => new { fill = g.Key, count = g.Count(), empty = g.Count(c => c.IsEmpty) })
            .OrderByDescending(x => x.count)
            .Take(24)
            .Cast<object>()
            .ToList();

        if (cells.Count <= opts.GridChunkCells)
            return new[] { Serialize(sheet.Name, cells, Array.Empty<GridCell>(), fillSummary) };

        var ordered = cells.OrderBy(c => c.Row).ThenBy(c => c.Column).ToList();
        var firstRow = ordered[0].Row;
        var headerCells = ordered.Where(c => c.Row < firstRow + GridHeaderRows && !c.IsEmpty).ToList();

        var payloads = new List<string>();
        var band = new List<GridCell>();
        var bandIncludesHeader = true;   // the first band already starts at the header rows
        foreach (var c in ordered)
        {
            // never split a single row across chunks (keep a row's label + its answer cells together)
            if (band.Count >= opts.GridChunkCells && c.Row != band[^1].Row)
            {
                payloads.Add(Serialize(sheet.Name, band, bandIncludesHeader ? Array.Empty<GridCell>() : headerCells, fillSummary));
                band = new List<GridCell>();
                bandIncludesHeader = false;
            }
            band.Add(c);
        }
        if (band.Count > 0)
            payloads.Add(Serialize(sheet.Name, band, bandIncludesHeader ? Array.Empty<GridCell>() : headerCells, fillSummary));
        return payloads;
    }

    private static string Serialize(string sheetName, IReadOnlyList<GridCell> band,
        IReadOnlyList<GridCell> contextHeaders, IReadOnlyList<object> fillSummary)
    {
        // Context headers (non-empty label cells) are prepended for column context; answer candidates
        // come ONLY from the band, so a header row's own answer cells (emitted with chunk 1) are never
        // re-emitted by a later chunk. Dedup by address in case a header cell also falls in the band.
        // Fill colour travels on every cell (null omitted) so the model can key on it; the answer
        // cells of a coloured DDQ are split across BOTH lists — empty (green manual) and non-empty
        // (yellow dropdowns pre-filled with a formula) — hence fill on cells AND empty_cells.
        var nonEmpty = contextHeaders
            .Concat(band.Where(c => !c.IsEmpty))
            .GroupBy(c => c.Address)
            .Select(g => g.First());
        var payload = new
        {
            sheet = sheetName,
            fill_summary = fillSummary,
            cells = nonEmpty.Select(c => new { address = c.Address, text = c.Text, fill = c.Fill }),
            empty_cells = band.Where(c => c.IsEmpty).Select(c => new { address = c.Address, fill = c.Fill }),
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
