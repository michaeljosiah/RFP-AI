using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace RfpExtractor.Cli.GenCore;

/// <summary>
/// Builds an <see cref="IChatClient"/> that talks to M&amp;G's GenCore gateway.
/// GenCore is an OpenAI-v1-compatible proxy (NOT Azure OpenAI native), so we use the OpenAI SDK
/// pointed at {BaseUri}/openai/v1 with an api-key header injected by <see cref="GenCoreHeaderHandler"/>.
/// Mirrors AP.Nexus.Agents' AzureOpenAiChatClientProvider (proxy mode).
/// </summary>
public static class GenCoreChatClientFactory
{
    private const string V1Suffix = "/openai/v1";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public static IChatClient Create(
        string baseUri, string apiKey, string model,
        string userEmail, string applicationName, string classifierVersion)
    {
        var handler = new GenCoreHeaderHandler(apiKey, model, userEmail, applicationName, classifierVersion);
        var http = new HttpClient(handler) { Timeout = Timeout };

        var options = new OpenAIClientOptions
        {
            Endpoint = NormalizeV1(baseUri),
            Transport = new HttpClientPipelineTransport(http),
            NetworkTimeout = Timeout,
        };

        // The real api-key travels in the header (added by the handler); the SDK credential is a
        // placeholder because GenCore authenticates via the api-key header, not a bearer token.
        var chatClient = new ChatClient(model, new ApiKeyCredential("unused-proxy-api-key"), options);
        return chatClient.AsIChatClient();
    }

    private static Uri NormalizeV1(string baseUri)
    {
        if (string.IsNullOrWhiteSpace(baseUri))
            throw new InvalidOperationException("GenCore BaseUri is missing.");

        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith(V1Suffix, StringComparison.OrdinalIgnoreCase)
            ? path
            : (string.IsNullOrEmpty(path) || path == "/" ? V1Suffix : path + V1Suffix);
        return builder.Uri;
    }
}

/// <summary>
/// Injects the headers GenCore requires and strips the SDK's Authorization header.
/// Matches AP.Nexus.Agents' OpenAIProxyHandler.
/// </summary>
public sealed class GenCoreHeaderHandler : DelegatingHandler
{
    private const string ApiKeyHeader = "api-key";
    private const string AuthorizationHeader = "Authorization";
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _userEmail;
    private readonly string _applicationName;
    private readonly string _classifierVersion;

    public GenCoreHeaderHandler(string apiKey, string model, string userEmail,
                                string applicationName, string classifierVersion)
    {
        _apiKey = apiKey;
        _model = model;
        _userEmail = userEmail;
        _applicationName = applicationName;
        _classifierVersion = classifierVersion;
        InnerHandler = new HttpClientHandler();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Remove(AuthorizationHeader);
        request.Headers.Remove(ApiKeyHeader);
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, _apiKey);

        request.Headers.TryAddWithoutValidation("model_engine", _model);
        request.Headers.TryAddWithoutValidation("user_email", _userEmail);
        request.Headers.TryAddWithoutValidation("application_name", _applicationName);
        request.Headers.TryAddWithoutValidation("classifier_version", _classifierVersion);

        return base.SendAsync(request, ct);
    }
}
