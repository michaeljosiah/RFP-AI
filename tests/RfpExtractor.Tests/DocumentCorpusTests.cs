using RfpExtractor.Core.Abstractions;
using RfpExtractor.LibreOffice;
using RfpExtractor.Telerik;
using Xunit;

namespace RfpExtractor.Tests;

/// <summary>
/// Runs the real Telerik engine adapters against a corpus of real questionnaire documents.
/// Deterministic and offline — no LLM, no network. Add a document by dropping it in TestData/
/// and adding a row to <see cref="Corpus"/>.
/// </summary>
public class DocumentCorpusTests
{
    // filename in TestData/  +  minimum expected data-entry tables (ground truth).
    //  - "1. EQDP RFP Questionnaire.docx": clean prose questionnaire (13 nested grids).
    //  - "FUND DUE DILIGENCE.docx": the IMAGE-HEAVY FORM case — ~99% drawings/ruled lines, only
    //    ~1.8k chars of real label text laid out as one big form table. It is the reason the
    //    cross-source reconciliation bug surfaced (text leg tags every field a table_cell, vision
    //    tags many as body). Kept in the corpus as the permanent regression for form-style docs.
    public static readonly TheoryData<string, int> Corpus = new()
    {
        { "FUND DUE DILIGENCE.docx", 1 },
        { "1. EQDP RFP Questionnaire.docx", 13 },
    };

    /// <summary>
    /// The corpus documents are REAL questionnaires and are deliberately NOT committed
    /// (confidential — see TestData/README.md and .gitignore). Tests no-op when a document is
    /// absent so a fresh clone still runs green; drop the documents into TestData/ locally to
    /// activate the full corpus regression.
    /// </summary>
    private static string? PathFor(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        return File.Exists(path) ? path : null;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Text_extractor_produces_clean_markdown_with_tables(string fileName, int minTables)
    {
        if (PathFor(fileName) is not string path) return;   // corpus doc not present locally
        // Both engines use the Open XML extractor for docx text (nested-table fidelity).
        var sd = await new OpenXmlTextExtractor().ExtractAsync(path, CancellationToken.None);

        Assert.True(sd.Markdown.Length > 1000, $"Markdown too short ({sd.Markdown.Length} chars) for {fileName}.");
        Assert.True(sd.Tables.Count >= minTables, $"Expected >= {minTables} tables, got {sd.Tables.Count} for {fileName}.");
        Assert.Contains("|", sd.Markdown); // markdown tables were emitted

        // The Telerik trial banner must never leak into the LLM input (StripTelerikBanner).
        Assert.DoesNotContain("trial will expire", sd.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("License validation couldn't run", sd.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("telerik.com/purchase", sd.Markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fund_dd_form_extracts_field_labels_as_a_single_form_table()
    {
        if (PathFor("FUND DUE DILIGENCE.docx") is not string path) return;   // corpus doc not present locally
        // Image-heavy form: sparse text, but the labelled fields must come through as a table so
        // the text leg has something to reconcile against the vision leg.
        var sd = await new OpenXmlTextExtractor().ExtractAsync(path, CancellationToken.None);

        Assert.Single(sd.Tables);                                 // one big form table
        foreach (var label in new[] { "Fund Name", "Management Fee", "Short-Selling", "Under Law", "Prospectus" })
            Assert.Contains(label, sd.Markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Renderer_produces_png_pages(string fileName, int _)
    {
        if (PathFor(fileName) is not string path) return;   // corpus doc not present locally
        var pages = await new TelerikRenderer().RenderToImagesAsync(path, dpi: 150, CancellationToken.None);

        Assert.NotEmpty(pages);
        var first = pages[0];
        Assert.Equal(1, first.PageNumber);
        Assert.True(first.PngBytes.Length > 1000, $"First page PNG too small for {fileName}.");
        // PNG magic number: 89 50 4E 47
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, first.PngBytes.Take(4).ToArray());
    }
}
