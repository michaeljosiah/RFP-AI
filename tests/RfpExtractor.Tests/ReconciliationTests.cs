using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Pipeline;
using RfpExtractor.Core.Reconciliation;
using RfpExtractor.Core.Validation;
using Xunit;

namespace RfpExtractor.Tests;

public class InvariantTests
{
    [Fact]
    public void Valid_result_has_no_errors()
    {
        var r = Build(("AT-1", "Q1"), ("AT-2", "Q2"));
        Assert.Empty(InvariantValidator.Validate(r));
    }

    [Fact]
    public void Question_referencing_missing_target_is_flagged()
    {
        var r = new ExtractionResult
        {
            DocumentSchema = new DocumentSchema
            {
                Sections = { new Section { Id = "s", Name = "S", Items = { Open("AT-1") } } }
            },
            Questions = { Q("Q1", "AT-2") }   // AT-2 not in schema
        };
        var errors = InvariantValidator.Validate(r);
        Assert.Contains(errors, e => e.Contains("AT-2"));
    }

    [Fact]
    public void Schema_target_without_question_is_flagged()
    {
        var r = new ExtractionResult
        {
            DocumentSchema = new DocumentSchema
            {
                Sections = { new Section { Id = "s", Name = "S", Items = { Open("AT-1"), Open("AT-2") } } }
            },
            Questions = { Q("Q1", "AT-1") }   // AT-2 has no question
        };
        var errors = InvariantValidator.Validate(r);
        Assert.Contains(errors, e => e.Contains("AT-2") && e.Contains("no question"));
    }

    [Fact]
    public void Duplicate_question_target_is_flagged()
    {
        var r = new ExtractionResult
        {
            DocumentSchema = new DocumentSchema
            {
                Sections = { new Section { Id = "s", Name = "S", Items = { Open("AT-1") } } }
            },
            Questions = { Q("Q1", "AT-1"), Q("Q2", "AT-1") }
        };
        Assert.Contains(InvariantValidator.Validate(r), e => e.Contains("Duplicate question target"));
    }

    [Fact]
    public void Table_cells_are_counted_as_schema_targets()
    {
        var table = new Item
        {
            Id = "t", Type = ItemType.Table,
            Table = new TableSpec
            {
                Cells =
                {
                    new TableCell { AnswerTarget = "AT-1", Row = "Current", Column = "Firm AUM" },
                    new TableCell { AnswerTarget = "AT-2", Row = "1yr", Column = "Firm AUM" },
                }
            }
        };
        var r = new ExtractionResult
        {
            DocumentSchema = new DocumentSchema { Sections = { new Section { Id = "s", Name = "S", Items = { table } } } },
            Questions = { Q("Q1", "AT-1"), Q("Q2", "AT-2") }
        };
        Assert.Empty(InvariantValidator.Validate(r));
    }

    private static ExtractionResult Build(params (string at, string qid)[] pairs)
    {
        var section = new Section { Id = "s", Name = "S" };
        var questions = new List<Question>();
        foreach (var (at, qid) in pairs)
        {
            section.Items.Add(Open(at));
            questions.Add(Q(qid, at));
        }
        return new ExtractionResult
        {
            DocumentSchema = new DocumentSchema { Sections = { section } },
            Questions = questions
        };
    }

    private static Item Open(string at) => new()
    {
        Id = at, Type = ItemType.OpenQuestion, Verbatim = "v", AnswerTarget = at
    };

    private static Question Q(string qid, string at) => new()
    {
        QuestionId = qid, AnswerTarget = at, QuestionText = "q " + qid,
        VerbatimSource = "v", SectionPath = "S", Source = QuestionSource.Body,
        SchemaRef = new SchemaRef { SectionId = "s", ItemId = at }
    };
}

/// <summary>Cases distilled from the real M&amp;G questionnaire run that exposed the original defects.</summary>
public class ReconcilerTests
{
    private static Task<ReconciledResult> Run(ExtractionResult p, ExtractionResult s, IFuzzyMatcher? fuzzy = null) =>
        new Reconciler(fuzzy).ReconcileAsync(p, s, Array.Empty<TableStructure>(), fuzzyMatch: fuzzy != null, CancellationToken.None);

