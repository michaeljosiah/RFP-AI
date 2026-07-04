using System.Text.Json;
using System.Text.Json.Serialization;

namespace RfpExtractor.Core.Json;

public static class Json
{
    /// <summary>Indented — for human-facing output files.</summary>
    public static readonly JsonSerializerOptions Options = Build(indented: true);

    /// <summary>Compact — for LLM payloads; indentation is pure token waste in a prompt.</summary>
    public static readonly JsonSerializerOptions Compact = Build(indented: false);

    private static JsonSerializerOptions Build(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return o;
    }
}
