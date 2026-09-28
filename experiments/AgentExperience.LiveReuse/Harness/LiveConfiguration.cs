using System.ClientModel;
using System.Globalization;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>The provider a live run talks to.</summary>
public enum LiveProvider
{
    Gemini,
    Azure,
    Anthropic,
}

/// <summary>What reading the environment decided.</summary>
public enum ConfigurationOutcome
{
    /// <summary>A provider is fully configured; the run may start.</summary>
    Ready,

    /// <summary>No provider variable is set at all. The experiment skips, successfully, and says how to configure one.</summary>
    NotConfigured,

    /// <summary>A provider was asked for, or partly configured, and something it needs is missing or malformed.</summary>
    Invalid,
}

/// <summary>
/// The live run's configuration, read from environment variables only. The API key is held here and nowhere else:
/// it is handed to the provider SDK by <see cref="CreateChatClient"/> and never to the harness, the report, or a log.
/// </summary>
/// <remarks>
/// A class, not a record, on purpose: a record's generated <c>ToString</c> would print every property, the key
/// included. <see cref="ToString"/> here prints nothing secret.
/// </remarks>
public sealed class LiveConfiguration
{
    public const string ProviderVariable = "AGENTEXPERIENCE_LIVE_PROVIDER";
    public const string GeminiKeyVariable = "GEMINI_API_KEY";
    public const string GeminiModelVariable = "GEMINI_MODEL";
    public const string AzureEndpointVariable = "AZURE_OPENAI_ENDPOINT";
    public const string AzureKeyVariable = "AZURE_OPENAI_API_KEY";
    public const string AzureDeploymentVariable = "AZURE_OPENAI_DEPLOYMENT";
    public const string AnthropicKeyVariable = "ANTHROPIC_API_KEY";
    public const string AnthropicModelVariable = "ANTHROPIC_MODEL";
    public const string MaxCallsVariable = "AGENTEXPERIENCE_LIVE_MAX_CALLS";
    public const string MaxTokensVariable = "AGENTEXPERIENCE_LIVE_MAX_TOKENS";
    public const string MinIntervalVariable = "AGENTEXPERIENCE_LIVE_MIN_CALL_INTERVAL_MS";
    public const string InputPriceVariable = "AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK";
    public const string OutputPriceVariable = "AGENTEXPERIENCE_LIVE_PRICE_OUTPUT_PER_MTOK";
    public const string ResultsDirectoryVariable = "AGENTEXPERIENCE_LIVE_RESULTS_DIR";

    /// <summary>
    /// Gemini's current stable low-cost model (ai.google.dev/gemini-api/docs/models, checked 2026-09-26: "Gemini 3.1
    /// Flash-Lite is recommended as a stable, long-term model optimized for low-cost and high-volume tasks").
    /// </summary>
    public const string DefaultGeminiModel = "gemini-3.1-flash-lite";

    /// <summary>Gemini's OpenAI-compatible Chat Completions base URL (ai.google.dev/gemini-api/docs/openai).</summary>
    public static Uri GeminiEndpoint { get; } = new("https://generativelanguage.googleapis.com/v1beta/openai/");

    /// <summary>Anthropic's low-cost current model (the alias, which follows the latest Claude Haiku 4.5 snapshot).</summary>
    public const string DefaultAnthropicModel = "claude-haiku-4-5";

    /// <summary>The Anthropic API's base URL, set explicitly so an ambient <c>ANTHROPIC_BASE_URL</c> cannot redirect a run.</summary>
    public const string AnthropicBaseUrl = "https://api.anthropic.com";

    /// <summary><see cref="AnthropicBaseUrl"/> as a URI, for the report's host.</summary>
    public static Uri AnthropicEndpoint { get; } = new(AnthropicBaseUrl);

