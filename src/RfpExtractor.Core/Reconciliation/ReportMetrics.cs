using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Reconciliation;

/// <summary>
/// Populates the descriptive counts on the reconciliation report so both framings of "how many
/// questions" are visible side by side:
///  - <c>AnswerSlots</c> (= total questions): what a fill-back tool must fill. A document with two
///    parallel fund sections, a 3×3 AUM grid and a five-item enclosure list produces many slots.
///  - <c>UniqueQuestionTexts</c>: distinct question wording after deduping cross-section repeats —
///    the "how many genuinely distinct asks" view.
/// Plus a by-source breakdown, the data-entry-table count (grids, not cells), and the
/// applicant/internal split (internal-only sections are counted apart from what the responder fills).
/// </summary>
public static class ReportMetrics
{
    public static void Populate(ReconciliationReport report, ExtractionResult r)
    {
        // Tag applicant vs internal-only sections first, then count the two audiences separately.
        AudienceTagger.Tag(r);

        var qs = r.Questions;
        report.MergedCount = qs.Count;
        report.AnswerSlots = qs.Count;
        report.ApplicantSlots = qs.Count(q => q.Audience == Audience.Applicant);
        report.InternalSlots = qs.Count(q => q.Audience == Audience.Internal);
        report.BodyQuestions = qs.Count(q => q.Source == QuestionSource.Body);
        report.TableCells = qs.Count(q => q.Source == QuestionSource.TableCell);
        report.DocumentRequests = qs.Count(q => q.Source == QuestionSource.DocumentRequest);
        report.UniqueQuestionTexts = qs
            .Select(q => TextNormalizer.Key(q.QuestionText))
            .Where(k => k.Length > 0)
            .Distinct()
            .Count();
        report.PrintedQuestions = qs.Count;   // extraction is printed-level; AnswerSlots is bumped to the atomic total after decomposition
        report.DataEntryTables = r.DocumentSchema.Sections
            .SelectMany(s => s.Items)
            .Count(i => i.Type == ItemType.Table && i.Table is not null);
    }
}
