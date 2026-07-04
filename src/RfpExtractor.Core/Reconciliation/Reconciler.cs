using System.Text.RegularExpressions;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Reconciliation;

public static class TextNormalizer
{
    /// <summary>Alphanumeric squash: lowercase and drop everything else. Field finding (M&amp;G run):
    /// the document used a non-breaking hyphen ("net‑zero") that one leg read as "netzero" — any
    /// normalization that maps punctuation to spaces splits the two apart. At question length,
    /// full squashing cannot produce false positives between genuinely different questions.</summary>
    public static string Key(string s) =>
        Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "");
}

/// <summary>
/// One-to-one, ordered reconciliation of two extraction legs (field-tested on the M&amp;G
/// questionnaire run and rebuilt to fix what that run exposed):
///
///  1. Matching is ONE-TO-ONE via ordered queues — a document may legitimately repeat the same
///     question in different sections (e.g. per-fund repeats); each printed occurrence is its
///     own answer slot, so same-leg repeats are never collapsed and each secondary question can
///     consume at most one primary question (both legs emit in reading order, so FIFO pairing
///     aligns repeats correctly).
///  2. Match keys are VERBATIM-FIRST: table cells match on row+column; body questions match on
///     normalized verbatim_source (the printed text — stable across legs) and only then on
///     question_text (which each leg may rephrase). Leftovers go to an optional LLM fuzzy pass.
///  3. After matching, ALL answer targets are renumbered into one namespace and secondary-only
///     items are grafted into the merged schema — the two legs number their targets
///     independently, so a plain union produces target collisions and dangling schema refs.
/// </summary>
public sealed class Reconciler : IReconciler
{
    private const int FuzzyCap = 120;   // per-side cap on items sent to the LLM matcher
    private readonly IFuzzyMatcher? _fuzzy;

    public Reconciler(IFuzzyMatcher? fuzzy = null) => _fuzzy = fuzzy;

