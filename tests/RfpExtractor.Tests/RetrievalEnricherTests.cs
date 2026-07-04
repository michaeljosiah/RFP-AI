using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using RfpExtractor.Core.Llm;
using RfpExtractor.Core.Models;
using Xunit;

namespace RfpExtractor.Tests;

public class RetrievalEnricherTests
{
    [Fact]
    public async Task Decomposes_compound_into_parts_tags_single_and_skips_internal()
    {
        var canned = """
            { "questions": [
              { "id": "Q1", "parts": [
                  { "question_text": "Outline the firm's AUM.", "answer_type": "currency", "category": "firm_profile", "units": "S$ million", "requires_external_input": true, "ai_comment": "Requires audited AUM figures from Finance." },
                  { "question_text": "Provide a split of AUM by region.", "answer_type": "long_text", "category": "firm_profile", "units": null, "requires_external_input": true, "ai_comment": null }
              ] },
              { "id": "Q2", "parts": [
                  { "question_text": "Describe your ESG policy.", "answer_type": "long_text", "category": "esg", "units": null, "requires_external_input": false, "ai_comment": null }
              ] }
            ] }
            """;
        var r = new ExtractionResult
        {
            Questions =
            {
                Q("Q1", "Outline the firm's AUM and split by region.", AnswerType.LongText, "Business", QuestionSource.Body, Audience.Applicant),
                Q("Q2", "Describe your ESG policy.", AnswerType.LongText, "ESG", QuestionSource.Body, Audience.Applicant),
                Q("Q3", "Internal classification.", AnswerType.Text, "BGFML SECTION ONLY*", QuestionSource.Body, Audience.Internal),
            }
        };

        await new AgentRetrievalEnricher(new CannedChat(canned)).EnrichAsync(r, CancellationToken.None);

        // Q1 is compound -> parent has parts, retrieval lives on the parts
        var q1 = r.Questions[0];
        Assert.Null(q1.Retrieval);
        Assert.Equal(2, q1.Parts.Count);
        Assert.Equal("Q1.1", q1.Parts[0].PartId);
        Assert.Equal("AT-Q1-1", q1.Parts[0].AnswerTarget);
        Assert.Equal("Outline the firm's AUM.", q1.Parts[0].QuestionText);
        Assert.Equal(AnswerType.Currency, q1.Parts[0].AnswerType);
        Assert.Equal(QuestionCategory.FirmProfile, q1.Parts[0].Retrieval!.Category);
        Assert.Equal("S$ million", q1.Parts[0].Retrieval!.Units);
        Assert.Equal(ExpectedFormat.Value, q1.Parts[0].Retrieval!.ExpectedFormat);   // currency -> value
        Assert.Equal("Requires audited AUM figures from Finance.", q1.Parts[0].Retrieval!.AiComment);

        // Q2 is single -> tagged directly, no parts
        var q2 = r.Questions[1];
        Assert.Empty(q2.Parts);
        Assert.Equal(QuestionCategory.Esg, q2.Retrieval!.Category);
        Assert.False(q2.Retrieval!.RequiresExternalInput);
        Assert.Equal(ExpectedFormat.Narrative, q2.Retrieval!.ExpectedFormat);

        // Q3 internal -> never touched
        Assert.Null(r.Questions[2].Retrieval);
        Assert.Empty(r.Questions[2].Parts);
    }

