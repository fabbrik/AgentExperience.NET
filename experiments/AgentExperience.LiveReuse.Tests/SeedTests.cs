using AgentExperience.LiveReuse.Harness;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

/// <summary>Pre-registration amendment 1: the seed is sent only to a provider that accepts the field.</summary>
public sealed class SeedTests
{
    [Fact]
    public void Gemini_and_anthropic_do_not_send_the_seed_and_azure_does()
    {
        var gemini = LiveConfiguration.Read(name => name == "GEMINI_API_KEY" ? "fake-key" : null, 600, 2_000_000).Configuration!;
        var azure = LiveConfiguration.Read(
            name => name switch
            {
                "AZURE_OPENAI_ENDPOINT" => "https://contoso-ai.openai.azure.com/",
                "AZURE_OPENAI_API_KEY" => "fake-key",
                "AZURE_OPENAI_DEPLOYMENT" => "gpt-4.1-mini",
                _ => null,
            },
            600,
            2_000_000).Configuration!;

        var anthropic = LiveConfiguration.Read(name => name == "ANTHROPIC_API_KEY" ? "fake-key" : null, 600, 2_000_000).Configuration!;

        Assert.False(gemini.Describe().SeedSent);
        Assert.True(azure.Describe().SeedSent);
        Assert.False(anthropic.Describe().SeedSent);

        // Only Anthropic is sent an output cap: its Messages API requires max_tokens. The others' requests are unchanged.
        Assert.Null(gemini.Describe().MaxOutputTokens);
        Assert.Null(azure.Describe().MaxOutputTokens);
        Assert.Equal(4096, anthropic.Describe().MaxOutputTokens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_call_carries_the_seed_exactly_when_the_descriptor_says_it_is_sent(bool seedSent)
    {
        var model = new ScriptedOperatorModel();
        var seeds = new SeedRecorder(model);
        var design = LivePreregistration.ReadEmbedded();

        var result = await LiveReuseExperiment.RunAsync(new LiveExperimentOptions
        {
            Model = seeds,
            Descriptor = TestSupport.ScriptedDescriptor with { SeedSent = seedSent },
            Clock = new SteppingClock(),
        });

        Assert.True(result.Complete);
        Assert.NotEmpty(seeds.Seeds);
        Assert.All(seeds.Seeds, seed => Assert.Equal(seedSent ? design.Seed : null, seed));
        Assert.Contains(seedSent ? "requested on every call" : "not sent", LiveReuseReport.Markdown(result), StringComparison.Ordinal);
    }

    private sealed class SeedRecorder(IChatClient inner) : DelegatingChatClient(inner)
    {
        public List<long?> Seeds { get; } = [];

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            lock (Seeds)
            {
                Seeds.Add(options?.Seed);
            }

            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
