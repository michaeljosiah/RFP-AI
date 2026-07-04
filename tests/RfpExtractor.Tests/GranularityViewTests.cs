using RfpExtractor.Core.Models;
using RfpExtractor.Core.Reconciliation;
using Xunit;

namespace RfpExtractor.Tests;

public class GranularityViewTests
{
    // Canonical (hybrid) form: printed-level questions, compounds carry Parts. Here: one compound
    // (2 parts) + one single (no parts).
    private static ExtractionResult Canonical() => new()
    {
        Questions =
        {
            new Question
            {
                QuestionId = "Q001", AnswerTarget = "AT-1", SectionPath = "Business", Source = QuestionSource.Body,
                AnswerType = AnswerType.LongText, Retrieval = null,
                QuestionText = "Outline the firm's AUM and split by region.",
                VerbatimSource = "Outline the firm's AUM and split by region.",
                Parts =
                {
                    new QuestionPart { PartId = "Q001.1", AnswerTarget = "AT-1-1", QuestionText = "Outline the firm's AUM.", AnswerType = AnswerType.Currency, Retrieval = new RetrievalHint { Category = QuestionCategory.FirmProfile, Units = "S$ m" } },
                    new QuestionPart { PartId = "Q001.2", AnswerTarget = "AT-1-2", QuestionText = "Split AUM by region.", AnswerType = AnswerType.LongText, Retrieval = new RetrievalHint { Category = QuestionCategory.FirmProfile } },
                }
            },
            new Question
            {
                QuestionId = "Q002", AnswerTarget = "AT-2", SectionPath = "Process", Source = QuestionSource.Body,
                AnswerType = AnswerType.LongText, QuestionText = "What is the fund capacity?", VerbatimSource = "What is the current capacity on the fund?",
                Retrieval = new RetrievalHint { Category = QuestionCategory.InvestmentProcess }
            },
        }
    };

    [Fact]
    public void Atomic_count_sums_parts()
        => Assert.Equal(3, GranularityView.AtomicCount(Canonical().Questions));   // 2 parts + 1 single

    [Fact]
    public void Hybrid_returns_the_canonical_form_unchanged()
    {
        var r = Canonical();
        var view = GranularityView.Apply(r, Granularity.Hybrid);
        Assert.Same(r, view);
        Assert.Equal(2, view.Questions.Count);
        Assert.Equal(2, view.Questions[0].Parts.Count);
    }

    [Fact]
    public void Bundled_flattens_parts_to_sub_question_strings()
    {
        var view = GranularityView.Apply(Canonical(), Granularity.Bundled);
        Assert.Equal(2, view.Questions.Count);

        var aum = view.Questions[0];
        Assert.Empty(aum.Parts);
        Assert.Equal(new[] { "Outline the firm's AUM.", "Split AUM by region." }, aum.SubQuestions.ToArray());
        Assert.Equal("Outline the firm's AUM and split by region.", aum.QuestionText);   // printed text kept

        Assert.Empty(view.Questions[1].SubQuestions);   // single unchanged
    }

    [Fact]
    public void Atomic_expands_each_part_into_its_own_question()
    {
        var view = GranularityView.Apply(Canonical(), Granularity.Atomic);
        Assert.Equal(3, view.Questions.Count);          // 2 from the compound + 1 single

        Assert.Equal("Outline the firm's AUM.", view.Questions[0].QuestionText);
        Assert.Equal("AT-1-1", view.Questions[0].AnswerTarget);
        Assert.Equal(AnswerType.Currency, view.Questions[0].AnswerType);
        Assert.Equal(QuestionCategory.FirmProfile, view.Questions[0].Retrieval!.Category);
        Assert.Empty(view.Questions[0].Parts);

        Assert.Equal("Split AUM by region.", view.Questions[1].QuestionText);
        Assert.Equal("What is the fund capacity?", view.Questions[2].QuestionText);   // the single, unchanged
    }
}
