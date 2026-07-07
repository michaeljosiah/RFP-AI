using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Reconciliation;

/// <summary>
/// Rebuilds a grid result's <see cref="DocumentSchema"/> from its questions, so the 1:1
/// answer_target invariant holds BY CONSTRUCTION. The grid model emits questions ONLY (spending its
/// whole output budget on them, not a redundant schema that doubles the tokens and truncates on a
/// colour-dense sheet); this synthesizes the matching schema — one data_entry table per sheet, one
/// cell per question — so a partial/truncated response can never orphan a schema target.
/// </summary>
public static class GridSchema
{
    public static ExtractionResult Rebuild(ExtractionResult r)
    {
        var sections = new List<Section>();
        var questions = new List<Question>(r.Questions.Count);

        foreach (var g in r.Questions.GroupBy(SheetOf))
        {
            var slug = Slug(g.Key);
            var sectionId = "grid-" + slug;
            var itemId = "grid-table-" + slug;

            var cells = new List<TableCell>();
            foreach (var q in g)
            {
                cells.Add(new TableCell
                {
                    AnswerTarget = q.AnswerTarget,
                    Row = q.SchemaRef.Row ?? "",
                    Column = q.SchemaRef.Column ?? "",
                    AnswerType = q.AnswerType,
                });
                // point the question's schema_ref at the synthesized item so the two stay linked;
                // a grid answer is always a table cell (the model may omit source).
                questions.Add(q with
                {
                    Source = QuestionSource.TableCell,
                    SchemaRef = q.SchemaRef with { SectionId = sectionId, ItemId = itemId },
                });
            }

            sections.Add(new Section
            {
                Id = sectionId,
                Name = g.Key,
                Items = { new Item { Id = itemId, Type = ItemType.Table, Verbatim = g.Key,
                    Table = new TableSpec { Classification = "data_entry", Cells = cells } } },
            });
        }

        return r with { DocumentSchema = new DocumentSchema { Sections = sections }, Questions = questions };
    }

    private static string SheetOf(Question q) =>
        !string.IsNullOrWhiteSpace(q.Binding?.Sheet) ? q.Binding!.Sheet!
        : !string.IsNullOrWhiteSpace(q.SectionPath) ? q.SectionPath
        : "Sheet";

    private static string Slug(string s)
    {
        var slug = new string((s ?? "").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return slug.Length == 0 ? "sheet" : slug;
    }
}
