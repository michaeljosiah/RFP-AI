using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace RfpExtractor.Core.Llm;

/// <summary>
/// The one way this codebase builds and runs a structured-output agent, shared by the extractor,
/// the fuzzy matcher and the decomposer so transport behaviour is identical everywhere:
///  - schema via native ResponseFormat, or appended to the prompt when the model can't take one
///    (see <see cref="ModelProfile.NativeJsonSchema"/> / <see cref="ModelCapabilities"/>);
///  - responses STREAMED and assembled — streaming buys no latency on the final result, but keeps
///    bytes flowing so gateway IDLE timeouts (GenCore) cannot cut long requests;
///  - non-streaming fallback for providers that don't stream structured output as text (Claude),
///    then code-fence/prose stripping via <see cref="StructuredJson.Payload"/>;
///  - throws on an empty response so the caller's retry policy fires.
/// </summary>
internal static class StructuredAgent
{
    internal static AIAgent Create(IChatClient chat, string name, string instructions,
        JsonElement schema, string schemaName, string schemaDescription, ModelProfile profile)
        => new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Instructions = profile.NativeJsonSchema
                    ? instructions
                    : instructions + StructuredJson.SchemaInstruction(schema),
                Temperature = profile.Temperature,
                MaxOutputTokens = profile.MaxOutputTokens,
                ResponseFormat = profile.NativeJsonSchema
                    ? ChatResponseFormat.ForJsonSchema(schema, schemaName, schemaDescription)
                    : null,
            },
        });

    /// <summary>Runs the agent and returns the raw JSON payload text (never empty — throws instead).</summary>
    internal static async Task<string> RunJsonAsync(AIAgent agent, ChatMessage message, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync(message, cancellationToken: ct))
            sb.Append(update.Text);

        var raw = sb.ToString();
        var diag = "";
        if (string.IsNullOrWhiteSpace(raw))                                        // rare empty stream
            (raw, diag) = await AgentResponseJson.FromAsync(agent, message, ct);

        var json = StructuredJson.Payload(raw);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"Model returned an empty response ({diag}).");   // triggers retry
        return json;
    }
}
