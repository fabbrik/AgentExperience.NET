using AgentExperience.LiveReuse.Harness;

namespace AgentExperience.LiveReuse.Tests;

public sealed class ConfigurationTests
{
    private const string FakeGeminiKey = "not-a-real-gemini-key-should-never-appear";
    private const string FakeAzureKey = "fake-azure-key-9f8e7d6c5b4a-should-never-appear";

    private static (ConfigurationOutcome Outcome, LiveConfiguration? Configuration, string Message) Read(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal);
        return LiveConfiguration.Read(name => map.GetValueOrDefault(name), 600, 2_000_000);
    }

    [Fact]
    public void No_provider_variable_means_not_configured_and_says_how_to_configure()
    {
        var (outcome, configuration, message) = Read();
        Assert.Equal(ConfigurationOutcome.NotConfigured, outcome);
        Assert.Null(configuration);
        Assert.Contains("GEMINI_API_KEY", message, StringComparison.Ordinal);
        Assert.Contains("nothing was spent", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gemini_key_alone_selects_gemini_with_the_default_model_and_the_openai_compatible_endpoint()
    {
        var (outcome, configuration, _) = Read(("GEMINI_API_KEY", FakeGeminiKey));
        Assert.Equal(ConfigurationOutcome.Ready, outcome);
        Assert.Equal(LiveProvider.Gemini, configuration!.Provider);
        Assert.Equal("gemini-3.1-flash-lite", configuration.Model);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/", configuration.Endpoint.ToString());
        Assert.Null(configuration.Budget);
        Assert.Equal(0.25, configuration.InputPrice);
        Assert.Equal(1.50, configuration.OutputPrice);
    }

    [Fact]
    public void Gemini_model_can_be_overridden_and_an_unknown_model_has_no_built_in_price()
    {
        var (_, configuration, _) = Read(("GEMINI_API_KEY", FakeGeminiKey), ("GEMINI_MODEL", "gemini-3.5-flash"));
        Assert.Equal("gemini-3.5-flash", configuration!.Model);
        Assert.Null(configuration.InputPrice);
        Assert.StartsWith("none", configuration.PriceSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://contoso-ai.openai.azure.com", "https://contoso-ai.openai.azure.com/openai/v1/")]
    [InlineData("https://contoso-ai.openai.azure.com/", "https://contoso-ai.openai.azure.com/openai/v1/")]
    [InlineData("https://contoso-ai.openai.azure.com/openai/v1/", "https://contoso-ai.openai.azure.com/openai/v1/")]
    [InlineData("https://contoso-ai.services.ai.azure.com/openai/v1", "https://contoso-ai.services.ai.azure.com/openai/v1/")]
    public void Azure_uses_the_v1_endpoint(string configured, string expected)
    {
        var (outcome, configuration, _) = Read(
            ("AZURE_OPENAI_ENDPOINT", configured), ("AZURE_OPENAI_API_KEY", FakeAzureKey), ("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini"));
        Assert.Equal(ConfigurationOutcome.Ready, outcome);
        Assert.Equal(LiveProvider.Azure, configuration!.Provider);
        Assert.Equal(expected, configuration.Endpoint.ToString());
        Assert.Equal("<resource>" + new Uri(expected).Host[new Uri(expected).Host.IndexOf('.', StringComparison.Ordinal)..], configuration.Describe().EndpointHost);
        Assert.DoesNotContain("contoso", configuration.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://contoso-ai.openai.azure.com/")]
    [InlineData("https://contoso-ai.openai.azure.com/?api-key=secret-in-query")]
    [InlineData("https://user:secret-in-userinfo@contoso-ai.openai.azure.com/")]
    [InlineData("not a url")]
    public void A_malformed_azure_endpoint_is_refused_without_echoing_it(string endpoint)
    {
        var (outcome, _, message) = Read(
            ("AGENTEXPERIENCE_LIVE_PROVIDER", "azure"), ("AZURE_OPENAI_ENDPOINT", endpoint), ("AZURE_OPENAI_API_KEY", FakeAzureKey), ("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini"));
        Assert.Equal(ConfigurationOutcome.Invalid, outcome);
        Assert.DoesNotContain("secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partly_configured_azure_names_what_is_missing()
    {
        var (outcome, _, message) = Read(("AZURE_OPENAI_ENDPOINT", "https://contoso-ai.openai.azure.com/"));
        Assert.Equal(ConfigurationOutcome.Invalid, outcome);
        Assert.Contains("AZURE_OPENAI_API_KEY", message, StringComparison.Ordinal);
        Assert.Contains("AZURE_OPENAI_DEPLOYMENT", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_providers_configured_without_a_choice_is_refused_and_a_choice_resolves_it()
    {
        (string, string)[] both = [("GEMINI_API_KEY", FakeGeminiKey), ("AZURE_OPENAI_ENDPOINT", "https://contoso-ai.openai.azure.com/"), ("AZURE_OPENAI_API_KEY", FakeAzureKey), ("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini")];
        Assert.Equal(ConfigurationOutcome.Invalid, Read(both).Outcome);
        Assert.Equal(LiveProvider.Gemini, Read([.. both, ("AGENTEXPERIENCE_LIVE_PROVIDER", "gemini")]).Configuration!.Provider);
        Assert.Equal(LiveProvider.Azure, Read([.. both, ("AGENTEXPERIENCE_LIVE_PROVIDER", "AZURE")]).Configuration!.Provider);
        Assert.Equal(ConfigurationOutcome.Invalid, Read([.. both, ("AGENTEXPERIENCE_LIVE_PROVIDER", "openai")]).Outcome);
    }

    [Fact]
    public void The_budget_and_prices_come_from_variables_and_bad_values_are_refused()
    {
        var (_, configuration, _) = Read(
            ("GEMINI_API_KEY", FakeGeminiKey), ("AGENTEXPERIENCE_LIVE_MAX_CALLS", "50"), ("AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK", "0.1"), ("AGENTEXPERIENCE_LIVE_PRICE_OUTPUT_PER_MTOK", "0.4"));
        Assert.Equal(new LiveBudget(50, 2_000_000), configuration!.Budget);
        Assert.Equal(0.1, configuration.InputPrice);

        Assert.Equal(ConfigurationOutcome.Invalid, Read(("GEMINI_API_KEY", FakeGeminiKey), ("AGENTEXPERIENCE_LIVE_MAX_TOKENS", "-5")).Outcome);
        Assert.Equal(ConfigurationOutcome.Invalid, Read(("GEMINI_API_KEY", FakeGeminiKey), ("AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK", "0.1")).Outcome);
        Assert.Equal(ConfigurationOutcome.Invalid, Read(("GEMINI_API_KEY", FakeGeminiKey), ("GEMINI_MODEL", "../../etc")).Outcome);
    }

    [Fact]
    public void The_key_never_appears_in_the_configuration_text_or_the_descriptor()
    {
        var gemini = Read(("GEMINI_API_KEY", FakeGeminiKey)).Configuration!;
        var azure = Read(("AZURE_OPENAI_ENDPOINT", "https://contoso-ai.openai.azure.com/"), ("AZURE_OPENAI_API_KEY", FakeAzureKey), ("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini")).Configuration!;

        foreach (var (configuration, key) in new[] { (gemini, FakeGeminiKey), (azure, FakeAzureKey) })
        {
            Assert.DoesNotContain(key, configuration.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(key, configuration.Describe().ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("/openai/", configuration.Describe().EndpointHost, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_provider_client_is_built_without_a_network_call()
    {
        using var client = Read(("GEMINI_API_KEY", FakeGeminiKey)).Configuration!.CreateChatClient();
        var metadata = (Microsoft.Extensions.AI.ChatClientMetadata?)client.GetService(typeof(Microsoft.Extensions.AI.ChatClientMetadata));
        Assert.Equal("gemini-3.1-flash-lite", metadata?.DefaultModelId);
        Assert.Equal("generativelanguage.googleapis.com", metadata?.ProviderUri?.Host);
    }
}
