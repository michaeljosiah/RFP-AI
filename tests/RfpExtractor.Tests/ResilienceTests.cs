using System.Collections.Concurrent;
using System.Text.Json;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Pipeline;
using RfpExtractor.Core.Reconciliation;
using Xunit;

namespace RfpExtractor.Tests;

public class MarkdownChunkerTests
{
    [Fact]
    public void Small_input_returns_single_chunk()
    {
        var chunks = MarkdownChunker.Chunk("# Title\n\nHello world.", maxChars: 24_000);
        Assert.Single(chunks);
    }

    [Fact]
    public void Chunks_preserve_all_content()
    {
        var md = string.Join("\n\n", Enumerable.Range(1, 50).Select(i => $"## Section {i}\n\nQuestion text number {i}?"));
        var chunks = MarkdownChunker.Chunk(md, maxChars: 300);

        Assert.True(chunks.Count > 1);
        var reassembled = string.Concat(chunks).Replace("\r\n", "\n").TrimEnd();
        Assert.Equal(md.Replace("\r\n", "\n").TrimEnd(), reassembled);
    }

    [Fact]
    public void Tables_are_never_split_across_chunks()
    {
        var table = "| Col A | Col B |\n| --- | --- |\n" +
                    string.Join("\n", Enumerable.Range(1, 20).Select(i => $"| row{i}a | row{i}b |"));
        var md = "Intro paragraph.\n\n" + table + "\n\nOutro paragraph.";
        var chunks = MarkdownChunker.Chunk(md, maxChars: 100);   // table alone exceeds the cap

        var tableChunk = chunks.Single(c => c.Contains("| row1a |"));
        Assert.Contains("| row20a |", tableChunk);               // whole table in one chunk
    }

    [Fact]
    public void Respects_max_size_except_oversized_single_blocks()
    {
        var md = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i} with some content."));
        var chunks = MarkdownChunker.Chunk(md, maxChars: 200);
        Assert.All(chunks, c => Assert.True(c.Length <= 200 + 50, $"Chunk too large: {c.Length}"));
    }
}

public class PipelineResilienceTests
{
    // ---------- fakes ----------

    private sealed class FakeRenderer(int pages) : IDocumentRenderer
    {
        public Task<IReadOnlyList<PageImage>> RenderToImagesAsync(string path, int dpi, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PageImage>>(
                Enumerable.Range(1, pages).Select(i => new PageImage(i, new byte[] { 1 })).ToList());
    }

    private sealed class FakeText(string markdown) : IStructuredTextExtractor
    {
        public Task<StructuredDocument> ExtractAsync(string path, CancellationToken ct) =>
            Task.FromResult(new StructuredDocument(markdown, Array.Empty<TableStructure>()));
    }

    private sealed class ThrowingRenderer : IDocumentRenderer
    {
        public Task<IReadOnlyList<PageImage>> RenderToImagesAsync(string path, int dpi, CancellationToken ct) =>
            throw new InvalidOperationException("cannot rasterize embedded image");
    }

    private sealed class FakeGrid(SheetGrid sheet) : ISpreadsheetExtractor
    {
        public Task<WorkbookGrid> ExtractAsync(string path, CancellationToken ct) =>
            Task.FromResult(new WorkbookGrid(new[] { sheet }));
    }

    /// <summary>Returns one question per call; permanently fails for the configured page number.</summary>
    private sealed class FlakyLlm(int failPage) : ILlmExtractor
    {
        public int TextCalls;
        public readonly ConcurrentBag<string> GridPayloads = new();

        public Task<ExtractionResult> ExtractFromImageAsync(PageImage page, CancellationToken ct) =>
            page.PageNumber == failPage
                ? throw new InvalidOperationException($"boom page {page.PageNumber}")
                : Task.FromResult(OneQuestion($"page{page.PageNumber}"));

        public Task<ExtractionResult> ExtractFromTextAsync(string markdown, int? pageHint, CancellationToken ct)
        {
            Interlocked.Increment(ref TextCalls);
            return Task.FromResult(OneQuestion($"chunk{pageHint}"));
        }

        public Task<ExtractionResult> ExtractFromGridAsync(string sheetGridJson, CancellationToken ct)
        {
            GridPayloads.Add(sheetGridJson);
            return Task.FromResult(OneQuestion("grid"));
        }
    }

