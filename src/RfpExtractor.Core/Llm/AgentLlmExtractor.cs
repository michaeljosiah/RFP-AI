using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Llm;

/// <summary>
/// LLM extraction via Microsoft Agent Framework over any <see cref="IChatClient"/>. One agent per
/// mode (vision / text / grid), all built and run through <see cref="StructuredAgent"/> — see that
/// class for the streaming / schema-mode / empty-response behaviour. Model-specific call shaping
/// (temperature, schema mode, output budget) comes in as a <see cref="ModelProfile"/>.
/// </summary>
public sealed class AgentLlmExtractor : ILlmExtractor
{
    private readonly AIAgent _vision;
    private readonly AIAgent _text;
    private readonly AIAgent _grid;

    public AgentLlmExtractor(IChatClient chat, ModelProfile? profile = null)
    {
        var p = profile ?? ModelProfile.Default;
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(ExtractionResult), serializerOptions: Json.Json.Options);

        AIAgent Make(string name, string instructions) => StructuredAgent.Create(chat, name, instructions,
            schema, "extraction_result", "Document schema and flat question list extracted from a questionnaire.", p);

        _vision = Make("vision-extractor", Prompts.Prompts.Vision);
        _text = Make("text-extractor", Prompts.Prompts.Text);
        _grid = Make("grid-extractor", Prompts.Prompts.Grid);
    }

    public Task<ExtractionResult> ExtractFromImageAsync(PageImage page, CancellationToken ct)
    {
        var message = new ChatMessage(ChatRole.User, new AIContent[]
        {
            new TextContent($"This is page {page.PageNumber}. Extract per the rules."),
            new DataContent(page.PngBytes, "image/png"),
        });
        return ExecuteAsync(_vision, message, ct);
    }

    public Task<ExtractionResult> ExtractFromTextAsync(string markdown, int? pageHint, CancellationToken ct)
        => ExecuteAsync(_text, new ChatMessage(ChatRole.User, markdown), ct);

    public Task<ExtractionResult> ExtractFromGridAsync(string sheetGridJson, CancellationToken ct)
        => ExecuteAsync(_grid, new ChatMessage(ChatRole.User, sheetGridJson), ct);

    private static async Task<ExtractionResult> ExecuteAsync(AIAgent agent, ChatMessage message, CancellationToken ct)
    {
        var json = await StructuredAgent.RunJsonAsync(agent, message, ct);
        var result = JsonSerializer.Deserialize<ExtractionResult>(json, Json.Json.Options)
            ?? throw new InvalidOperationException("Model response deserialized to null.");
        return Reconciliation.QuestionCleaner.Clean(result);
    }
}
