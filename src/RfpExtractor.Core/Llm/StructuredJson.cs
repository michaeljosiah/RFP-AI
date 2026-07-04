using System.Text.Json;

namespace RfpExtractor.Core.Llm;

/// <summary>
/// Helpers for JSON-schema structured output across providers. Two modes, mirroring MEAI's
/// <c>GetResponseAsync&lt;T&gt;(useJsonSchemaResponseFormat:)</c>:
///  - NATIVE (OpenAI/GenCore/Azure): a JSON schema is set on ChatOptions.ResponseFormat.
///  - PROMPT (Anthropic Claude, whose beta IChatClient mishandles a native schema → empty response):
///    the schema is appended to the instructions and the model returns plain JSON text.
/// </summary>
public static class StructuredJson
{
    /// <summary>Instruction appended to a prompt when the model can't take a native response-format schema.</summary>
    public static string SchemaInstruction(JsonElement schema) =>
        "\n\nOUTPUT FORMAT: respond with ONLY a single JSON value that conforms to this JSON Schema — " +
        "no prose, no explanation, no markdown code fences.\nJSON Schema:\n" + schema.GetRawText();

    /// <summary>
    /// Pulls the JSON value out of a model's raw text: strips ``` code fences and trims any prose
    /// before/after the outermost object/array. A no-op on already-clean native-schema output.
    /// </summary>
    public static string Payload(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";
        var s = raw.Trim();

        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            s = nl >= 0 ? s[(nl + 1)..] : s;                 // drop the ```json line
            var fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) s = s[..fence];
            s = s.Trim();
        }

        var start = s.IndexOfAny(['{', '[']);
        var end = s.LastIndexOfAny(['}', ']']);
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }
}