    /// <summary>
    /// The per-call output cap sent to Anthropic. The Messages API requires <c>max_tokens</c> on every request; the other
    /// providers are sent none. Generous for this task, where a reply is a few tool calls or a short sentence.
    /// </summary>
    public const int AnthropicMaxOutputTokens = 4096;

    /// <summary>
    /// Prices the report can name without being told, with their source. Anything else needs the two price variables,
    /// or the report says the cost was not estimated.
    /// </summary>
    private static readonly Dictionary<(LiveProvider Provider, string Model), (double Input, double Output, string Source)> KnownPrices = new()
    {
        [(LiveProvider.Gemini, DefaultGeminiModel)] = (0.25, 1.50, "ai.google.dev/gemini-api/docs/pricing, Standard paid tier, text input and output (thinking included), checked 2026-09-26"),
        [(LiveProvider.Anthropic, DefaultAnthropicModel)] = (1.00, 5.00, "Anthropic API pricing, standard rates for Claude Haiku 4.5, input and output, no batch or prompt-cache discount"),
    };

    private readonly string _apiKey;

    private LiveConfiguration(LiveProvider provider, string apiKey, string model, Uri endpoint, LiveBudget? budget, TimeSpan minimumInterval, double? inputPrice, double? outputPrice, string priceSource, string? resultsDirectory)
    {
        Provider = provider;
        _apiKey = apiKey;
        Model = model;
        Endpoint = endpoint;
        Budget = budget;
        MinimumCallInterval = minimumInterval;
        InputPrice = inputPrice;
        OutputPrice = outputPrice;
        PriceSource = priceSource;
        ResultsDirectory = resultsDirectory;
    }

    public LiveProvider Provider { get; }

    /// <summary>The Gemini or Anthropic model, or the Azure deployment.</summary>
    public string Model { get; }

    /// <summary>The full endpoint the SDK is pointed at. Only its host ever reaches a report.</summary>
    public Uri Endpoint { get; }

    /// <summary>The budget override, or <see langword="null"/> for the pre-registered defaults.</summary>
    public LiveBudget? Budget { get; }

    public TimeSpan MinimumCallInterval { get; }

    public double? InputPrice { get; }

    public double? OutputPrice { get; }

    public string PriceSource { get; }

    public string? ResultsDirectory { get; }

    /// <summary>The provider's name as reports and the ledger spell it.</summary>
    public string ProviderName => Provider switch
    {
        LiveProvider.Gemini => "gemini",
        LiveProvider.Azure => "azure",
        _ => "anthropic",
    };

    /// <summary>
    /// What the report may know about the provider: its name, the model, and the endpoint's host -- for Azure with the
    /// resource name replaced, because an Azure host is the resource name and reports are committed to a public repository.
    /// </summary>
    public RunDescriptor Describe() => new(
        ProviderName,
        Model,
        Provider == LiveProvider.Azure ? RedactedAzureHost(Endpoint.Host) : Endpoint.Host,
        InputPrice,
        OutputPrice,
        PriceSource)
    {
        // Only Azure accepts a seed: Gemini's OpenAI-compatible endpoint rejects the field (amendment 1), and the
        // Anthropic Messages API has no such parameter.
        SeedSent = Provider == LiveProvider.Azure,
        MaxOutputTokens = Provider == LiveProvider.Anthropic ? AnthropicMaxOutputTokens : null,
    };

    /// <summary>
    /// The provider's <see cref="IChatClient"/>. The only place the key is used. Gemini and Azure: the OpenAI SDK pointed
    /// at the provider's OpenAI-compatible endpoint, adapted by Microsoft.Extensions.AI.OpenAI. Anthropic: Anthropic's own
    /// SDK and its <c>AsIChatClient</c> adapter, with the key and the base URL set here rather than read from the
    /// environment, and <see cref="AnthropicMaxOutputTokens"/> as the default <c>max_tokens</c>. No thinking mode is
    /// requested: the harness never sets <see cref="ChatOptions.Reasoning"/>.
    /// </summary>
    public IChatClient CreateChatClient() => Provider switch
    {
        LiveProvider.Anthropic =>
            new AnthropicClient { ApiKey = _apiKey, BaseUrl = AnthropicBaseUrl }
                .AsIChatClient(Model, AnthropicMaxOutputTokens),
        _ =>
            new OpenAIClient(new ApiKeyCredential(_apiKey), new OpenAIClientOptions { Endpoint = Endpoint })
                .GetChatClient(Model)
                .AsIChatClient(),
    };

