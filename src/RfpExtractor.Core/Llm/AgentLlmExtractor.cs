using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Llm;

/// <summary>
/// LLM extraction via Microsoft Agent Framework over any <see cref="IChatClient"/>.
///
/// Responses are STREAMED and assembled before deserialization. Streaming buys no latency on the
/// final structured result, but it keeps bytes flowing continuously on the wire — which defeats
/// gateway IDLE timeouts (GenCore is notorious for cutting silent long-running requests). Hard
/// total-duration caps are handled separately by bounding request size (page-per-call, chunked
/// text, per-sheet grids) in the pipelines. Structured output is requested with a native JSON-schema
/// ResponseFormat where supported; for Claude (whose beta client mishandles that) the schema is put
/// in the prompt and the JSON is parsed from the text — see <c>ModelCapabilities.SupportsNativeJsonSchema</c>.
///
/// Structured output is enforced by putting an explicit JSON schema (derived from
/// <see cref="ExtractionResult"/>) on ChatOptions.ResponseFormat — the streaming-compatible
/// equivalent of RunAsync&lt;T&gt;.
/// </summary>
public sealed class AgentLlmExtractor : ILlmExtractor
{
    private readonly AIAgent _vision;
    private readonly AIAgent _text;
    private readonly AIAgent _grid;

    /// <param name="temperature">
    /// Sampling temperature, or null to omit it. Pass null for GPT-5 / o-series models, which reject
    /// any non-default temperature (see <c>Wiring.TemperatureFor</c>); defaults to 0 for determinism.
    /// </param>
    /// <param name="nativeSchema">
    /// True to set a native JSON-schema ResponseFormat (OpenAI/GenCore). False for Claude, whose beta
    /// client mishandles it — the schema is put in the prompt instead (see
    /// <c>ModelCapabilities.SupportsNativeJsonSchema</c>).
    /// </param>
    /// <param name="maxOutputTokens">
    /// Max output tokens, or null for the provider default. REQUIRED to be generous for Claude, whose
    /// thinking draws from the same budget (see <c>ModelCapabilities.MaxOutputTokensFor</c>).
    /// </param>
    public AgentLlmExtractor(IChatClient chat, float? temperature = 0f, bool nativeSchema = true, int? maxOutputTokens = null)
    {
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(ExtractionResult), serializerOptions: Json.Json.Options);
        var responseFormat = ChatResponseFormat.ForJsonSchema(schema, "extraction_result",
            "Document schema and flat question list extracted from a questionnaire.");
        var schemaSuffix = StructuredJson.SchemaInstruction(schema);

        AIAgent Make(string name, string instructions) => new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Instructions = nativeSchema ? instructions : instructions + schemaSuffix,
                Temperature = temperature,
                MaxOutputTokens = maxOutputTokens,
                ResponseFormat = nativeSchema ? responseFormat : null,
            },
        });

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
        // Stream to keep bytes flowing so gateway IDLE timeouts (GenCore) cannot cut the request.
        // Native-schema and schema-in-prompt both return the JSON as text, so streaming works for all.
        var sb = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync(message, cancellationToken: ct))
            sb.Append(update.Text);

        var raw = sb.ToString();
        var diag = "";
        if (string.IsNullOrWhiteSpace(raw))                                        // rare empty stream
            (raw, diag) = await AgentResponseJson.FromAsync(agent, message, ct);

        var json = StructuredJson.Payload(raw);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"Model returned an empty response ({diag}).");   // triggers pipeline retry

        var result = JsonSerializer.Deserialize<ExtractionResult>(json, Json.Json.Options)
            ?? throw new InvalidOperationException("Model response deserialized to null.");
        return RfpExtractor.Core.Reconciliation.QuestionCleaner.Clean(result);
    }
}