    [Fact]
    public async Task Verbatim_match_merges_despite_different_phrasing()
    {
        // M&G defect 2: legs rephrase question_text but verbatim_source is identical.
        var p = Result(Body("p1", "AT-0003", "Please provide breakdown of total assets of the firm by fund/strategy.",
                            "Please provide breakdown of total assets of the firm by fund/strategy"));
        var s = Result(Body("s1", "AT-0007", "Provide a breakdown of total assets of the firm by fund/strategy.",
                            "Please provide breakdown of total assets of the firm by fund/strategy"));

        var r = await Run(p, s);

        Assert.Single(r.Merged.Questions);
        Assert.Equal(FoundBy.Both, r.Merged.Questions[0].FoundBy);
        Assert.Equal(Confidence.High, r.Merged.Questions[0].Confidence);
        Assert.Empty(r.ReviewQueue);
    }

    [Fact]
    public async Task Hyphen_variants_merge_deterministically()
    {
        // M&G field finding: the doc used a non-breaking hyphen ("net‑zero") which one leg read
        // as "netzero" — space-based normalization split the pair; alphanumeric squash merges it.
        var p = Result(Body("p1", "AT-1", "Do you have a net‑zero target?", "Do you have a net‑zero or science‑based target strategy?"));
        var s = Result(Body("s1", "AT-9", "Do you have a netzero target?", "Do you have a netzero or sciencebased target strategy?"));

        var r = await Run(p, s);

        Assert.Single(r.Merged.Questions);
        Assert.Equal(FoundBy.Both, r.Merged.Questions[0].FoundBy);
    }

    [Fact]
    public async Task Repeated_question_across_sections_is_preserved_and_pairwise_merged()
    {
        // M&G defect 1: the document repeats a question in PRU + Main Fund sections — two slots.
        var p = Result(
            Body("p1", "AT-1", "Outline the liquidity profile of the fund.", "Outline the liquidity profile of the fund?", "PRU"),
            Body("p2", "AT-2", "Outline the liquidity profile of the fund.", "Outline the liquidity profile of the fund?", "Main Fund"));
        var s = Result(
            Body("s1", "AT-1", "Outline the liquidity profile of the fund.", "Outline the liquidity profile of the fund?", "PRU"),
            Body("s2", "AT-2", "Outline the liquidity profile of the fund.", "Outline the liquidity profile of the fund?", "Main Fund"));

        var r = await Run(p, s);

        Assert.Equal(2, r.Merged.Questions.Count);                       // both slots kept
        Assert.All(r.Merged.Questions, q => Assert.Equal(FoundBy.Both, q.FoundBy));
    }

    [Fact]
    public async Task Same_leg_repeat_without_counterpart_stays_single_source()
    {
        // Two printed occurrences in the text leg, only one seen by vision:
        // exactly one may upgrade to Both; the other must remain text-only (not collapse).
        var p = Result(
            Body("p1", "AT-1", "Outline the fee schedule.", "Outline the fee schedule.", "PRU"),
            Body("p2", "AT-2", "Outline the fee schedule.", "Outline the fee schedule.", "Main Fund"));
        var s = Result(
            Body("s1", "AT-9", "Outline the fee schedule.", "Outline the fee schedule.", "PRU"));

        var r = await Run(p, s);

        Assert.Equal(2, r.Merged.Questions.Count);
        Assert.Equal(1, r.Merged.Questions.Count(q => q.FoundBy == FoundBy.Both));
        Assert.Equal(1, r.Merged.Questions.Count(q => q.FoundBy == FoundBy.Text && q.NeedsReview));
    }

    [Fact]
    public async Task Vision_only_questions_are_grafted_renumbered_and_invariant_holds()
    {
        // M&G defect 3: both legs number AT-0001.. independently -> collisions + dangling refs.
        var p = WithSchema(Result(Body("p1", "AT-0001", "Text question one?", "Text question one?")));
        var sTable = new Item
        {
            Id = "item-11", Type = ItemType.Table, Verbatim = "AUM grid",
            Table = new TableSpec
            {
                ColumnHeaders = { "Firm AUM" }, RowHeaders = { "Current" },
                Cells = { new TableCell { AnswerTarget = "AT-0001", Row = "Current", Column = "Firm AUM", AnswerType = AnswerType.Currency } }
            }
        };
        var s = new ExtractionResult
        {
            DocumentSchema = new DocumentSchema { Sections = { new Section { Id = "sec-2", Name = "Business", Items = { sTable } } } },
            Questions =
            {
                Cell("s1", "AT-0001", "What is the current Firm AUM?", "Current", "Firm AUM") // SAME target id as text leg
            }
        };

        var r = await Run(p, s);

        Assert.Equal(2, r.Merged.Questions.Count);
        Assert.Equal(r.Merged.Questions.Count, r.Merged.Questions.Select(q => q.AnswerTarget).Distinct().Count()); // no collisions
        Assert.Empty(InvariantValidator.Validate(r.Merged));             // grafted schema restores 1:1
        var grafted = r.Merged.Questions.Single(q => q.FoundBy == FoundBy.Vision);
        Assert.StartsWith("vision-", grafted.SchemaRef.SectionId);
    }

