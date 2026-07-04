using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Docnet.Core;
using Docnet.Core.Models;
using RfpExtractor.Core.Abstractions;

namespace RfpExtractor.LibreOffice;

/// <summary>
/// docx -> Markdown + table ground truth via the Open XML SDK. Used by BOTH engines for the text
/// leg: Telerik's markdown export flattens tables nested inside layout tables (field finding from
/// the M&amp;G questionnaire — the nested AUM/team grids were lost, so the text leg emitted zero
/// table cells). This walker recurses instead:
///  - a table that CONTAINS other tables is treated as a layout table: label cells become
///    headings, cell content (paragraphs + nested tables) is rendered recursively;
///  - a leaf table becomes a real markdown pipe table and counts toward the ground truth.
/// PDF inputs fall back to a plain text-layer extraction (Docnet.Core); scanned PDFs yield empty
/// text so the pipeline degrades to vision-only.
/// </summary>
public sealed class OpenXmlTextExtractor : IStructuredTextExtractor
{
    public Task<StructuredDocument> ExtractAsync(string path, CancellationToken ct)
    {
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ExtractPdfText(path));

        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return Task.FromResult(new StructuredDocument("", Array.Empty<TableStructure>()));

        var sb = new StringBuilder();
        var tables = new List<TableStructure>();
        int tIdx = 0;
        RenderBlocks(body.ChildElements, sb, tables, ref tIdx, depth: 0);

        return Task.FromResult(new StructuredDocument(sb.ToString(), tables));
    }

    private static void RenderBlocks(IEnumerable<OpenXmlElement> els, StringBuilder sb,
        List<TableStructure> tables, ref int tIdx, int depth)
    {
        foreach (var el in els)
        {
            if (el is Paragraph p) RenderParagraph(p, sb);
            else if (el is Table t)
            {
                if (t.Descendants<Table>().Any()) RenderLayoutTable(t, sb, tables, ref tIdx, depth);
                else RenderPipeTable(t, sb, tables, ref tIdx);
            }
        }
    }

    private static void RenderParagraph(Paragraph p, StringBuilder sb)
    {
        var text = p.InnerText;
        if (string.IsNullOrWhiteSpace(text)) { sb.AppendLine(); return; }

        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            var digits = new string(style.Where(char.IsDigit).ToArray());
            var level = int.TryParse(digits, out var lv) ? lv : 1;
            sb.AppendLine($"{new string('#', Math.Clamp(level, 1, 6))} {text}");
        }
        else sb.AppendLine(text);
        sb.AppendLine();
    }

    /// <summary>A table containing nested tables is layout, not data: short first-column cells
    /// become section headings; everything else renders recursively in reading order.</summary>
    private static void RenderLayoutTable(Table t, StringBuilder sb,
        List<TableStructure> tables, ref int tIdx, int depth)
    {
        foreach (var row in t.Elements<TableRow>())
        {
            var cells = row.Elements<TableCell>().ToList();
            if (cells.Count == 0) continue;

            var first = DirectCellText(cells[0]);
            bool labelled = first.Length is > 0 and <= 64 && !cells[0].Descendants<Table>().Any();

            if (labelled && cells.Count >= 2)
            {
                sb.AppendLine($"{new string('#', Math.Min(depth + 2, 6))} {first}");
                sb.AppendLine();
                foreach (var c in cells.Skip(1)) RenderBlocks(c.ChildElements, sb, tables, ref tIdx, depth + 1);
            }
            else if (labelled && cells.Count == 1)
            {
                sb.AppendLine($"{new string('#', Math.Min(depth + 2, 6))} {first}");   // banner row
                sb.AppendLine();
            }
            else
            {
                foreach (var c in cells) RenderBlocks(c.ChildElements, sb, tables, ref tIdx, depth + 1);
            }
        }
    }

    private static void RenderPipeTable(Table t, StringBuilder sb, List<TableStructure> tables, ref int tIdx)
    {
        var rows = t.Elements<TableRow>().ToList();
        int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Elements<TableCell>().Count());
        tables.Add(new TableStructure(tIdx++, rows.Count, cols, 0));

        for (int r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Elements<TableCell>()
                .Select(c => c.InnerText.Replace("|", "\\|").Trim())
                .ToList();
            while (cells.Count < cols) cells.Add("");
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            if (r == 0)
                sb.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", cols)) + " |");
        }
        sb.AppendLine();
    }

    /// <summary>Text of the paragraphs directly under a cell (excludes nested-table content).</summary>
    private static string DirectCellText(TableCell cell) =>
        string.Join(" ", cell.Elements<Paragraph>().Select(p => p.InnerText.Trim())
            .Where(s => s.Length > 0)).Trim();

    private static StructuredDocument ExtractPdfText(string path)
    {
        // Shared PDFium singleton: don't dispose, serialize access (legs run concurrently).
        lock (Pdfium.Lock)
        {
            var lib = DocLib.Instance;
            using var reader = lib.GetDocReader(path, new PageDimensions(1.0));
            var sb = new StringBuilder();
            int count = reader.GetPageCount();
            for (int i = 0; i < count; i++)
            {
                using var pr = reader.GetPageReader(i);
                sb.AppendLine(pr.GetText());
                sb.AppendLine();
            }
            return new StructuredDocument(sb.ToString(), Array.Empty<TableStructure>());
        }
    }
}