    private static ExtractionResult OneQuestion(string key) => new()
    {
        DocumentSchema = new DocumentSchema
        {
            Sections = { new Section { Id = "s", Name = "S", Items = {
                new Item { Id = key, Type = ItemType.OpenQuestion, Verbatim = key, AnswerTarget = "AT-0001" } } } }
        },
        Questions =
        {
            new Question { QuestionId = "Q001", AnswerTarget = "AT-0001", QuestionText = $"Question {key}?",
                VerbatimSource = key, SectionPath = "S", Source = QuestionSource.Body,
                SchemaRef = new SchemaRef { SectionId = "s", ItemId = key } }
        }
    };

    private static ExtractionOptions FastOptions(Strategy s, int chunkChars = 24_000, int maxCells = 4_000) =>
        new(s, Dpi: 72, MaxParallel: 4, TextChunkChars: chunkChars, MaxCellsPerSheet: maxCells)
        { RetryDelay = TimeSpan.Zero };

    // ---------- tests ----------

    [Fact]
    public async Task Failed_page_is_skipped_with_warning_and_other_pages_survive()
    {
        var llm = new FlakyLlm(failPage: 2);
        var pipeline = new DocumentPipeline(new FakeRenderer(3), new FakeText(""), llm, new Reconciler());

        var result = await pipeline.RunAsync("doc.docx", FastOptions(Strategy.Vision), CancellationToken.None);

        Assert.Equal(2, result.Merged.Questions.Count);          // pages 1 + 3 survive
        Assert.Contains(result.Report.Warnings, w => w.Contains("vision page 2") && w.Contains("failed"));
    }

    [Fact]
    public async Task Text_leg_is_chunked_and_fans_out()
    {
        // 3 sections of ~120 chars with a 150-char cap -> 3 chunks -> 3 LLM calls
        var md = string.Join("\n\n", Enumerable.Range(1, 3).Select(i =>
            $"## Section {i}\n\n{new string('x', 100)} question {i}?"));
        var llm = new FlakyLlm(failPage: -1);
        var pipeline = new DocumentPipeline(new FakeRenderer(0), new FakeText(md), llm, new Reconciler());

        var result = await pipeline.RunAsync("doc.docx", FastOptions(Strategy.Text, chunkChars: 150), CancellationToken.None);

        Assert.Equal(3, llm.TextCalls);
        Assert.Equal(3, result.Merged.Questions.Count);          // stitched with unique targets
        Assert.Empty(result.Report.Warnings);
    }

    [Fact]
    public async Task Spreadsheet_render_failure_degrades_to_grid_only_with_warning()
    {
        // The grid leg is authoritative for Excel; a vision cross-check render failure (e.g. an
        // embedded image Telerik can't rasterize) must warn and fall back, not sink the run.
        var cells = new List<GridCell> { new("A1", 0, 0, "Firm AUM", false), new("B1", 0, 1, "", true) };
        var llm = new FlakyLlm(failPage: -1);
        var pipeline = new SpreadsheetPipeline(new FakeGrid(new SheetGrid("S", 0, cells)),
                                               new ThrowingRenderer(), llm, new Reconciler());

        // Strategy.Both triggers the vision cross-check, whose render throws.
        var result = await pipeline.RunAsync("wb.xlsx", FastOptions(Strategy.Both), CancellationToken.None);

        Assert.Contains(result.Report.Warnings, w => w.Contains("Vision cross-check skipped"));
        Assert.Equal(1, result.Merged.Questions.Count);   // grid question survived; no crash
    }

    [Fact]
    public async Task Sheet_payload_is_compact_capped_and_split_by_emptiness()
    {
        var cells = new List<GridCell>();
        for (int r = 0; r < 100; r++)
        {
            cells.Add(new GridCell($"A{r + 1}", r, 0, $"Label {r}", false));
            cells.Add(new GridCell($"B{r + 1}", r, 1, "", true));
        }
        var llm = new FlakyLlm(failPage: -1);
        var pipeline = new SpreadsheetPipeline(new FakeGrid(new SheetGrid("AUM", 0, cells)),
                                               new FakeRenderer(0), llm, new Reconciler());

        var result = await pipeline.RunAsync("wb.xlsx", FastOptions(Strategy.Text, maxCells: 50), CancellationToken.None);

        Assert.Contains(result.Report.Warnings, w => w.Contains("cap"));

        var payload = llm.GridPayloads.Single();
        Assert.DoesNotContain("\n", payload);                    // compact JSON, no indentation
        using var doc = JsonDocument.Parse(payload);
        int nonEmpty = doc.RootElement.GetProperty("cells").GetArrayLength();
        int empty = doc.RootElement.GetProperty("empty_cells").GetArrayLength();
        Assert.True(nonEmpty + empty <= 50, $"cap not applied: {nonEmpty}+{empty}");
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("empty_cells")[0].ValueKind); // bare addresses
    }
}
