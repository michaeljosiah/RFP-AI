using ClosedXML.Excel;
using RfpExtractor.Core.Abstractions;

namespace RfpExtractor.LibreOffice;

/// <summary>xlsx -> exact cell grid via ClosedXML (MIT). Row/Column are stored 0-based; Address is A1.</summary>
public sealed class ClosedXmlSpreadsheetExtractor : ISpreadsheetExtractor
{
    public Task<WorkbookGrid> ExtractAsync(string path, CancellationToken ct)
    {
        using var wb = new XLWorkbook(path);
        var sheets = new List<SheetGrid>();
        int si = 0;

        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();      // IXLRange, null if the sheet is empty
            var cells = new List<GridCell>();
            if (used != null)
            {
                var a = used.RangeAddress;
                for (int r = a.FirstAddress.RowNumber; r <= a.LastAddress.RowNumber; r++)        // 1-based
                for (int c = a.FirstAddress.ColumnNumber; c <= a.LastAddress.ColumnNumber; c++)
                {
                    var cell = ws.Cell(r, c);
                    string text = cell.GetString().Trim();
                    cells.Add(new GridCell(
                        Address: $"{cell.Address.ColumnLetter}{cell.Address.RowNumber}",
                        Row: r - 1, Column: c - 1,
                        Text: text, IsEmpty: string.IsNullOrWhiteSpace(text),
                        Fill: FillHex(cell)));
                }
            }
            sheets.Add(new SheetGrid(ws.Name, si++, cells));
        }

        return Task.FromResult(new WorkbookGrid(sheets));
    }

    /// <summary>Normalized RRGGBB of a cell's solid fill, or null for no fill / a colour we can't
    /// resolve to RGB (theme-with-tint edge cases). White is treated as "no highlight".</summary>
    private static string? FillHex(IXLCell cell)
    {
        try
        {
            var fill = cell.Style.Fill;
            if (fill.PatternType == XLFillPatternValues.None) return null;
            var col = fill.BackgroundColor;
            if (col is null || col.ColorType != XLColorType.Color) return null;   // skip theme/indexed we can't trust
            var c = col.Color;
            if (c.A == 0) return null;
            var hex = $"{c.R:X2}{c.G:X2}{c.B:X2}";
            return hex == "FFFFFF" ? null : hex;   // plain white = no highlight
        }
        catch { return null; }
    }
}
