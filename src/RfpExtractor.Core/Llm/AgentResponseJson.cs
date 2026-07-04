using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace RfpExtractor.Core.Llm;

/// <summary>
/// One non-streaming agent call that returns the JSON payload, tolerating how different providers
/// deliver JSON-schema structured output (text vs. a forced tool call). When nothing usable comes
/// back it also returns a short diagnostic describing what the response actually contained, so an
/// otherwise-opaque "empty response" can be debugged from the error message.
/// </summary>
internal static class AgentResponseJson
{
    public static async Task<(string Json, string Diagnostic)> FromAsync(AIAgent agent, ChatMessage message, CancellationToken ct)
    {
        var response = await agent.RunAsync(message, cancellationToken: ct);

        // (a) plain text (OpenAI / GenCore / Azure)
        if (!string.IsNullOrWhiteSpace(response.Text)) return (response.Text, "");

        // (b) structured output delivered as a tool/function call (Claude) -> args ARE the object
        foreach (var msg in response.Messages)
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent { Arguments: { Count: > 0 } args })
                    return (JsonSerializer.Serialize(args, Json.Json.Options), "");
                if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                    return (tc.Text, "");
            }

        // (c) nothing usable — describe what came back so we can diagnose precisely
        var blocks = response.Messages.SelectMany(m => m.Contents).ToList();
        var types = blocks.Count == 0 ? "none" : string.Join("+", blocks.Select(c => c.GetType().Name));
        var finish = response.Messages.LastOrDefault()?.AdditionalProperties is { } ap && ap.Count > 0
            ? " props=" + string.Join(",", ap.Keys) : "";
        return ("", $"blocks={types}{finish}");
    }
}