    public override string ToString() => $"{ProviderName} model={Model} host={Describe().EndpointHost} (key redacted)";

    internal static string RedactedAzureHost(string host) =>
        host.IndexOf('.', StringComparison.Ordinal) is var dot and > 0 ? "<resource>" + host[dot..] : "<resource>";

    /// <summary>Reads the configuration. <paramref name="variable"/> is the environment in a live run, a dictionary in tests.</summary>
    public static (ConfigurationOutcome Outcome, LiveConfiguration? Configuration, string Message) Read(Func<string, string?> variable, int preregisteredMaxCalls, long preregisteredMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(variable);

        string? Get(string name) => variable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        var chosen = Get(ProviderVariable)?.ToLowerInvariant();
        var geminiKey = Get(GeminiKeyVariable);
        var azureAny = Get(AzureEndpointVariable) is not null || Get(AzureKeyVariable) is not null || Get(AzureDeploymentVariable) is not null;
        var anthropicKey = Get(AnthropicKeyVariable);

        if (chosen is null)
        {
            var configured = new List<string>(3);
            if (geminiKey is not null)
            {
                configured.Add("gemini");
            }

            if (azureAny)
            {
                configured.Add("azure");
            }

            if (anthropicKey is not null)
            {
                configured.Add("anthropic");
            }

            switch (configured.Count)
            {
                case 0:
                    return (ConfigurationOutcome.NotConfigured, null,
                        $"No provider is configured, so no live run was attempted and nothing was spent. Set {GeminiKeyVariable} "
                        + $"(and optionally {GeminiModelVariable}), or {AzureEndpointVariable}, {AzureKeyVariable} and {AzureDeploymentVariable}, "
                        + $"or {AnthropicKeyVariable} (and optionally {AnthropicModelVariable}); "
                        + $"choose between them with {ProviderVariable}=gemini|azure|anthropic. See experiments/AgentExperience.LiveReuse/README.md.");
                case 1:
                    chosen = configured[0];
                    break;
                default:
                    return (ConfigurationOutcome.Invalid, null,
                        $"Variables for more than one provider are set ({string.Join(", ", configured)}); choose one with {ProviderVariable}=gemini|azure|anthropic.");
            }
        }

        LiveBudget? budget = null;
        var maxCalls = Get(MaxCallsVariable);
        var maxTokens = Get(MaxTokensVariable);
        if (maxCalls is not null || maxTokens is not null)
        {
            if (!TryPositive(maxCalls, preregisteredMaxCalls, out var calls) || !TryPositive(maxTokens, preregisteredMaxTokens, out var tokens))
            {
                return (ConfigurationOutcome.Invalid, null, $"{MaxCallsVariable} and {MaxTokensVariable} must be positive integers.");
            }

            budget = new LiveBudget((int)Math.Min(calls, int.MaxValue), tokens);
        }

        var interval = TimeSpan.Zero;
        if (Get(MinIntervalVariable) is { } intervalText)
        {
            if (!int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            {
                return (ConfigurationOutcome.Invalid, null, $"{MinIntervalVariable} must be a non-negative integer number of milliseconds.");
            }

            interval = TimeSpan.FromMilliseconds(milliseconds);
        }

        string apiKey;
        string model;
        Uri endpoint;
        LiveProvider provider;

        switch (chosen)
        {
            case "gemini":
                if (geminiKey is null)
                {
                    return (ConfigurationOutcome.Invalid, null, $"{ProviderVariable}=gemini, but {GeminiKeyVariable} is not set.");
                }

                provider = LiveProvider.Gemini;
                apiKey = geminiKey;
                model = Get(GeminiModelVariable) ?? DefaultGeminiModel;
                endpoint = GeminiEndpoint;
                break;

            case "azure":
                var missing = new[] { AzureEndpointVariable, AzureKeyVariable, AzureDeploymentVariable }.Where(name => Get(name) is null).ToList();
                if (missing.Count > 0)
                {
                    return (ConfigurationOutcome.Invalid, null, $"The Azure provider needs {string.Join(", ", missing)}, which {(missing.Count == 1 ? "is" : "are")} not set.");
                }

                if (!TryAzureEndpoint(Get(AzureEndpointVariable)!, out endpoint))
                {
                    // The value is not echoed: an endpoint can carry a resource name someone did not mean to publish.
                    return (ConfigurationOutcome.Invalid, null, $"{AzureEndpointVariable} must be an https URL such as https://<resource>.openai.azure.com/.");
                }

                provider = LiveProvider.Azure;
                apiKey = Get(AzureKeyVariable)!;
                model = Get(AzureDeploymentVariable)!;
                break;

            case "anthropic":
                if (anthropicKey is null)
                {
                    return (ConfigurationOutcome.Invalid, null, $"{ProviderVariable}=anthropic, but {AnthropicKeyVariable} is not set.");
                }

                provider = LiveProvider.Anthropic;
                apiKey = anthropicKey;
                model = Get(AnthropicModelVariable) ?? DefaultAnthropicModel;
                endpoint = AnthropicEndpoint;
                break;

            default:
                return (ConfigurationOutcome.Invalid, null, $"{ProviderVariable} must be 'gemini', 'azure' or 'anthropic'.");
        }

        if (model.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')))
        {
            return (ConfigurationOutcome.Invalid, null, "The model or deployment name may contain only letters, digits, '-', '.' and '_'.");
        }

        var inputText = Get(InputPriceVariable);
        var outputText = Get(OutputPriceVariable);
        double? inputPrice = null;
        double? outputPrice = null;
        string priceSource;
        if (inputText is not null || outputText is not null)
        {
            if (!double.TryParse(inputText, NumberStyles.Float, CultureInfo.InvariantCulture, out var input) || input < 0
                || !double.TryParse(outputText, NumberStyles.Float, CultureInfo.InvariantCulture, out var output) || output < 0)
            {
                return (ConfigurationOutcome.Invalid, null, $"Set both {InputPriceVariable} and {OutputPriceVariable} as non-negative USD per million tokens.");
            }

            (inputPrice, outputPrice, priceSource) = (input, output, $"{InputPriceVariable} and {OutputPriceVariable}, as set for this run");
        }
        else if (KnownPrices.TryGetValue((provider, model), out var known))
        {
            (inputPrice, outputPrice, priceSource) = (known.Input, known.Output, known.Source);
        }
        else
        {
            priceSource = $"none: no built-in price for this model; set {InputPriceVariable} and {OutputPriceVariable} to estimate cost";
        }

        return (ConfigurationOutcome.Ready,
            new LiveConfiguration(provider, apiKey, model, endpoint, budget, interval, inputPrice, outputPrice, priceSource, Get(ResultsDirectoryVariable)),
            string.Empty);
    }

    /// <summary>
    /// The Azure OpenAI v1 endpoint: <c>https://&lt;resource&gt;.openai.azure.com/openai/v1/</c>. A bare resource URL gets
    /// the v1 path appended; one that already ends in it is kept.
    /// </summary>
    internal static bool TryAzureEndpoint(string value, out Uri endpoint)
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

    private static bool TryPositive(string? text, long fallback, out long value)
    {
        if (text is null)
        {
            value = fallback;
            return true;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }
}
