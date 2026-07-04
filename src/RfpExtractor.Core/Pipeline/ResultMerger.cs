using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Pipeline;

/// <summary>
/// Concatenates per-page (or per-sheet) extraction results into one:
///  1. joins items the model flagged <c>truncated</c> (cut off at a page edge) with the
///     continuation at the top of the next page — otherwise a page-1 fragment surfaces as a
///     phantom question that never reconciles against the complete version (M&amp;G field finding);
///  2. renumbers answer_target (AT-####) and question ids (Q###) continuously so they stay
///     globally unique;
///  3. de-duplicates metadata by label.
/// </summary>
public static class ResultMerger
{
    public static ExtractionResult StitchPages(IReadOnlyList<ExtractionResult> pages)
    {
        if (pages.Count == 0) return new ExtractionResult();
        if (pages.Count == 1) return pages[0];

        // Mutable working copies (records are immutable; copy the lists so the inputs are untouched).
        var work = pages.Select(p => (
            Sections: p.DocumentSchema.Sections.Select(s => s with { Items = s.Items.ToList() }).ToList(),
            Questions: p.Questions.ToList())).ToList();

        JoinTruncatedItems(work);

        var sections = new List<Section>();
        var questions = new List<Question>();
        int at = 0, qn = 0;

        foreach (var (pageSections, pageQuestions) in work)
        {
            // assign fresh answer_target ids for everything on this page, in reading order
            var map = new Dictionary<string, string>();
            foreach (var s in pageSections)
            foreach (var it in s.Items)
            {
                if (it.Type == ItemType.Table && it.Table is not null)
                    foreach (var c in it.Table.Cells) map[c.AnswerTarget] = $"AT-{++at:D4}";
                else if (!string.IsNullOrEmpty(it.AnswerTarget))
                    map[it.AnswerTarget!] = $"AT-{++at:D4}";
            }

            foreach (var s in pageSections)
            {
                var items = new List<Item>();
                foreach (var it in s.Items)
                {
                    if (it.Type == ItemType.Table && it.Table is not null)
                        items.Add(it with { Table = it.Table with { Cells = it.Table.Cells.Select(c => c with { AnswerTarget = Remap(map, c.AnswerTarget) }).ToList() } });
                    else if (!string.IsNullOrEmpty(it.AnswerTarget))
                        items.Add(it with { AnswerTarget = Remap(map, it.AnswerTarget!) });
                    else items.Add(it);
                }
                sections.Add(s with { Items = items });
            }

            foreach (var q in pageQuestions)
                questions.Add(q with { QuestionId = $"Q{++qn:D3}", AnswerTarget = Remap(map, q.AnswerTarget) });
        }

        var meta = pages.SelectMany(p => p.DocumentSchema.Metadata)
            .GroupBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return new ExtractionResult
        {
            DocumentSchema = new DocumentSchema { Metadata = meta, Sections = sections },
            Questions = questions,
        };
    }

    /// <summary>If a page's last open question is <c>truncated</c>, fold it into the first open
    /// question of the next page (its continuation) and drop the fragment. When the continuation
    /// already contains the fragment's text (the model re-rendered the whole question on the next
    /// page) this is a pure de-duplication; otherwise the fragment's lead-in is prepended.</summary>
    private static void JoinTruncatedItems(List<(List<Section> Sections, List<Question> Questions)> work)
    {
        for (int i = 0; i < work.Count - 1; i++)
        {
            var (tailSection, tailIndex, tail) = LastOpen(work[i].Sections);
            if (tail is null || !tail.Truncated) continue;

            var (headSection, headIndex, head) = FirstOpen(work[i + 1].Sections);
            if (head is null) continue;

            var tailQ = work[i].Questions.FirstOrDefault(q => q.AnswerTarget == tail.AnswerTarget);
            var headQ = work[i + 1].Questions.FirstOrDefault(q => q.AnswerTarget == head.AnswerTarget);
            if (tailQ is null || headQ is null) continue;

            var tv = (tail.Verbatim ?? "").Trim();
            var hv = (head.Verbatim ?? "").Trim();
            bool subsumed = tv.Length == 0 || hv.Contains(tv, StringComparison.OrdinalIgnoreCase);

            // extend the continuation item + its question so the completed text drives reconciliation
            headSection!.Items[headIndex] = head with { Verbatim = subsumed ? hv : tv + "\n" + hv };

            var hqv = (headQ.VerbatimSource ?? "").Trim();
            bool qSubsumed = tv.Length == 0 || hqv.Contains(tv, StringComparison.OrdinalIgnoreCase);
            int qi = work[i + 1].Questions.IndexOf(headQ);
            work[i + 1].Questions[qi] = headQ with
            {
                VerbatimSource = qSubsumed ? hqv : tv + "\n" + hqv,
                SubQuestions = tailQ.SubQuestions.Concat(headQ.SubQuestions).Distinct().ToList(),
            };

            // drop the fragment (schema item + question) from the earlier page
            tailSection!.Items.RemoveAt(tailIndex);
            work[i].Questions.Remove(tailQ);
        }
    }

    private static (Section? Section, int Index, Item? Item) LastOpen(List<Section> sections)
    {
        for (int s = sections.Count - 1; s >= 0; s--)
            for (int i = sections[s].Items.Count - 1; i >= 0; i--)
                if (sections[s].Items[i].Type == ItemType.OpenQuestion)
                    return (sections[s], i, sections[s].Items[i]);
        return (null, -1, null);
    }

    private static (Section? Section, int Index, Item? Item) FirstOpen(List<Section> sections)
    {
        for (int s = 0; s < sections.Count; s++)
            for (int i = 0; i < sections[s].Items.Count; i++)
                if (sections[s].Items[i].Type == ItemType.OpenQuestion)
                    return (sections[s], i, sections[s].Items[i]);
        return (null, -1, null);
    }

    private static string Remap(Dictionary<string, string> map, string old) =>
        map.TryGetValue(old, out var v) ? v : old;
}
