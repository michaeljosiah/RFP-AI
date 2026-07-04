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
                        Text: text, IsEmpty: cell.IsEmpty()));
                }
            }
            sheets.Add(new SheetGrid(ws.Name, si++, cells));
        }

        return Task.FromResult(new WorkbookGrid(sheets));
    }
}