    public async Task<ReconciledResult> ReconcileAsync(ExtractionResult primary, ExtractionResult secondary,
        IReadOnlyList<TableStructure> groundTruth, bool fuzzyMatch, CancellationToken ct)
    {
        var pQs = primary.Questions;
        var report = new ReconciliationReport { PrimaryCount = pQs.Count, SecondaryCount = secondary.Questions.Count };
        var matched = new bool[pQs.Count];
        var mergedSubs = new List<string>?[pQs.Count];

        // ---- deterministic phases ----
        // 1) real data-entry cells match by coordinate (row|column) - restricted to cells.
        // 2+3) verbatim then normalized text, across ALL sources: a form field that the text leg
        //      tags "table_cell" (it sees the form as one big table) and vision tags "body" is the
        //      SAME answer slot. Source type must NOT partition matching (field finding on the Fund
        //      DD form — 19 identical fields duplicated because cells and body were never compared).
        var pending = secondary.Questions.ToList();
        pending = MatchPhase(pending, pQs, matched, mergedSubs, q => q.Source == QuestionSource.TableCell, CellKey);
        pending = MatchPhase(pending, pQs, matched, mergedSubs, eligible: null, q => TextNormalizer.Key(q.VerbatimSource));
        pending = MatchPhase(pending, pQs, matched, mergedSubs, eligible: null, q => TextNormalizer.Key(q.QuestionText));

        // ---- optional LLM fuzzy pass over the leftovers (paraphrase duplicates) ----
        if (fuzzyMatch && _fuzzy is not null && pending.Count > 0 && Array.IndexOf(matched, false) >= 0)
        {
            var leftIdx = Enumerable.Range(0, pQs.Count).Where(i => !matched[i]).ToList();
            if (leftIdx.Count <= FuzzyCap && pending.Count <= FuzzyCap)
            {
                try
                {
                    var pairs = await _fuzzy.MatchAsync(leftIdx.Select(i => pQs[i]).ToList(), pending, ct);
                    var pById = new Dictionary<string, int>();
                    foreach (var i in leftIdx) pById.TryAdd(pQs[i].QuestionId, i);
                    var sById = new Dictionary<string, Question>();
                    foreach (var s in pending) sById.TryAdd(s.QuestionId, s);

                    foreach (var pair in pairs)
                    {
                        if (pById.TryGetValue(pair.Primary, out var pi) && !matched[pi] &&
                            sById.TryGetValue(pair.Secondary, out var sv) && pending.Remove(sv))
                        {
                            matched[pi] = true;
                            mergedSubs[pi] = pQs[pi].SubQuestions.Union(sv.SubQuestions).ToList();
                        }
                    }
                }
                catch (Exception ex) { report.Warnings.Add("Fuzzy reconcile failed (deterministic result kept): " + ex.Message); }
            }
            else report.Warnings.Add($"Fuzzy reconcile skipped: {leftIdx.Count}+{pending.Count} unmatched items exceed the {FuzzyCap}-per-side cap.");
        }

        // ---- renumber everything into ONE target namespace (legs number independently) ----
        var final = new List<Question>();
        var mapPrimary = new Dictionary<string, string>();
        int n = 0;
        string NextAt() => $"AT-{++n:D4}";

        for (int i = 0; i < pQs.Count; i++)
        {
            var q = pQs[i];
            var at = NextAt();
            if (!string.IsNullOrEmpty(q.AnswerTarget) && !mapPrimary.TryAdd(q.AnswerTarget, at))
                report.Warnings.Add($"Primary leg reused answer_target {q.AnswerTarget}; renumbered uniquely.");
            final.Add(q with
            {
                QuestionId = $"Q{n:D3}",
                AnswerTarget = at,
                FoundBy = matched[i] ? FoundBy.Both : FoundBy.Text,
                Confidence = matched[i] ? Confidence.High
                           : q.Source == QuestionSource.TableCell ? Confidence.Medium : Confidence.Low,
                NeedsReview = !matched[i],
                SubQuestions = mergedSubs[i] ?? q.SubQuestions,
            });
        }

        var schema = RemapSchema(primary.DocumentSchema, mapPrimary);

        // ---- graft secondary-only questions + their schema items into the merged schema ----
        var graftSections = new Dictionary<string, Section>();
        var groups = pending
            .Select((q, order) => (q, order))
            .GroupBy(x => (x.q.SchemaRef.SectionId, x.q.SchemaRef.ItemId))
            .OrderBy(g => g.Min(x => x.order));

        foreach (var g in groups)
        {
            var (secId, itemId) = g.Key;
            var srcSection = secondary.DocumentSchema.Sections.FirstOrDefault(s => s.Id == secId);
            var srcItem = srcSection?.Items.FirstOrDefault(i => i.Id == itemId);

            var sectionKey = "vision-" + (string.IsNullOrEmpty(secId) ? "misc" : secId);
            if (!graftSections.TryGetValue(sectionKey, out var target))
            {
                target = new Section
                {
                    Id = sectionKey,
                    Name = srcSection?.Name ?? g.First().q.SectionPath,
                    Page = srcSection?.Page,
                };
                graftSections[sectionKey] = target;
                schema.Sections.Add(target);
            }

            Question Adjust(Question q, string at, string newItemId) => q with
            {
                QuestionId = $"Q{n:D3}",
                AnswerTarget = at,
                FoundBy = FoundBy.Vision,
                Confidence = q.Source == QuestionSource.TableCell ? Confidence.Medium : Confidence.Low,
                NeedsReview = true,
                SchemaRef = q.SchemaRef with { SectionId = sectionKey, ItemId = newItemId },
            };

            if (srcItem is { Type: ItemType.Table, Table: not null })
            {
                var cells = new List<TableCell>();
                foreach (var (q, _) in g.OrderBy(x => x.order))
                {
                    var at = NextAt();
                    cells.Add(new TableCell { AnswerTarget = at, Row = q.SchemaRef.Row ?? "", Column = q.SchemaRef.Column ?? "", AnswerType = q.AnswerType });
                    final.Add(Adjust(q, at, srcItem.Id));
                }
                target.Items.Add(srcItem with { Table = srcItem.Table with { Cells = cells } });
            }
            else
            {
                foreach (var (q, _) in g.OrderBy(x => x.order))
                {
                    var at = NextAt();
                    var newItemId = (srcItem?.Id ?? "item") + "-" + at;
                    target.Items.Add(new Item
                    {
                        Id = newItemId,
                        Type = srcItem?.Type ?? (q.Source == QuestionSource.DocumentRequest ? ItemType.DocumentRequest : ItemType.OpenQuestion),
                        Verbatim = srcItem?.Verbatim ?? q.VerbatimSource,
                        AnswerTarget = at,
                        AnswerType = q.AnswerType,
                    });
                    final.Add(Adjust(q, at, newItemId));
                }
            }
        }

        // ---- cross-check + report ----
        // Only meaningful when the merged result actually models a fillable grid. A form whose rows
        // both legs (correctly) flattened to body questions has no cells to "undercount" — the raw
        // layout table in groundTruth is not a data-entry grid. Gating on a detected data-entry table
        // item avoids the false "found 0, grid implies ~12" warning on flattened forms (Fund-DD).
        var hasDataEntryGrid = schema.Sections
            .SelectMany(s => s.Items)
            .Any(i => i.Type == ItemType.Table && i.Table is not null);
        var cellCount = final.Count(q => q.Source == QuestionSource.TableCell);
        var expected = groundTruth.Sum(t => Math.Max(0, (t.Rows - 1) * (t.Columns - 1)));
        if (hasDataEntryGrid && expected > 0 && cellCount < expected)
            report.Warnings.Add($"Table cell undercount: found {cellCount}, grid implies ~{expected}.");

        report.MergedCount = final.Count;
        report.AgreedCount = final.Count(q => q.FoundBy == FoundBy.Both);
        report.PrimaryOnlyCount = final.Count(q => q.FoundBy == FoundBy.Text);
        report.SecondaryOnlyCount = final.Count(q => q.FoundBy == FoundBy.Vision);

        // Reconciliation-quality guard. When both legs ran but agreed on only a small fraction of
        // what each found, the merged list is likely inflated with cross-leg DUPLICATES the matcher
        // missed — the common failure mode on born-digital, TABLE-HEAVY documents: the vision leg
        // re-derives the same grids but labels the cells differently, so the coordinate/text keys
        // don't line up (and the paraphrase fuzzy pass only covers body questions, not grid cells).
        // The merged count is then NOT trustworthy; the single-leg (text) count usually is. Surface
        // it loudly rather than let an inflated headline stand.
        var smallerLeg = Math.Min(report.PrimaryCount, report.SecondaryCount);
        if (smallerLeg >= 20 && report.AgreedCount < 0.6 * smallerLeg)
            report.Warnings.Add(
                $"Low reconciliation match rate: only {report.AgreedCount} of ~{smallerLeg} questions matched across " +
                $"the two legs, so the merged list ({final.Count}) likely contains cross-leg duplicates (e.g. the same " +
                $"table grids re-derived by each leg). For born-digital documents (real text layer) prefer " +
                $"--strategy=text; reserve vision/both for scanned or image-only files.");

        return new ReconciledResult
        {
            Merged = new ExtractionResult { DocumentSchema = schema, Questions = final },
            ReviewQueue = final.Where(q => q.NeedsReview).ToList(),
            Report = report,
        };
    }

