using RfpExtractor.Core.Abstractions;
using Telerik.Windows.Documents.Spreadsheet.Model;
using XlsxProvider = Telerik.Windows.Documents.Spreadsheet.FormatProviders.OpenXml.Xlsx.XlsxFormatProvider;

namespace RfpExtractor.Telerik;

/// <summary>xlsx -> exact cell grid (RadSpreadProcessing).</summary>
public sealed class TelerikSpreadsheetExtractor : ISpreadsheetExtractor
{
    public Task<WorkbookGrid> ExtractAsync(string path, CancellationToken ct)
    {
        Workbook wb;
        using (var fs = File.OpenRead(path)) wb = new XlsxProvider().Import(fs, TimeSpan.FromSeconds(120));

        var sheets = new List<SheetGrid>();
        int si = 0;
        foreach (var ws in wb.Worksheets)
        {
            var used = ws.UsedCellRange;                    // CellRange, may be null for an empty sheet
            var cells = new List<GridCell>();
            if (used != null)
            {
                for (int r = used.FromIndex.RowIndex; r <= used.ToIndex.RowIndex; r++)
                for (int c = used.FromIndex.ColumnIndex; c <= used.ToIndex.ColumnIndex; c++)
                {
                    CellSelection cell = ws.Cells[r, c];
                    string text = (cell.GetValue().Value?.RawValue ?? "").Trim();
                    cells.Add(new GridCell(A1(r, c), r, c, text, string.IsNullOrWhiteSpace(text), FillHex(cell)));
                }
            }
            sheets.Add(new SheetGrid(ws.Name, si++, cells));
        }

        return Task.FromResult(new WorkbookGrid(sheets));
    }

    /// <summary>A stable key for a cell's solid fill (RRGGBB for a local colour, or "theme-{name}" for
    /// a theme colour), or null for no fill / white. DDQ templates colour-code answer cells — and often
    /// use a THEME colour (Excel's default palette, e.g. a light-blue accent) rather than a hard RGB, so
    /// theme fills must be captured too or the answer cells look unhighlighted.</summary>
    private static string? FillHex(CellSelection cell)
    {
        try
        {
            if (cell.GetFill().Value is PatternFill pf)
            {
                var tc = pf.PatternColor;
                if (tc.IsFromTheme)
                {
                    var name = tc.ThemeColorType.ToString();
                    // a Background/Light theme slot is the page background (white), not a highlight.
                    return name.StartsWith("Background") || name.StartsWith("Light") ? null : $"theme-{name}";
                }
                var col = tc.LocalValue;                   // ARGB bytes; automatic / no-fill -> A == 0
                if (col.A == 0) return null;
                var hex = $"{col.R:X2}{col.G:X2}{col.B:X2}";
                return hex == "FFFFFF" ? null : hex;       // plain white = no highlight
            }
        }
        catch { /* gradient / unresolved -> treat as no highlight */ }
        return null;
    }

    // 0-based (row,col) -> A1 (e.g. 0,0 -> "A1")
    private static string A1(int row, int col)
    {
        string s = "";
        int c = col + 1;
        while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
        return $"{s}{row + 1}";
    }
}