    [Fact]
    public async Task Cross_source_field_merges_when_one_leg_says_cell_and_other_says_body()
    {
        // Fund DD form finding: the text leg sees the whole form as a table (every field a
        // table_cell) while vision tags fields as body. Identical verbatim must still merge.
        var textLeg = Result(Cell("t1", "AT-1", "Is short-selling allowed?", "", "Short-Selling"));
        var visionLeg = Result(Body("v1", "AT-9", "Is there Short-Selling?", "Short-Selling"));

        var r = await Run(textLeg, visionLeg);

        Assert.Single(r.Merged.Questions);                       // one slot, not two
        Assert.Equal(FoundBy.Both, r.Merged.Questions[0].FoundBy);
        Assert.Equal(QuestionSource.TableCell, r.Merged.Questions[0].Source);   // primary's classification kept
    }

    [Fact]
    public async Task Distinct_cells_still_kept_separate_across_sources()
    {
        // Guard: opening cross-source matching must not collapse genuinely different fields.
        var textLeg = Result(
            Cell("t1", "AT-1", "Max % TNA borrowed?", "", "Max % TNA borrowed"),
            Cell("t2", "AT-2", "Max % TNA lended?", "", "Max % TNA lended"));
        var visionLeg = Result(Body("v1", "AT-9", "Max % TNA borrowed?", "Max % TNA borrowed"));

        var r = await Run(textLeg, visionLeg);

        Assert.Equal(2, r.Merged.Questions.Count);               // borrowed merges, lended stays
        Assert.Equal(1, r.Merged.Questions.Count(q => q.FoundBy == FoundBy.Both));
    }

    [Fact]
    public async Task Table_cells_match_by_row_and_column_in_order()
    {
        var p = Result(Cell("p1", "AT-1", "What is the firm's AUM currently?", "Current", "Firm AUM"));
        var s = Result(Cell("s1", "AT-7", "Firm AUM now?", "current", "firm aum"));
        var r = await Run(p, s);

        Assert.Single(r.Merged.Questions);
        Assert.Equal(FoundBy.Both, r.Merged.Questions[0].FoundBy);
    }

    [Fact]
    public async Task Fuzzy_matcher_pairs_paraphrase_leftovers()
    {
        var p = Result(Body("p1", "AT-1", "Please outline the nature of the derivatives used.",
                            "Derivatives: Please outline the nature of the derivatives used"));
        var s = Result(Body("s1", "AT-9", "Outline the nature of the derivatives used.",
                            "Please outline the nature of the derivatives used"));
        var fuzzy = new StubMatcher(("p1", "s1"));

        var r = await Run(p, s, fuzzy);

        Assert.Single(r.Merged.Questions);
        Assert.Equal(FoundBy.Both, r.Merged.Questions[0].FoundBy);
        Assert.True(fuzzy.Called);
    }

    [Fact]
    public async Task Fuzzy_matcher_failure_keeps_deterministic_result_with_warning()
    {
        var p = Result(Body("p1", "AT-1", "Question A?", "Question A?"));
        var s = Result(Body("s1", "AT-9", "Totally different question?", "Totally different question?"));
        var r = await Run(p, s, new ThrowingMatcher());

        Assert.Equal(2, r.Merged.Questions.Count);
        Assert.Contains(r.Report.Warnings, w => w.Contains("Fuzzy reconcile failed"));
    }

