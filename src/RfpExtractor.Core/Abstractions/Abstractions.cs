using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Abstractions;

public sealed record PageImage(int PageNumber, byte[] PngBytes);

public sealed record StructuredDocument(string Markdown, IReadOnlyList<TableStructure> Tables);

public sealed record TableStructure(int Index, int Rows, int Columns, int PageHint);

public sealed record WorkbookGrid(IReadOnlyList<SheetGrid> Sheets);

public sealed record SheetGrid(string Name, int Index, IReadOnlyList<GridCell> Cells);

public sealed record GridCell(string Address, int Row, int Column, string Text, bool IsEmpty);

/// <summary>Renders a Word/PDF/Excel file to one PNG per page.</summary>
public interface IDocumentRenderer
{
    Task<IReadOnlyList<PageImage>> RenderToImagesAsync(string path, int dpi, CancellationToken ct);
}

/// <summary>Extracts a Word/PDF document as structured markdown + table ground truth.</summary>
public interface IStructuredTextExtractor
{
    Task<StructuredDocument> ExtractAsync(string path, CancellationToken ct);
}

/// <summary>Extracts an Excel workbook as an exact cell grid.</summary>
public interface ISpreadsheetExtractor
{
    Task<WorkbookGrid> ExtractAsync(string path, CancellationToken ct);
}

/// <summary>The three LLM extraction modes, all via Microsoft Agent Framework.</summary>
public interface ILlmExtractor
{
    Task<ExtractionResult> ExtractFromImageAsync(PageImage page, CancellationToken ct);
    Task<ExtractionResult> ExtractFromTextAsync(string markdown, int? pageHint, CancellationToken ct);
    Task<ExtractionResult> ExtractFromGridAsync(string sheetGridJson, CancellationToken ct);
}

public interface IReconciler
{
    /// <summary>Merges two legs one-to-one, renumbers answer targets into a single namespace and
    /// grafts secondary-only items into the merged schema so the 1:1 invariant holds.</summary>
    Task<ReconciledResult> ReconcileAsync(ExtractionResult primary, ExtractionResult secondary,
        IReadOnlyList<TableStructure> groundTruthTables, bool fuzzyMatch, CancellationToken ct);
}

/// <summary>One matched duplicate across the two legs (by question id within each leg).</summary>
public sealed record MatchPair
{
    public string Primary { get; init; } = "";
    public string Secondary { get; init; } = "";
}

/// <summary>LLM-backed pairing of paraphrased duplicates that deterministic keys missed.</summary>
public interface IFuzzyMatcher
{
    Task<IReadOnlyList<MatchPair>> MatchAsync(
        IReadOnlyList<Question> primary, IReadOnlyList<Question> secondary, CancellationToken ct);
}

/// <summary>Adds a <see cref="RetrievalHint"/> to each applicant-facing question, in place, so a
/// downstream answer-retrieval stage can query knowledge sources per question.</summary>
public interface IRetrievalEnricher
{
    Task EnrichAsync(ExtractionResult result, CancellationToken ct);
}
