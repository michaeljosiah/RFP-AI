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

        /// <summary>No colours by default -> the plain-grid (LLM chunk) path, as these tests expect.</summary>
        public Task<IReadOnlyList<AnswerColour>> DetectAnswerColoursAsync(string colourProfileJson, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AnswerColour>>(Array.Empty<AnswerColour>());

        /// <summary>Not a table -> the plain-grid (LLM chunk) path, as these tests expect.</summary>
        public Task<TableColumns?> DetectTableColumnsAsync(string tableProfileJson, CancellationToken ct) =>
            Task.FromResult<TableColumns?>(null);
    }

    /// <summary>Reports the given answer colours; counts (and refuses) LLM grid-enumeration calls.</summary>
    private sealed class ColouredGridLlm(params AnswerColour[] colours) : ILlmExtractor
    {
        public int GridCalls;
        public Task<ExtractionResult> ExtractFromImageAsync(PageImage page, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExtractionResult> ExtractFromTextAsync(string markdown, int? pageHint, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExtractionResult> ExtractFromGridAsync(string sheetGridJson, CancellationToken ct)
        { Interlocked.Increment(ref GridCalls); return Task.FromResult(new ExtractionResult()); }
        public Task<IReadOnlyList<AnswerColour>> DetectAnswerColoursAsync(string colourProfileJson, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AnswerColour>>(colours);
        public Task<TableColumns?> DetectTableColumnsAsync(string tableProfileJson, CancellationToken ct) =>
            Task.FromResult<TableColumns?>(null);
    }

    /// <summary>Reports a table layout (no colours); counts (and refuses) LLM grid-enumeration calls,
    /// proving the uncoloured table path enumerates in code.</summary>
    private sealed class TableGridLlm(TableColumns cols) : ILlmExtractor
    {
        public int GridCalls;
        public Task<ExtractionResult> ExtractFromImageAsync(PageImage page, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExtractionResult> ExtractFromTextAsync(string markdown, int? pageHint, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExtractionResult> ExtractFromGridAsync(string sheetGridJson, CancellationToken ct)
        { Interlocked.Increment(ref GridCalls); return Task.FromResult(new ExtractionResult()); }
        public Task<IReadOnlyList<AnswerColour>> DetectAnswerColoursAsync(string colourProfileJson, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AnswerColour>>(Array.Empty<AnswerColour>());
        public Task<TableColumns?> DetectTableColumnsAsync(string tableProfileJson, CancellationToken ct) =>
            Task.FromResult<TableColumns?>(cols);
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
    public void Colour_enumeration_emits_one_question_per_coloured_cell()
    {
        // A = question text, B = green (manual, empty), C = yellow (dropdown, pre-filled); + header row.
        var cells = new List<GridCell>
        {
            new("A1", 0, 0, "Question", false), new("B1", 0, 1, "Comments", false), new("C1", 0, 2, "Status", false),
        };
        for (int r = 1; r <= 3; r++)
        {
            cells.Add(new GridCell($"A{r + 1}", r, 0, $"Does the firm do thing {r}?", false));
            cells.Add(new GridCell($"B{r + 1}", r, 1, "", true, "E2EFDA"));
            cells.Add(new GridCell($"C{r + 1}", r, 2, "Yes", false, "FFFFCC"));
        }
        var colours = new[] { new AnswerColour("E2EFDA", AnswerType.LongText), new AnswerColour("FFFFCC", AnswerType.YesNo) };

        var result = Core.Pipeline.ColourGridBuilder.Enumerate(new SheetGrid("DDQ", 0, cells), colours);

        Assert.Equal(6, result.Questions.Count);                        // 3 rows x 2 answer colours
        Assert.Empty(Core.Validation.InvariantValidator.Validate(result));   // schema synthesized 1:1
        Assert.All(result.Questions, x => Assert.Equal(QuestionSource.TableCell, x.Source));
        var green = result.Questions.First(x => x.Binding!.Address == "B2");
        Assert.Equal(AnswerType.LongText, green.AnswerType);
        Assert.Contains("Does the firm do thing 1?", green.QuestionText);
        Assert.Contains("Comments", green.QuestionText);               // column header appended
        var yellow = result.Questions.First(x => x.Binding!.Address == "C2");
        Assert.Equal(AnswerType.YesNo, yellow.AnswerType);             // pre-filled cell still an answer
    }

    [Fact]
    public async Task Colour_coded_sheet_enumerates_deterministically_and_skips_the_llm_grid_call()
    {
        var cells = new List<GridCell> { new("A1", 0, 0, "Question", false), new("B1", 0, 1, "Response", false) };
        for (int r = 1; r <= 10; r++)
        {
            cells.Add(new GridCell($"A{r + 1}", r, 0, $"Question {r}?", false));
            cells.Add(new GridCell($"B{r + 1}", r, 1, "", true, "E2EFDA"));
        }
        var llm = new ColouredGridLlm(new AnswerColour("E2EFDA", AnswerType.Text));
        var pipeline = new SpreadsheetPipeline(new FakeGrid(new SheetGrid("S", 0, cells)),
                                               new FakeRenderer(0), llm, new Reconciler());

        var result = await pipeline.RunAsync("wb.xlsx", FastOptions(Strategy.Text), CancellationToken.None);

        Assert.Equal(10, result.Merged.Questions.Count);   // one per green cell, enumerated in code
        Assert.Equal(0, llm.GridCalls);                    // the LLM grid-enumeration call was NOT used
        Assert.Empty(result.Report.Warnings);
    }

    [Fact]
    public async Task Large_sheet_is_chunked_and_every_answer_cell_is_covered_exactly_once()
    {
        // 60 rows x 3 cols (label + 2 answer cells) = 180 cells; a small GridChunkCells forces chunks.
        var cells = new List<GridCell>();
        for (int r = 0; r < 60; r++)
        {
            cells.Add(new GridCell($"A{r + 1}", r, 0, $"Question {r}", false));
            cells.Add(new GridCell($"B{r + 1}", r, 1, "", true));
            cells.Add(new GridCell($"C{r + 1}", r, 2, "", true));
        }
        var llm = new FlakyLlm(failPage: -1);
        var opts = FastOptions(Strategy.Text) with { GridChunkCells = 60 };   // ~20 rows per chunk
        var pipeline = new SpreadsheetPipeline(new FakeGrid(new SheetGrid("S", 0, cells)),
                                               new FakeRenderer(0), llm, new Reconciler());

        await pipeline.RunAsync("wb.xlsx", opts, CancellationToken.None);

        Assert.True(llm.GridPayloads.Count > 1, $"sheet should have been chunked, got {llm.GridPayloads.Count}");
        var seen = new List<string>();
        foreach (var p in llm.GridPayloads)
        {
            using var doc = JsonDocument.Parse(p);
            foreach (var a in doc.RootElement.GetProperty("empty_cells").EnumerateArray())
                seen.Add(a.GetProperty("address").GetString()!);
        }
        var expected = cells.Where(c => c.IsEmpty).Select(c => c.Address).OrderBy(x => x).ToList();
        Assert.Equal(expected, seen.OrderBy(x => x).ToList());   // no duplicate, no miss
    }

    [Fact]
    public async Task Uncoloured_table_enumerates_in_code_skipping_blank_template_rows()
    {
        // typical DDQ shape: header row 4, then question rows, then a blank NUMBERED template row.
        // Columns B=No. D=Category E=Question F=Answer (0-based: 1,3,4,5).
        var cells = new List<GridCell>
        {
            new("B4", 3, 1, "No.", false),  new("D4", 3, 3, "Category", false),
            new("E4", 3, 4, "Question", false), new("F4", 3, 5, "Answer", false),

            new("B5", 4, 1, "1", false), new("D5", 4, 3, "General", false),
            new("E5", 4, 4, "What is your firm's AUM?", false), new("F5", 4, 5, "", true),

            new("B6", 5, 1, "2", false), new("D6", 5, 3, "Risk", false),
            new("E6", 5, 4, "Describe your risk framework.", false), new("F6", 5, 5, "", true),

            new("B7", 6, 1, "3", false), new("D7", 6, 3, "ESG", false),
            new("E7", 6, 4, "Do you have an ESG policy?", false), new("F7", 6, 5, "", true),

            // blank template row: a No. but NO question text -> must be skipped
            new("B8", 7, 1, "4", false), new("E8", 7, 4, "", true), new("F8", 7, 5, "", true),
        };
        var cols = new TableColumns(HeaderRow: 4, QuestionColumn: "E", AnswerColumn: "F",
                                    NumberColumn: "B", CategoryColumn: "D", AnswerType: AnswerType.LongText);
        var llm = new TableGridLlm(cols);
        var pipeline = new SpreadsheetPipeline(new FakeGrid(new SheetGrid("DDQ", 0, cells)),
                                               new FakeRenderer(0), llm, new Reconciler());

        var result = await pipeline.RunAsync("wb.xlsx", FastOptions(Strategy.Text), CancellationToken.None);

        Assert.Equal(3, result.Merged.Questions.Count);        // 3 question rows; blank template row skipped
        Assert.Equal(0, llm.GridCalls);                        // enumerated in code, not by the LLM
        Assert.Empty(result.Report.Warnings);
        var byBinding = result.Merged.Questions.OrderBy(q => q.Binding!.Address).ToList();
        Assert.Equal(new[] { "F5", "F6", "F7" }, byBinding.Select(q => q.Binding!.Address));   // bound to the ANSWER column
        Assert.Contains(result.Merged.Questions, q => q.QuestionText == "Do you have an ESG policy?" && q.SectionPath == "ESG");
    }

    [Fact]
    public async Task Coverage_guard_flags_suspected_under_enumeration_on_the_llm_grid_path()
    {
        // 20 answerable rows (a question label + an empty answer cell each), but the LLM returns just
        // one question -> the guard must warn that the sheet was likely under-enumerated.
        var cells = new List<GridCell>();
        for (int r = 0; r < 20; r++)
        {
            cells.Add(new GridCell($"A{r + 1}", r, 0, $"Please describe your process for area {r}.", false));
            cells.Add(new GridCell($"B{r + 1}", r, 1, "", true));
        }
        var sheet = new SheetGrid("S", 0, cells);
        Assert.Equal(20, SpreadsheetPipeline.CountAnswerableRows(sheet));   // the deterministic lower bound

        var llm = new FlakyLlm(failPage: -1);                              // returns 1 grid question
        var pipeline = new SpreadsheetPipeline(new FakeGrid(sheet), new FakeRenderer(0), llm, new Reconciler());

        var result = await pipeline.RunAsync("wb.xlsx", FastOptions(Strategy.Text), CancellationToken.None);

        Assert.Contains(result.Report.Warnings,
            w => w.Contains("under-enumerated") && w.Contains("answerable") && w.Contains("--dump-grid"));
    }

    [Fact]
    public async Task Coverage_guard_stays_quiet_on_small_sheets()
    {
        // only 5 answerable rows -> below the floor, a single-shot LLM enumeration is trusted (no noise).
        var cells = new List<GridCell>();
        for (int r = 0; r < 5; r++)
        {
            cells.Add(new GridCell($"A{r + 1}", r, 0, $"Please describe your process for area {r}.", false));
            cells.Add(new GridCell($"B{r + 1}", r, 1, "", true));
        }
        var llm = new FlakyLlm(failPage: -1);
        var pipeline = new SpreadsheetPipeline(new FakeGrid(new SheetGrid("S", 0, cells)),
                                               new FakeRenderer(0), llm, new Reconciler());

        var result = await pipeline.RunAsync("wb.xlsx", FastOptions(Strategy.Text), CancellationToken.None);

        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("under-enumerated"));
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
        Assert.Single(result.Merged.Questions);           // grid question survived; no crash
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
        var firstEmpty = doc.RootElement.GetProperty("empty_cells")[0];          // now {address, fill?}
        Assert.Equal(JsonValueKind.Object, firstEmpty.ValueKind);
        Assert.Equal(JsonValueKind.String, firstEmpty.GetProperty("address").ValueKind);
    }
}