    [Fact]
    public void ReportMetrics_report_both_framings_and_source_breakdown()
    {
        // Two sections repeat the same body question (per-fund slots) + a 2-cell grid + 1 upload.
        var grid = new Item
        {
            Id = "t", Type = ItemType.Table,
            Table = new TableSpec { Cells = {
                new TableCell { AnswerTarget = "AT-1", Row = "Current", Column = "Firm AUM" },
                new TableCell { AnswerTarget = "AT-2", Row = "1yr", Column = "Firm AUM" } } }
        };
        var r = new ExtractionResult
        {
            DocumentSchema = new DocumentSchema { Sections = { new Section { Id = "s", Name = "S", Items = { grid } } } },
            Questions =
            {
                Cell("c1", "AT-1", "What is the current firm AUM?", "Current", "Firm AUM"),
                Cell("c2", "AT-2", "What was the firm AUM 1 year ago?", "1yr", "Firm AUM"),
                Body("b1", "AT-3", "Outline the liquidity profile of the fund.", "Outline the liquidity profile of the fund?", "PRU"),
                Body("b2", "AT-4", "Outline the liquidity profile of the fund.", "Outline the liquidity profile of the fund?", "Main Fund"),
                new Question { QuestionId = "d1", AnswerTarget = "AT-5", QuestionText = "Please enclose the factsheet.",
                    VerbatimSource = "Factsheet", Source = QuestionSource.DocumentRequest, SectionPath = "Data",
                    SchemaRef = new SchemaRef { SectionId = "s", ItemId = "d" } }
            }
        };

        var report = new ReconciliationReport();
        ReportMetrics.Populate(report, r);

        Assert.Equal(5, report.AnswerSlots);           // 5 slots to fill
        Assert.Equal(4, report.UniqueQuestionTexts);   // the two "liquidity profile" repeats collapse to 1
        Assert.Equal(2, report.BodyQuestions);
        Assert.Equal(2, report.TableCells);
        Assert.Equal(1, report.DataEntryTables);       // 2 cells -> 1 grid
        Assert.Equal(1, report.DocumentRequests);
    }

    [Fact]
    public void Cleaner_replaces_markdown_cell_verbatim_with_header_labels()
    {
        // M&G run: table-cell verbatim_source carried the whole markdown block.
        var r = new ExtractionResult
        {
            Questions =
            {
                new Question
                {
                    QuestionId = "Q1", AnswerTarget = "AT-1", QuestionText = "What is the current firm AUM?",
                    VerbatimSource = "|  | Firm AUM | Strategy Assets |\n| --- | --- | --- |\n| Current |  |  |",
                    Source = QuestionSource.TableCell, SectionPath = "Business",
                    SchemaRef = new SchemaRef { SectionId = "s", ItemId = "t", Row = "Current", Column = "Firm AUM" }
                },
                new Question   // body question is untouched
                {
                    QuestionId = "Q2", AnswerTarget = "AT-2", QuestionText = "Describe the business.",
                    VerbatimSource = "Describe the business.", Source = QuestionSource.Body, SectionPath = "Business",
                    SchemaRef = new SchemaRef { SectionId = "s", ItemId = "b" }
                }
            }
        };

        var cleaned = QuestionCleaner.Clean(r);

        Assert.Equal("Firm AUM / Current", cleaned.Questions[0].VerbatimSource);
        Assert.Equal("Describe the business.", cleaned.Questions[1].VerbatimSource);
    }

    [Fact]
    public async Task Undercount_warning_emitted_when_a_detected_grid_is_under_populated()
    {
        // A real data-entry grid IS in the merged schema but the legs under-filled its cells.
        var grid = new Item
        {
            Id = "t", Type = ItemType.Table, Verbatim = "AUM grid",
            Table = new TableSpec { Cells = { new TableCell { AnswerTarget = "AT-1", Row = "Current", Column = "Firm AUM" } } }
        };
        var p = new ExtractionResult
        {
            DocumentSchema = new DocumentSchema { Sections = { new Section { Id = "s", Name = "S", Items = { grid } } } },
            Questions = { Cell("p1", "AT-1", "q", "Current", "Firm AUM") }
        };
        var gt = new[] { new TableStructure(0, 3, 3, 0) };   // implies 4 data cells, only 1 found
        var r = await new Reconciler().ReconcileAsync(p, new ExtractionResult(), gt, false, CancellationToken.None);
        Assert.Contains(r.Report.Warnings, w => w.Contains("undercount"));
    }

