using System.Text.Json;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Reconciliation;

namespace RfpExtractor.Core.Pipeline;

/// <summary>
/// Deterministic question enumeration for UNCOLOURED, one-question-per-row questionnaire tables (the
/// most common Excel DDQ layout: No. | Category | Question | Answer). An LLM reliably decides the
/// column LAYOUT once (a tiny classification — see <see cref="ILlmExtractor.DetectTableColumnsAsync"/>),
/// but will not exhaustively LIST every row: it stops early (10 of 17 on a bilingual DDQ). So this
/// enumerates the rows in code — one question per row whose question cell is non-empty — guaranteeing
/// completeness and a consistent answer-cell binding. Sibling of <see cref="ColourGridBuilder"/> for
/// sheets that carry no answer fill colour.
/// </summary>
public static class TableGridBuilder
{
    private const int SampleRows = 8;      // header + a few data rows is enough to classify the layout

    /// <summary>Compact classification input: the first non-empty rows as {row (1-based), cells:[{col
    /// letter, text}]}. Null when the sheet has too few rows to be a table.</summary>
    public static string? BuildTableProfile(SheetGrid sheet)
    {
        var rowsWithData = sheet.Cells.Where(c => !c.IsEmpty)
            .GroupBy(c => c.Row)
            .OrderBy(g => g.Key)
            .Take(SampleRows)
            .ToList();
        if (rowsWithData.Count < 3) return null;   // too small to classify as a questionnaire table

        var rows = rowsWithData.Select(g => new
        {
            row = g.Key + 1,   // 1-based Excel row, matching the classifier's contract
            cells = g.OrderBy(c => c.Column).Select(c => new { col = ColLetter(c.Column), text = Cap(c.Text) }),
        });
        return JsonSerializer.Serialize(new { sheet = sheet.Name, rows }, Json.Json.Compact);
    }

    /// <summary>One question per data row whose QUESTION-column cell is non-empty, bound to that row's
    /// ANSWER-column cell. Rows with an empty question cell (blank numbered template rows) are skipped.</summary>
    public static ExtractionResult Enumerate(SheetGrid sheet, TableColumns cols)
    {
        int qCol = ColIndex(cols.QuestionColumn);
        if (qCol < 0) return new ExtractionResult();               // invalid column -> caller falls back
        int aCol = ColIndex(cols.AnswerColumn); if (aCol < 0) aCol = qCol;
        int catCol = ColIndex(cols.CategoryColumn);
        int numCol = ColIndex(cols.NumberColumn);
        int headerRow0 = cols.HeaderRow - 1;                       // 1-based Excel row -> 0-based grid row

        var byColRow = new Dictionary<(int Col, int Row), GridCell>();
        foreach (var c in sheet.Cells) byColRow[(c.Column, c.Row)] = c;

        var answerHeader = byColRow.TryGetValue((aCol, headerRow0), out var ah) && !ah.IsEmpty
            ? ah.Text.Trim() : "Answer";

        var questions = new List<Question>();
        int n = 0;
        var dataRows = sheet.Cells.Select(c => c.Row).Where(r => r > headerRow0).Distinct().OrderBy(r => r);
        foreach (var row in dataRows)
        {
            // enumerate ONLY rows that actually carry a question — this skips blank numbered template
            // rows (a "No." with no question text) that would otherwise become empty questions.
            if (!byColRow.TryGetValue((qCol, row), out var qCell) || qCell.IsEmpty) continue;
            var qText = qCell.Text.Trim();

            var answerAddr = byColRow.TryGetValue((aCol, row), out var aCell) ? aCell.Address : A1(aCol, row);
            var category = catCol >= 0 && byColRow.TryGetValue((catCol, row), out var catCell) && !catCell.IsEmpty
                ? catCell.Text.Trim() : sheet.Name;
            var noLabel = numCol >= 0 && byColRow.TryGetValue((numCol, row), out var nCell) && !nCell.IsEmpty
                ? nCell.Text.Trim() : $"row {row + 1}";

            var at = $"AT-{++n:D4}";
            questions.Add(new Question
            {
                QuestionId = $"Q{n:D3}",
                AnswerTarget = at,
                QuestionText = qText,
                VerbatimSource = qText,
                AnswerType = cols.AnswerType,
                SectionPath = category,
                Source = QuestionSource.TableCell,
                SchemaRef = new SchemaRef { Row = noLabel, Column = answerHeader },
                Binding = new Binding { Kind = "cell", Sheet = sheet.Name, Address = answerAddr },
                Confidence = Confidence.High,
            });
        }

        return GridSchema.Rebuild(new ExtractionResult { Questions = questions });   // synthesize schema
    }

    private static string Cap(string s) => s.Trim() is var t && t.Length <= 80 ? t : t[..80];

    /// <summary>Excel column letter -> 0-based index ("A"->0, "E"->4, "AA"->26). -1 for null/invalid.</summary>
    internal static int ColIndex(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return -1;
        int idx = 0;
        foreach (var ch in letter.Trim().ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z') return -1;
            idx = idx * 26 + (ch - 'A' + 1);
        }
        return idx - 1;
    }

    /// <summary>0-based column index -> Excel letter (0->"A", 4->"E").</summary>
    internal static string ColLetter(int index)
    {
        if (index < 0) return "?";
        string s = ""; int c = index + 1;
        while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
        return s;
    }

    private static string A1(int col, int row) => $"{ColLetter(col)}{row + 1}";
}