    [Fact]
    public async Task Document_requests_never_split_even_if_the_model_returns_multiple_parts()
    {
        var canned = """
            { "questions": [
              { "id": "Q1", "parts": [
                  { "question_text": "Upload attribution for 1 year.", "answer_type": "document_upload", "category": "performance", "requires_external_input": true, "ai_comment": null },
                  { "question_text": "Upload attribution for 3 years.", "answer_type": "document_upload", "category": "performance", "requires_external_input": true, "ai_comment": null }
              ] }
            ] }
            """;
        var r = new ExtractionResult
        {
            Questions = { Q("Q1", "Upload attribution for 1, 3 and 5 year periods.", AnswerType.DocumentUpload, "Data request", QuestionSource.DocumentRequest, Audience.Applicant) }
        };

        await new AgentRetrievalEnricher(new CannedChat(canned)).EnrichAsync(r, CancellationToken.None);

        Assert.Empty(r.Questions[0].Parts);                            // forced single despite 2 returned parts
        Assert.Equal(QuestionCategory.Performance, r.Questions[0].Retrieval!.Category);
        Assert.Equal(ExpectedFormat.Document, r.Questions[0].Retrieval!.ExpectedFormat);
    }

    [Fact]
    public async Task Llm_failure_leaves_deterministic_baseline()
    {
        var r = new ExtractionResult
        {
            Questions = { Q("Q1", "Provide the launch date.", AnswerType.Date, "Fund Details", QuestionSource.Body, Audience.Applicant) }
        };

        await new AgentRetrievalEnricher(new FailingChat()).EnrichAsync(r, CancellationToken.None);

        var q = r.Questions[0];
        Assert.Empty(q.Parts);                                 // no decomposition without the LLM
        Assert.Equal(QuestionCategory.Other, q.Retrieval!.Category);
        Assert.Equal(ExpectedFormat.Date, q.Retrieval!.ExpectedFormat);   // derived even without the LLM
        Assert.True(q.Retrieval!.RequiresExternalInput);       // safe default
    }

    [Theory]
    [InlineData("firm_profile", QuestionCategory.FirmProfile)]
    [InlineData("Investment Process", QuestionCategory.InvestmentProcess)]
    [InlineData("nonsense", QuestionCategory.Other)]
    [InlineData(null, QuestionCategory.Other)]
    public void Parse_category_is_tolerant(string? value, QuestionCategory expected)
        => Assert.Equal(expected, AgentRetrievalEnricher.ParseCategory(value));

    [Theory]
    [InlineData("currency", AnswerType.Currency)]
    [InlineData("long_text", AnswerType.LongText)]
    [InlineData("yes_no", AnswerType.YesNo)]
    [InlineData("document_upload", AnswerType.DocumentUpload)]
    [InlineData("nonsense", AnswerType.Text)]   // -> fallback
    [InlineData("", AnswerType.Text)]
    public void Parse_answer_type_is_tolerant(string? value, AnswerType expected)
        => Assert.Equal(expected, AgentRetrievalEnricher.ParseAnswerType(value, AnswerType.Text));

    [Theory]
    [InlineData(AnswerType.DocumentUpload, QuestionSource.DocumentRequest, ExpectedFormat.Document)]
    [InlineData(AnswerType.YesNo, QuestionSource.Body, ExpectedFormat.Boolean)]
    [InlineData(AnswerType.Currency, QuestionSource.Body, ExpectedFormat.Value)]
    [InlineData(AnswerType.Text, QuestionSource.TableCell, ExpectedFormat.Value)]
    [InlineData(AnswerType.LongText, QuestionSource.Body, ExpectedFormat.Narrative)]
    [InlineData(AnswerType.Text, QuestionSource.Body, ExpectedFormat.ShortText)]
    public void Expected_format_derivation(AnswerType type, QuestionSource source, ExpectedFormat expected)
        => Assert.Equal(expected, AgentRetrievalEnricher.ExpectedFormatFor(type, source));

    private static Question Q(string id, string text, AnswerType type, string section, QuestionSource source, Audience audience) => new()
    {
        QuestionId = id, AnswerTarget = "AT-" + id, QuestionText = text, VerbatimSource = text,
        AnswerType = type, SectionPath = section, Source = source, Audience = audience,
        SchemaRef = new SchemaRef { SectionId = "s", ItemId = id }
    };

    // ---- fake IChatClients ----

    private sealed class CannedChat(string json) : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, json);
        }
    }

    private sealed class FailingChat : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new InvalidOperationException("gateway down");
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("gateway down");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
