using System.ClientModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>The provider a live run talks to.</summary>
public enum LiveProvider
{
    Gemini,
    Azure,
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

    /// <summary>
    /// Prices the report can name without being told, with their source. Anything else needs the two price variables,
    /// or the report says the cost was not estimated.
    /// </summary>
    private static readonly Dictionary<string, (double Input, double Output, string Source)> KnownPrices = new(StringComparer.Ordinal)
    {
        [DefaultGeminiModel] = (0.25, 1.50, "ai.google.dev/gemini-api/docs/pricing, Standard paid tier, text input and output (thinking included), checked 2026-09-26"),
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

    /// <summary>The Gemini model, or the Azure deployment.</summary>
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

    /// <summary>
    /// What the report may know about the provider: its name, the model, and the endpoint's host -- for Azure with the
    /// resource name replaced, because an Azure host is the resource name and reports are committed to a public repository.
    /// </summary>
    public RunDescriptor Describe() => new(
        Provider == LiveProvider.Gemini ? "gemini" : "azure",
        Model,
        Provider == LiveProvider.Gemini ? Endpoint.Host : RedactedAzureHost(Endpoint.Host),
        InputPrice,
        OutputPrice,
        PriceSource);

    /// <summary>
    /// The provider's <see cref="IChatClient"/>: the OpenAI SDK pointed at the provider's OpenAI-compatible endpoint,
    /// adapted by Microsoft.Extensions.AI.OpenAI. The only place the key is used.
    /// </summary>
    public IChatClient CreateChatClient() =>
        new OpenAIClient(new ApiKeyCredential(_apiKey), new OpenAIClientOptions { Endpoint = Endpoint })
            .GetChatClient(Model)
            .AsIChatClient();

    public override string ToString() => $"{Describe().Provider} model={Model} host={Describe().EndpointHost} (key redacted)";

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

        if (chosen is null)
        {
            if (geminiKey is null && !azureAny)
            {
                return (ConfigurationOutcome.NotConfigured, null,
                    $"No provider is configured, so no live run was attempted and nothing was spent. Set {GeminiKeyVariable} "
                    + $"(and optionally {GeminiModelVariable}), or {AzureEndpointVariable}, {AzureKeyVariable} and {AzureDeploymentVariable}; "
                    + $"choose between them with {ProviderVariable}=gemini|azure. See experiments/AgentExperience.LiveReuse/README.md.");
            }

            if (geminiKey is not null && azureAny)
            {
                return (ConfigurationOutcome.Invalid, null, $"Both Gemini and Azure variables are set; choose one with {ProviderVariable}=gemini|azure.");
            }

            chosen = geminiKey is not null ? "gemini" : "azure";
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

            default:
                return (ConfigurationOutcome.Invalid, null, $"{ProviderVariable} must be 'gemini' or 'azure'.");
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
        else if (provider == LiveProvider.Gemini && KnownPrices.TryGetValue(model, out var known))
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