    [Fact]
    public async Task Undercount_warning_suppressed_when_form_flattened_to_body()
    {
        // Fund-DD false positive: the raw doc is one big LAYOUT table (in groundTruth), but both legs
        // correctly flattened its rows to body questions. No data-entry grid exists to undercount, so
        // "found 0, grid implies ~12" must NOT fire.
        var p = WithSchema(Result(Body("p1", "AT-1", "Provide the Fund Name.", "Fund Name")));
        var gt = new[] { new TableStructure(0, 13, 2, 0) };  // a 13x2 layout table -> would imply 12 cells
        var r = await new Reconciler().ReconcileAsync(p, new ExtractionResult(), gt, false, CancellationToken.None);
        Assert.DoesNotContain(r.Report.Warnings, w => w.Contains("undercount"));
    }

    [Fact]
    public void Cleaner_strips_yn_checkbox_bleed_from_yes_no_labels()
    {
        // 5.5 run: the Y/N answer columns mashed onto yes/no field labels in verbatim_source, which
        // is the fill-back anchor. Strip the trailing artifact so it locates the right cell.
        var r = new ExtractionResult
        {
            Questions =
            {
                YesNo("Q1", "AT-1", "Is this a Closed-End Fund?", "Closed-End Fundyn"),
                YesNo("Q2", "AT-2", "Are UNGC controversies excluded?", "UNGC controversies yn"),
                YesNo("Q3", "AT-3", "Under Law: Company Act of 1940?", "Under Law: Company Act of 1940yn"),
            }
        };

        var cleaned = QuestionCleaner.Clean(r);

        Assert.Equal("Closed-End Fund", cleaned.Questions[0].VerbatimSource);
        Assert.Equal("UNGC controversies", cleaned.Questions[1].VerbatimSource);
        Assert.Equal("Under Law: Company Act of 1940", cleaned.Questions[2].VerbatimSource);
    }

    [Fact]
    public void Cleaner_preserves_real_parenthetical_yn_and_non_yesno_labels()
    {
        var r = new ExtractionResult
        {
            Questions =
            {
                YesNo("Q1", "AT-1", "Institutional share class?", "Institutional Share Class (Y/N)"),  // real printed (Y/N)
                Body("b1", "AT-2", "Provide the Fund Currency.", "Fund Currency"),                      // not yes_no -> untouched
            }
        };

        var cleaned = QuestionCleaner.Clean(r);

        Assert.Equal("Institutional Share Class (Y/N)", cleaned.Questions[0].VerbatimSource);
        Assert.Equal("Fund Currency", cleaned.Questions[1].VerbatimSource);
    }

    [Fact]
    public void Audience_tagger_flags_internal_only_sections_and_leaves_applicant()
    {
        // BGFML SECTION ONLY* is completed by the receiving firm, not the responder.
        var r = new ExtractionResult
        {
            DocumentSchema = new DocumentSchema { Sections = {
                new Section { Id = "s1", Name = "Fund Details" },
                new Section { Id = "s2", Name = "BGFML SECTION ONLY*" } } },
            Questions =
            {
                Body("q1", "AT-1", "Provide the fund name.", "Fund Name", "Fund Details"),
                Body("q2", "AT-2", "Provide the note.", "Note", "BGFML SECTION ONLY*"),
            }
        };

        AudienceTagger.Tag(r);

        Assert.Equal(Audience.Applicant, r.Questions[0].Audience);
        Assert.Equal(Audience.Internal, r.Questions[1].Audience);
        Assert.Equal(Audience.Applicant, r.DocumentSchema.Sections[0].Audience);   // schema tagged too
        Assert.Equal(Audience.Internal, r.DocumentSchema.Sections[1].Audience);
    }

