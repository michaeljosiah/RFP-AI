using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using RfpExtractor.Cli.GenCore;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.LibreOffice;
using RfpExtractor.Telerik;

namespace RfpExtractor.Cli;

/// <summary>Shared composition helpers used by both the batch CLI path and the "serve" UI.</summary>
public static class Wiring
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.Local.json", optional: true)   // machine-local (gitignored): real endpoints/keys
        .AddEnvironmentVariables()
        .Build();

    public static string GotenbergUrl(IConfiguration config) =>
        config["GOTENBERG_URL"] ?? "http://localhost:3000";

    public static (IDocumentRenderer Renderer, IStructuredTextExtractor Text, ISpreadsheetExtractor Sheet)
        CreateEngine(string engine, IConfiguration config) => engine.ToLowerInvariant() switch
    {
        // BOTH engines use the Open XML SDK text extractor for docx: Telerik's markdown export
        // flattens nested tables (M&G field finding — the nested AUM/team grids were lost), while
        // the Open XML walker preserves them. Telerik still does rendering + spreadsheets.
        "telerik" => ((IDocumentRenderer)new TelerikRenderer(),
                      new OpenXmlTextExtractor(), new TelerikSpreadsheetExtractor()),
        "libreoffice" => ((IDocumentRenderer)new LibreOfficeRenderer(Http, GotenbergUrl(config)),
                          new OpenXmlTextExtractor(), new ClosedXmlSpreadsheetExtractor()),
        _ => throw new ArgumentException($"Unknown engine '{engine}' (use telerik or libreoffice)."),
    };

    public static string ResolveDefaultModel(IConfiguration config)
    {
        var proxy = config["AzureOpenAIProxyName"] ?? "GenerativeCore";
        return (config[$"AzureOpenAIProxySettings:{proxy}:DefaultModel"] ?? "gpt-4o")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    /// <summary>The model that will actually run, resolving provider-specific defaults.</summary>
    public static string EffectiveModel(string provider, string? model, IConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(model)) return model!;
        return provider.ToLowerInvariant() switch
        {
            "gencore" => ResolveDefaultModel(config),
            "claude" or "anthropic" => "claude-sonnet-5",
            _ => "gpt-4o",
        };
    }

    /// <summary>
    /// The sampling temperature to send for a model, or <c>null</c> to omit it. Delegates to
    /// <see cref="Core.Llm.ModelCapabilities.TemperatureFor"/> (GPT-5 / o-series reject non-default
    /// temperature); kept here so the CLI has one wiring facade.
    /// </summary>
    public static float? TemperatureFor(string? model) => Core.Llm.ModelCapabilities.TemperatureFor(model);

    public static IChatClient CreateChatClient(string provider, string? model, IConfiguration config, string? userEmail = null)
    {
        switch (provider.ToLowerInvariant())
        {
            case "gencore":
            {
                var proxy = config["AzureOpenAIProxyName"] ?? "GenerativeCore";
                var baseUri = config[$"AzureOpenAIProxySettings:{proxy}:BaseUri"]
                    ?? throw new InvalidOperationException($"GenCore BaseUri missing (AzureOpenAIProxySettings:{proxy}:BaseUri).");
                var resolvedModel = string.IsNullOrWhiteSpace(model) ? ResolveDefaultModel(config) : model;
                var apiKey = config["EnterpriseGenCoreApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("GenCore API key missing. Set env var EnterpriseGenCoreApiKey (or in appsettings.json).");
                var email = userEmail ?? config["GenCore:UserEmail"];
                if (string.IsNullOrWhiteSpace(email)) email = Environment.UserName;
                return GenCoreChatClientFactory.Create(baseUri, apiKey, resolvedModel, email,
                    config["GenCore:ApplicationName"] ?? "smartdocs",
                    config["GenCore:ClassifierVersion"] ?? "v1.1");
            }
            case "azure":
            {
                // Azure OpenAI v1 API (GA since Aug 2025): the plain OpenAI SDK pointed at
                // {resource}/openai/v1, NOT the Azure-specific AzureOpenAIClient — no api-version,
                // minimal code diff from plain OpenAI. "model" here is the DEPLOYMENT NAME, not
                // necessarily the underlying model name. https://YOUR-RESOURCE.openai.azure.com/openai/v1
                var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? config["AzureOpenAIEndpoint"]
                    ?? throw new InvalidOperationException(
                        "Set AZURE_OPENAI_ENDPOINT (e.g. https://<resource>.openai.azure.com/openai/v1).");
                var resolvedModel = EffectiveModel(provider, model, config);
                var options = new OpenAIClientOptions { Endpoint = NormalizeAzureV1(endpoint) };
                var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? config["AzureOpenAIApiKey"];

                OpenAIClient azure;
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    azure = new OpenAIClient(new ApiKeyCredential(apiKey), options);
                }
                else
                {
                    // Microsoft Entra ID (recommended): requires `az login` and the
                    // "Cognitive Services OpenAI User" role on the signed-in identity.
#pragma warning disable OPENAI001   // BearerTokenPolicy ctor / authenticationPolicy ctor are experimental
                    var tokenPolicy = new BearerTokenPolicy(new DefaultAzureCredential(), "https://ai.azure.com/.default");
                    azure = new OpenAIClient(authenticationPolicy: tokenPolicy, options: options);
#pragma warning restore OPENAI001
                }

                // Azure v1: use the RESPONSES API (Microsoft's recommended surface). The chat-completions
                // API returns "api_not_supported" for many deployments — gpt-5 / o-series reasoning models
                // AND Foundry-hosted third-party models like Claude / DeepSeek / Grok — whereas the
                // Responses API works across all of them. Surfaced as an IChatClient, pipeline unchanged.
#pragma warning disable OPENAI001   // Responses-API AsIChatClient is experimental in this SDK version
                return azure.GetResponsesClient().AsIChatClient(resolvedModel);
#pragma warning restore OPENAI001
            }
            case "openai":
            {
                // Plain OpenAI (api.openai.com) — handy for a quick local test before GenCore.
                var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? config["OpenAIApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("Set OPENAI_API_KEY (or OpenAIApiKey in appsettings.json).");
                return new OpenAIClient(apiKey)
                    .GetChatClient(string.IsNullOrWhiteSpace(model) ? "gpt-4o" : model)
                    .AsIChatClient();
            }
            case "claude":
            case "anthropic":
            {
                // Anthropic Claude direct (api.anthropic.com) via the official SDK's IChatClient.
                var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? config["AnthropicApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("Set ANTHROPIC_API_KEY (or AnthropicApiKey in appsettings.json).");
                return new Anthropic.AnthropicClient { ApiKey = apiKey }
                    .AsIChatClient(EffectiveModel(provider, model, config));
            }
            default:
                throw new ArgumentException($"Unknown provider '{provider}' (use gencore, azure, openai or claude).");
        }
    }

    /// <summary>Ensures the Azure OpenAI v1 endpoint carries the required "/openai/v1" path suffix.</summary>
    private static Uri NormalizeAzureV1(string baseUri)
    {
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        const string suffix = "/openai/v1";
        builder.Path = path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? path
            : (string.IsNullOrEmpty(path) || path == "/" ? suffix : path + suffix);
        return builder.Uri;
    }
}
