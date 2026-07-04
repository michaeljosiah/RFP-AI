using System.Text.RegularExpressions;
using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Reconciliation;

/// <summary>
/// Deterministic tidy-ups applied to each leg's questions before reconciliation. Two rules:
///
///  1. Y/N checkbox-column bleed. A yes/no field on a form sits next to its Y and N answer columns;
///     when the text leg renders that row as markdown the column letters mash onto the printed
///     label ("Coalyn", "...Company Act of 1940yn", "UNGC controversies yn" — 5.5 run). The clean
///     printed label is what answer fill-back anchors on, so strip the trailing Y/N artifact. A
///     genuine parenthetical "(Y/N)" is preserved.
///
///  2. Table-cell verbatim that carries the whole markdown table block (M&amp;G run — the Open XML
///     markdown leaks into <c>verbatim_source</c>). An empty answer cell has no printed text of its
///     own; the faithful "verbatim" is the column/row header it sits under. Reconciliation matches
///     cells by row+column, so this is purely an output-quality improvement.
/// </summary>
public static class QuestionCleaner
{
    // Trailing Y/N answer-column letters: "yn", " yn", "y n", "y/n" at the very end of the label.
    private static readonly Regex CheckboxBleed =
        new(@"\s*y\s*/?\s*n\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ExtractionResult Clean(ExtractionResult r)
    {
        var changed = false;
        var questions = r.Questions.Select(q =>
        {
            var v = q.VerbatimSource ?? "";

            // 1) strip Y/N column bleed from yes/no labels — but keep a real parenthetical "(Y/N)".
            if (q.AnswerType == AnswerType.YesNo && !v.TrimEnd().EndsWith(")"))
            {
                var stripped = CheckboxBleed.Replace(v, "").TrimEnd();
                if (stripped.Length > 0 && stripped.Length < v.TrimEnd().Length)
                {
                    q = q with { VerbatimSource = stripped };
                    v = stripped;
                    changed = true;
                }
            }

            // 2) table-cell verbatim carrying the markdown block -> "{Column} / {Row}".
            if (q.Source == QuestionSource.TableCell && (v.Contains('|') || v.Contains('\n')))
            {
                var col = q.SchemaRef.Column?.Trim();
                var row = q.SchemaRef.Row?.Trim();
                var label = string.Join(" / ", new[] { col, row }.Where(x => !string.IsNullOrEmpty(x)));
                if (label.Length > 0)
                {
                    q = q with { VerbatimSource = label };
                    changed = true;
                }
            }

            return q;
        }).ToList();

        return changed ? r with { Questions = questions } : r;
    }
}
