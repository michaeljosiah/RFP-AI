namespace RfpExtractor.Cli;

/// <summary>Ensures an OpenAI-v1-compatible base address (GenCore gateway, Azure OpenAI v1)
/// carries the required "/openai/v1" path suffix. One implementation for every provider.</summary>
internal static class OpenAIV1
{
    internal static Uri Normalize(string baseUri)
    {
        if (string.IsNullOrWhiteSpace(baseUri))
            throw new InvalidOperationException("Provider base URI is missing.");

        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        const string suffix = "/openai/v1";
        builder.Path = path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? path
            : (string.IsNullOrEmpty(path) || path == "/" ? suffix : path + suffix);
        return builder.Uri;
    }
}
