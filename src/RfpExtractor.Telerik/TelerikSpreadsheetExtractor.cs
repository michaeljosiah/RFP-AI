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
                    cells.Add(new GridCell(A1(r, c), r, c, text, string.IsNullOrWhiteSpace(text)));
                }
            }
            sheets.Add(new SheetGrid(ws.Name, si++, cells));
        }

        return Task.FromResult(new WorkbookGrid(sheets));
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
