using System.Text;
using RfpExtractor.Core.Abstractions;

namespace RfpExtractor.Core.Diagnostics;

/// <summary>
/// Renders the RESOLVED spreadsheet grid — exactly what the pipeline sees after the engine flattens
/// merged cells, evaluates formulas and captures fills — as human-readable text, one populated row per
/// line. The cheapest "see ground truth" tool: it removes any need to hand-parse OOXML (which is
/// error-prone — raw sheet XML stores merged values only at the anchor cell, so column positions lie).
/// Lives in Core (not the CLI) because any embedding host wants this diagnostic; it depends only on
/// the grid abstractions.
/// </summary>
public static class GridDump
{
    private const int TextCap = 60;             // keep a line readable; the full text is still in the cell
    private const int MaxRowsPerSheet = 2000;   // backstop for a pathological sheet, even in the file

    /// <summary>Dump a whole workbook: a header line + every sheet via <see cref="AppendSheet"/>.</summary>
    public static string Render(WorkbookGrid workbook, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# grid dump — {title} ({workbook.Sheets.Count} sheet(s))");
        foreach (var s in workbook.Sheets) AppendSheet(sb, s);
        return sb.ToString();
    }

    public static void AppendSheet(StringBuilder sb, SheetGrid sheet)
    {
        int nonEmpty = sheet.Cells.Count(c => !c.IsEmpty);
        sb.AppendLine();
        sb.AppendLine($"########## SHEET '{sheet.Name}' (index {sheet.Index}) — {sheet.Cells.Count} cells, {nonEmpty} non-empty ##########");

        var fills = sheet.Cells.Where(c => c.Fill != null).GroupBy(c => c.Fill!)
            .OrderByDescending(g => g.Count()).ToList();
        if (fills.Count > 0)
            sb.AppendLine("fills: " + string.Join(", ", fills.Select(g => $"#{g.Key}×{g.Count()}(empty {g.Count(c => c.IsEmpty)})")));

        // one line per row that has any content OR any fill (an empty-but-coloured answer cell matters).
        var rowGroups = sheet.Cells.GroupBy(c => c.Row)
            .Where(g => g.Any(c => !c.IsEmpty || c.Fill != null))
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var g in rowGroups.Take(MaxRowsPerSheet))
        {
            var shown = g.Where(c => !c.IsEmpty || c.Fill != null).OrderBy(c => c.Column).Select(Format);
            sb.AppendLine($"r{g.Key + 1,-4} | " + string.Join(" | ", shown));
        }
        if (rowGroups.Count > MaxRowsPerSheet)
            sb.AppendLine($"... ({rowGroups.Count - MaxRowsPerSheet} more populated rows)");
    }

    private static string Format(GridCell c)
    {
        var fill = c.Fill != null ? $" [#{c.Fill}]" : "";
        if (c.IsEmpty) return $"{c.Address}=∅{fill}";       // empty but coloured (an answer cell)
        var t = c.Text.Trim().Replace("\r", " ").Replace("\n", " ");
        if (t.Length > TextCap) t = t[..TextCap] + "…";
        return $"{c.Address}={t}{fill}";
    }
}