    /// <summary>One matching phase: FIFO queues of unmatched primary questions per key; each
    /// secondary question consumes at most one. Ordered pairing keeps per-section repeats aligned.
    /// <paramref name="eligible"/> restricts which questions take part (e.g. the coordinate phase is
    /// cells only); null means every source participates.</summary>
    private static List<Question> MatchPhase(List<Question> pending, IReadOnlyList<Question> pQs,
        bool[] matched, List<string>?[] subs, Func<Question, bool>? eligible, Func<Question, string> keyOf)
    {
        var queues = new Dictionary<string, Queue<int>>();
        for (int i = 0; i < pQs.Count; i++)
        {
            if (matched[i]) continue;
            var q = pQs[i];
            if (eligible != null && !eligible(q)) continue;
            var key = keyOf(q);
            if (key.Length == 0) continue;
            if (!queues.TryGetValue(key, out var qu)) queues[key] = qu = new Queue<int>();
            qu.Enqueue(i);
        }

        var still = new List<Question>();
        foreach (var v in pending)
        {
            if (eligible != null && !eligible(v)) { still.Add(v); continue; }
            var key = keyOf(v);
            if (key.Length > 0 && queues.TryGetValue(key, out var qu) && qu.Count > 0)
            {
                var i = qu.Dequeue();
                matched[i] = true;
                subs[i] = pQs[i].SubQuestions.Union(v.SubQuestions).ToList();
            }
            else still.Add(v);
        }
        return still;
    }

    private static string CellKey(Question q) =>
        TextNormalizer.Key(q.SchemaRef.Row ?? "") + "|" + TextNormalizer.Key(q.SchemaRef.Column ?? "");

    private static DocumentSchema RemapSchema(DocumentSchema s, Dictionary<string, string> map)
    {
        return new DocumentSchema
        {
            Metadata = s.Metadata.ToList(),
            Notes = s.Notes,
            Sections = s.Sections.Select(sec => sec with
            {
                Items = sec.Items.Select(it =>
                    it is { Type: ItemType.Table, Table: not null }
                        ? it with
                        {
                            Table = it.Table with
                            {
                                Cells = it.Table.Cells
                                    .Select(c => c with { AnswerTarget = map.GetValueOrDefault(c.AnswerTarget, c.AnswerTarget) })
                                    .ToList(),
                            },
                        }
                        : it with { AnswerTarget = it.AnswerTarget is { Length: > 0 } t ? map.GetValueOrDefault(t, t) : it.AnswerTarget }
                ).ToList(),
            }).ToList(),
        };
    }
}