    [Theory]
    [InlineData("BGFML SECTION ONLY*", true)]
    [InlineData("For internal use only", true)]
    [InlineData("Office use only", true)]
    [InlineData("To be completed by the Manager", true)]
    [InlineData("Fund Details", false)]
    [InlineData("Investment & Legal Details", false)]
    [InlineData("Documentation required", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Audience_tagger_recognizes_internal_headings(string? heading, bool internalExpected)
        => Assert.Equal(internalExpected, AudienceTagger.IsInternal(heading));

    [Fact]
    public void ReportMetrics_split_applicant_and_internal_slots()
    {
        var r = new ExtractionResult
        {
            Questions =
            {
                Body("b1", "AT-1", "Provide the fund name.", "Fund Name", "Fund Details"),
                Body("b2", "AT-2", "Provide the note.", "Note", "BGFML SECTION ONLY*"),
                Body("b3", "AT-3", "Provide the date.", "Date", "BGFML SECTION ONLY*"),
            }
        };

        var report = new ReconciliationReport();
        ReportMetrics.Populate(report, r);

        Assert.Equal(3, report.AnswerSlots);
        Assert.Equal(1, report.ApplicantSlots);
        Assert.Equal(2, report.InternalSlots);
    }

    // ---------- helpers ----------

    private sealed class StubMatcher(params (string p, string s)[] pairs) : IFuzzyMatcher
    {
        public bool Called;
        public Task<IReadOnlyList<MatchPair>> MatchAsync(IReadOnlyList<Question> primary, IReadOnlyList<Question> secondary, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult<IReadOnlyList<MatchPair>>(
                pairs.Select(x => new MatchPair { Primary = x.p, Secondary = x.s }).ToList());
        }
    }

    private sealed class ThrowingMatcher : IFuzzyMatcher
    {
        public Task<IReadOnlyList<MatchPair>> MatchAsync(IReadOnlyList<Question> primary, IReadOnlyList<Question> secondary, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Low_cross_leg_match_rate_warns_about_likely_duplicates()
    {
        // Born-digital, table-heavy failure mode (EQDP --strategy=both): each leg finds ~the same
        // questions but labels them differently, so few reconcile and the merge is duplicate-heavy.
        var p = WithSchema(Result(Enumerable.Range(0, 25)
            .Select(i => Body($"p{i}", $"AT-P{i}", $"primary question {i}", $"pverb {i}")).ToArray()));
        var s = Result(Enumerable.Range(0, 25)
            .Select(i => Body($"s{i}", $"AT-S{i}", $"secondary question {i}", $"sverb {i}")).ToArray());

        var r = await Run(p, s);

        Assert.Equal(0, r.Report.AgreedCount);
        Assert.Equal(50, r.Report.MergedCount);                       // nothing merged -> duplicated
        Assert.Contains(r.Report.Warnings, w => w.Contains("Low reconciliation match rate") && w.Contains("--strategy=text"));
    }

    [Fact]
    public async Task Healthy_match_rate_does_not_warn()
    {
        // Both legs see the same 25 printed questions (identical verbatim) -> all match, no warning.
        var p = WithSchema(Result(Enumerable.Range(0, 25)
            .Select(i => Body($"p{i}", $"AT-P{i}", $"question {i}", $"verb {i}")).ToArray()));
        var s = Result(Enumerable.Range(0, 25)
            .Select(i => Body($"s{i}", $"AT-S{i}", $"reworded {i}", $"verb {i}")).ToArray());

        var r = await Run(p, s);

        Assert.Equal(25, r.Report.AgreedCount);
        Assert.DoesNotContain(r.Report.Warnings, w => w.Contains("Low reconciliation match rate"));
    }

    private static ExtractionResult Result(params Question[] qs) => new() { Questions = qs.ToList() };

    /// <summary>Adds a minimal schema so the invariant can hold for the primary questions.</summary>
    private static ExtractionResult WithSchema(ExtractionResult r)
    {
        var section = new Section { Id = "s", Name = "S" };
        foreach (var q in r.Questions)
            section.Items.Add(new Item { Id = q.SchemaRef.ItemId, Type = ItemType.OpenQuestion, Verbatim = q.VerbatimSource, AnswerTarget = q.AnswerTarget });
        return r with { DocumentSchema = new DocumentSchema { Sections = { section } } };
    }

    private static Question Body(string id, string at, string text, string verbatim, string section = "S") => new()
    {
        QuestionId = id, AnswerTarget = at, QuestionText = text, VerbatimSource = verbatim,
        SectionPath = section, Source = QuestionSource.Body,
        SchemaRef = new SchemaRef { SectionId = "s", ItemId = id }
    };

    private static Question Cell(string id, string at, string text, string row, string col) => new()
    {
        QuestionId = id, AnswerTarget = at, QuestionText = text, VerbatimSource = $"{col}/{row}",
        SectionPath = "S", Source = QuestionSource.TableCell,
        SchemaRef = new SchemaRef { SectionId = "s", ItemId = "t", Row = row, Column = col }
    };

    private static Question YesNo(string id, string at, string text, string verbatim) => new()
    {
        QuestionId = id, AnswerTarget = at, QuestionText = text, VerbatimSource = verbatim,
        SectionPath = "S", Source = QuestionSource.Body, AnswerType = AnswerType.YesNo,
        SchemaRef = new SchemaRef { SectionId = "s", ItemId = id }
    };
}

public class ResultMergerTests
{
    [Fact]
    public void Stitch_renumbers_targets_and_questions_uniquely()
    {
        var p1 = Page("AT-0001", "Q001");
        var p2 = Page("AT-0001", "Q001");
        var merged = ResultMerger.StitchPages(new[] { p1, p2 });

        var schemaTargets = merged.DocumentSchema.Sections
            .SelectMany(s => s.Items).Select(i => i.AnswerTarget).ToList();
        Assert.Equal(new[] { "AT-0001", "AT-0002" }, schemaTargets);
        Assert.Equal(new[] { "Q001", "Q002" }, merged.Questions.Select(q => q.QuestionId).ToArray());
        Assert.Equal(new[] { "AT-0001", "AT-0002" }, merged.Questions.Select(q => q.AnswerTarget).ToArray());
        Assert.Empty(InvariantValidator.Validate(merged));
    }

    [Fact]
    public void Stitch_joins_truncated_tail_with_next_page_continuation()
    {
        // M&G field finding: a page-1 tail cut at "…to the following:" resurfaced as a phantom
        // question (Q075) that never reconciled against the complete version on page 2.
        const string frag = "Outline any material changes over the past 12 months to the following:";
        const string full = frag + "\n- Investment process\n- Risk management process";

        var page1 = OnePage(
            ("i1", "AT-0001", "Business question?", "Business question?", false),
            ("i2", "AT-0002", frag, frag, truncated: true));      // truncated tail
        var page2 = OnePage(
            ("j1", "AT-0001", full, full, false),                 // continuation (own numbering)
            ("j2", "AT-0002", "Next section question?", "Next section question?", false));

        var merged = ResultMerger.StitchPages(new[] { page1, page2 });

        Assert.DoesNotContain(merged.Questions, q => q.VerbatimSource.Trim() == frag);   // phantom gone
        Assert.Contains(merged.Questions, q => q.VerbatimSource.Contains("Investment process"));
        Assert.Equal(3, merged.Questions.Count);                  // 4 items - 1 folded fragment
        Assert.Empty(InvariantValidator.Validate(merged));
    }

    [Fact]
    public void Stitch_prepends_genuine_continuation_not_otherwise_present()
    {
        const string frag = "Please describe the derivatives programme, including";
        const string rest = "the instruments used and the counterparties.";
        var page1 = OnePage(("i1", "AT-0001", frag, frag, truncated: true));
        var page2 = OnePage(("j1", "AT-0001", rest, rest, false));

        var merged = ResultMerger.StitchPages(new[] { page1, page2 });

        Assert.Single(merged.Questions);
        Assert.Contains(frag, merged.Questions[0].VerbatimSource);   // lead-in preserved
        Assert.Contains(rest, merged.Questions[0].VerbatimSource);   // continuation preserved
    }

    private static ExtractionResult OnePage(params (string id, string at, string text, string verbatim, bool truncated)[] items)
    {
        var section = new Section { Id = "s", Name = "S" };
        var qs = new List<Question>();
        foreach (var it in items)
        {
            section.Items.Add(new Item { Id = it.id, Type = ItemType.OpenQuestion, Verbatim = it.verbatim, AnswerTarget = it.at, Truncated = it.truncated });
            qs.Add(new Question { QuestionId = it.id, AnswerTarget = it.at, QuestionText = it.text, VerbatimSource = it.verbatim, SectionPath = "S", Source = QuestionSource.Body, SchemaRef = new SchemaRef { SectionId = "s", ItemId = it.id } });
        }
        return new ExtractionResult { DocumentSchema = new DocumentSchema { Sections = { section } }, Questions = qs };
    }

    private static ExtractionResult Page(string at, string qid) => new()
    {
        DocumentSchema = new DocumentSchema
        {
            Sections = { new Section { Id = "s", Name = "S", Items = {
                new Item { Id = "i", Type = ItemType.OpenQuestion, Verbatim = "v", AnswerTarget = at } } } }
        },
        Questions =
        {
            new Question { QuestionId = qid, AnswerTarget = at, QuestionText = "q", VerbatimSource = "v",
                SectionPath = "S", Source = QuestionSource.Body, SchemaRef = new SchemaRef { SectionId = "s", ItemId = "i" } }
        }
    };
}
