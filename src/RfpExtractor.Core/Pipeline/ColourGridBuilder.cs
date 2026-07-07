using System.Text.RegularExpressions;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Reconciliation;

namespace RfpExtractor.Core.Pipeline;

/// <summary>
/// Deterministic answer-cell enumeration for COLOUR-CODED spreadsheets. Professional DDQ templates
/// mark answer cells by fill colour (green = fill manually, yellow = drop-down, gray = auto, orange =
/// assessor). An LLM reliably decides WHICH colours are answers (a tiny classification), but will not
/// exhaustively emit a question for each of hundreds of near-identical cells — so this enumerates them
/// in code: one question per answer-coloured cell, phrased from the row's question text + the column
/// header. Guarantees completeness; the LLM is used only for the colour decision.
/// </summary>
public static class ColourGridBuilder
{
    private static readonly Regex LegendLine = new(
        @"cells?\s+(must|will|are|should|to)\b|drop-?down|generated automatically|filled\s+(in\s+)?(manually|by)|for\s+(office|internal)\s+use|assessor",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Colour-key lines found anywhere in the workbook (e.g. a "READ ME" legend), to help the
    /// classifier map colours to roles.</summary>
    public static IReadOnlyList<string> FindLegend(WorkbookGrid wb) =>
        wb.Sheets.SelectMany(s => s.Cells)
            .Where(c => !c.IsEmpty && c.Text.Trim().Length is > 8 and < 160 && LegendLine.IsMatch(c.Text))
            .Select(c => c.Text.Trim())
            .Distinct()
            .Take(12)
            .ToList();

    /// <summary>Classification input for one sheet (fill histogram + samples + legend), or null when the
    /// sheet carries no fills to classify.</summary>
    public static string? BuildColourProfile(SheetGrid sheet, IReadOnlyList<string> legend)
    {
        var byFill = sheet.Cells.Where(c => c.Fill != null).GroupBy(c => c.Fill!).ToList();
        if (byFill.Count == 0) return null;

        var rowQuestion = sheet.Cells.Where(c => !c.IsEmpty)
            .GroupBy(c => c.Row)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Text.Length).First().Text);

        var fills = byFill
            .OrderByDescending(g => g.Count())
            .Take(16)
            .Select(g => new
            {
                fill = g.Key,
                count = g.Count(),
                empty = g.Count(c => c.IsEmpty),
                sample_values = g.Where(c => !c.IsEmpty).Select(c => Cap(c.Text)).Take(3).ToList(),
                sample_row_questions = g.Select(c => rowQuestion.GetValueOrDefault(c.Row, ""))
                    .Where(t => t.Length > 0).Distinct().Take(3).Select(Cap).ToList(),
            });

        return System.Text.Json.JsonSerializer.Serialize(new { sheet = sheet.Name, legend, fills }, Json.Json.Compact);
    }

    /// <summary>One question per answer-coloured cell, phrased from row question + column header.</summary>
    public static ExtractionResult Enumerate(SheetGrid sheet, IReadOnlyList<AnswerColour> answerColours)
    {
        var answerFills = answerColours
            .GroupBy(c => c.Fill, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().AnswerType, StringComparer.OrdinalIgnoreCase);

        bool IsAnswer(GridCell c) => c.Fill != null && answerFills.ContainsKey(c.Fill);

        var byRow = sheet.Cells.GroupBy(c => c.Row).ToDictionary(g => g.Key, g => g.ToList());
        var byColRow = new Dictionary<(int Col, int Row), GridCell>();
        foreach (var c in sheet.Cells) byColRow[(c.Column, c.Row)] = c;

        var questions = new List<Question>();
        int n = 0;
        foreach (var cell in sheet.Cells.Where(IsAnswer).OrderBy(c => c.Row).ThenBy(c => c.Column))
        {
            byRow.TryGetValue(cell.Row, out var rowCells);
            // the row's question = its longest non-answer text; a short id-like cell = the row label
            var rowText = rowCells?.Where(c => !c.IsEmpty && !IsAnswer(c))
                .OrderByDescending(c => c.Text.Length).FirstOrDefault()?.Text.Trim() ?? "";
            var rowLabel = rowCells?.Where(c => !c.IsEmpty && !IsAnswer(c) && c.Text.Trim().Length is > 0 and <= 12)
                .Select(c => c.Text.Trim()).FirstOrDefault() ?? $"row {cell.Row + 1}";

            // column header = nearest non-answer, non-empty cell above in the same column
            var colHeader = "";
            for (int r = cell.Row - 1; r >= 0; r--)
                if (byColRow.TryGetValue((cell.Column, r), out var up) && !up.IsEmpty && !IsAnswer(up))
                { colHeader = up.Text.Trim(); break; }

            // a short header (first line, capped) differentiates I/J/K in the question text; the FULL
            // header stays in schema_ref.column. (These templates put whole instructions in the header.)
            var shortHeader = colHeader.Split('\n', '\r')[0].Trim();
            if (shortHeader.Length > 48) shortHeader = shortHeader[..48].TrimEnd() + "…";

            var qText = rowText.Length > 0
                ? (shortHeader.Length > 0 ? $"{rowText} — {shortHeader}" : rowText)
                : (shortHeader.Length > 0 ? $"{shortHeader} ({rowLabel})" : $"Provide the value for cell {cell.Address}");

            var at = $"AT-{++n:D4}";
            questions.Add(new Question
            {
                QuestionId = $"Q{n:D3}",
                AnswerTarget = at,
                QuestionText = qText,
                VerbatimSource = rowText.Length > 0 ? rowText : (colHeader.Length > 0 ? colHeader : cell.Address),
                AnswerType = answerFills[cell.Fill!],
                SectionPath = sheet.Name,
                Source = QuestionSource.TableCell,
                SchemaRef = new SchemaRef { Row = rowLabel, Column = colHeader },
                Binding = new Binding { Kind = "cell", Sheet = sheet.Name, Address = cell.Address },
                Confidence = Confidence.High,
            });
        }

        return GridSchema.Rebuild(new ExtractionResult { Questions = questions });   // synthesize schema
    }

    private static string Cap(string s) => s.Trim() is var t && t.Length <= 80 ? t : t[..80];
}
