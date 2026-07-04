using RfpExtractor.Core.Llm;
using Xunit;

namespace RfpExtractor.Tests;

public class StructuredJsonTests
{
    [Theory]
    // already-clean native-schema output: unchanged
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("[{\"a\":1}]", "[{\"a\":1}]")]
    // markdown code fences (Claude often wraps despite instructions)
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```\n[1,2]\n```", "[1,2]")]
    // prose before/after the JSON
    [InlineData("Here is the JSON:\n{\"a\":1}", "{\"a\":1}")]
    [InlineData("{\"a\":1}\nLet me know if you need more.", "{\"a\":1}")]
    // leading/trailing whitespace
    [InlineData("  \n {\"a\":1} \n ", "{\"a\":1}")]
    public void Payload_extracts_json(string raw, string expected)
        => Assert.Equal(expected, StructuredJson.Payload(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Payload_tolerates_empty(string? raw)
        => Assert.True(string.IsNullOrWhiteSpace(StructuredJson.Payload(raw)));
}
