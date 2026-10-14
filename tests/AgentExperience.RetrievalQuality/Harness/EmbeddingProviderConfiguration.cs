using System.ClientModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>
/// The hybrid column's embedding provider, read from environment variables only, in the live experiment's style: nothing
/// runs unless <see cref="ProviderVariable"/> names a provider, so a developer machine that happens to have an API key set
/// still makes no network call in the default test run, and CI never needs a key.
/// </summary>
/// <remarks>
/// A class, not a record, so no generated <c>ToString</c> can print the key. The key is handed to the provider SDK and
/// nowhere else.
/// </remarks>
internal sealed class EmbeddingProviderConfiguration
{
    /// <summary>Opts in, and names the provider: <c>openai</c> (any OpenAI-compatible endpoint) or <c>azure</c>.</summary>
    public const string ProviderVariable = "AGENTEXPERIENCE_RETRIEVALQUALITY_EMBEDDINGS";

    public const string OpenAiKeyVariable = "OPENAI_API_KEY";

    /// <summary>Optional: another OpenAI-compatible endpoint than api.openai.com.</summary>
    public const string OpenAiBaseUrlVariable = "OPENAI_BASE_URL";

    /// <summary>Optional for OpenAI: the embedding model. Defaults to <see cref="DefaultOpenAiModel"/>.</summary>
    public const string OpenAiModelVariable = "OPENAI_EMBEDDING_MODEL";

    public const string AzureEndpointVariable = "AZURE_OPENAI_ENDPOINT";

    public const string AzureKeyVariable = "AZURE_OPENAI_API_KEY";

    public const string AzureDeploymentVariable = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT";

    /// <summary>
    /// The vector width. Optional for the models <see cref="KnownDimensions"/> lists; required otherwise, because every
    /// stored vector is stamped with it before the provider is called.
    /// </summary>
    public const string DimensionsVariable = "AGENTEXPERIENCE_EMBEDDING_DIMENSIONS";

    /// <summary>Where to write the rendered comparison, in addition to the test output. Optional.</summary>
    public const string ReportPathVariable = "AGENTEXPERIENCE_RETRIEVALQUALITY_HYBRID_REPORT";

    public const string DefaultOpenAiModel = "text-embedding-3-small";

    private static readonly Dictionary<string, int> KnownDimensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text-embedding-3-small"] = 1536,
        ["text-embedding-3-large"] = 3072,
        ["text-embedding-ada-002"] = 1536,
    };

    private readonly string _apiKey;

    private EmbeddingProviderConfiguration(string provider, string apiKey, string model, Uri? endpoint, int dimensions)
    {
        Provider = provider;
        _apiKey = apiKey;
        Model = model;
        Endpoint = endpoint;
        Dimensions = dimensions;
    }

    public string Provider { get; }

    /// <summary>The model, or the Azure deployment.</summary>
    public string Model { get; }

    /// <summary>The endpoint, or <see langword="null"/> for api.openai.com.</summary>
    public Uri? Endpoint { get; }

    public int Dimensions { get; }

    /// <summary>What a report may say about the provider: never the key, and for Azure not the resource name.</summary>
    public string Describe() => Invariant($"{Provider}, model {Model}, {Dimensions} dimensions, at {Host}");

    private string Host => Endpoint is null
        ? "api.openai.com"
        : Provider == "azure" && Endpoint.Host.IndexOf('.', StringComparison.Ordinal) is var dot and > 0
            ? "<resource>" + Endpoint.Host[dot..]
            : Endpoint.Host;

    /// <summary>The provider's embedding generator, reporting <see cref="Model"/> and <see cref="Dimensions"/>.</summary>
    public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator()
    {
        var options = new OpenAIClientOptions();
        if (Endpoint is not null)
        {
            options.Endpoint = Endpoint;
        }

        return new OpenAIClient(new ApiKeyCredential(_apiKey), options)
            .GetEmbeddingClient(Model)
            .AsIEmbeddingGenerator(Dimensions);
    }

    public override string ToString() => Describe() + " (key redacted)";

    /// <summary>
    /// Reads the configuration. <see langword="null"/> with a message when the provider variable is unset (skip) or the
    /// configuration is incomplete (the message says what is missing).
    /// </summary>
    public static (EmbeddingProviderConfiguration? Configuration, string Message) Read(Func<string, string?> variable)
    {
        string? Get(string name) => variable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        var provider = Get(ProviderVariable)?.ToLowerInvariant();
        if (provider is null)
        {
            return (null,
                $"Hybrid column skipped: no embedding provider configured. Set {ProviderVariable}=openai with {OpenAiKeyVariable} "
                + $"(and optionally {OpenAiModelVariable}, {OpenAiBaseUrlVariable}, {DimensionsVariable}), or {ProviderVariable}=azure with "
                + $"{AzureEndpointVariable}, {AzureKeyVariable}, {AzureDeploymentVariable} and {DimensionsVariable} when the model is not one of "
                + $"{string.Join(", ", KnownDimensions.Keys)}.");
        }

        string? key, model;
        Uri? endpoint = null;
        switch (provider)
        {
            case "openai":
                key = Get(OpenAiKeyVariable);
                model = Get(OpenAiModelVariable) ?? DefaultOpenAiModel;
                if (Get(OpenAiBaseUrlVariable) is { } baseUrl && !Uri.TryCreate(baseUrl, UriKind.Absolute, out endpoint))
                {
                    return (null, $"{OpenAiBaseUrlVariable} is not an absolute URL.");
                }

                if (key is null)
                {
                    return (null, $"{ProviderVariable}=openai needs {OpenAiKeyVariable}.");
                }

                break;
            case "azure":
                key = Get(AzureKeyVariable);
                model = Get(AzureDeploymentVariable);
                if (key is null || model is null || Get(AzureEndpointVariable) is not { } azure)
                {
                    return (null, $"{ProviderVariable}=azure needs {AzureEndpointVariable}, {AzureKeyVariable} and {AzureDeploymentVariable}.");
                }

                if (!TryAzureEndpoint(azure, out var v1))
                {
                    return (null, $"{AzureEndpointVariable} must be an https URL such as https://<resource>.openai.azure.com/.");
                }

                endpoint = v1;
                break;
            default:
                return (null, $"{ProviderVariable} must be openai or azure.");
        }

        int dimensions;
        if (Get(DimensionsVariable) is { } text)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out dimensions)
                || dimensions is < 1 or > ExperienceEmbeddingDescriptor.MaxDimension)
            {
                return (null, Invariant($"{DimensionsVariable} must be a whole number from 1 to {ExperienceEmbeddingDescriptor.MaxDimension}."));
            }
        }
        else if (!KnownDimensions.TryGetValue(model, out dimensions))
        {
            return (null, $"{DimensionsVariable} is required for '{model}': its width is not known here.");
        }

        return (new EmbeddingProviderConfiguration(provider, key, model, endpoint, dimensions), string.Empty);
    }

    /// <summary>The Azure OpenAI v1 endpoint, as the live experiment builds it.</summary>
    private static bool TryAzureEndpoint(string value, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/openai/v1";
        }

        endpoint = new Uri(parsed.GetLeftPart(UriPartial.Authority) + path + "/");
        return true;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
