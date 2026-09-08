using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.CompatibilityProof;

/// <summary>
/// Story 1.7, Track 2 (AC2): proves a minimal custom <see cref="AIContextProvider"/> (the "simple tier",
/// overriding only <c>ProvideAIContextAsync</c>) can deliver labeled retrieval output, a bounded-payload cap,
/// and a scope/eligibility gate -- end to end through a real <see cref="ChatClientAgent"/> invocation, by
/// inspecting the exact messages a recording fake <see cref="IChatClient"/> receives.
/// </summary>
/// <remarks>
/// <para>
/// Grounding: <c>story-1-7-research-digest.md</c>, Track 2.
/// </para>
/// <para>
/// <strong>Why <c>TextSearchProvider</c> was rejected in favor of a custom <see cref="AIContextProvider"/>:</strong>
/// <list type="bullet">
/// <item><description><c>TextSearchProvider</c>'s <c>TextSearchResult</c> exposes only
/// <c>SourceName</c>/<c>SourceLink</c>/<c>Text</c>/<c>RawRepresentation</c> -- there is no confidence/applicability
/// field, and <c>RawRepresentation</c> is documented as debug-only (not sent to the model). AgentExperience.NET
/// needs confidence carried into the model-visible context, which this type cannot express.</description></item>
/// <item><description><c>TextSearchProvider</c>, not the caller, constructs the search query from chat history --
/// there is no first-class way to hand it a specific, pre-computed result set for a given invocation.</description></item>
/// <item><description>It fail-open catches exceptions internally but has no timeout at all -- and per the
/// digest, neither <c>TextSearchProvider</c> nor the <see cref="AIContextProvider"/> base type has any built-in
/// timeout, so a caller-side timeout wrapper is required regardless of which path is chosen; it is not a reason
/// to prefer one over the other, only a shared gap either way.</description></item>
/// </list>
/// A custom <see cref="AIContextProvider"/> at the simple tier is the documented, supported path for arbitrary
/// metadata and full control over injection content, while still inheriting MAF's message-source stamping and
/// instructions/messages/tools merging for free.
/// Sources: https://github.com/microsoft/agent-framework/blob/dotnet-1.20.0/dotnet/src/Microsoft.Agents.AI/TextSearchProvider.cs ,
/// https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.textsearchprovider.textsearchresult ,
/// https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/context-providers
/// </para>
/// </remarks>
public class ContextProviderFitProof
{
    /// <summary>A candidate retrieval result before eligibility/cap filtering is applied.</summary>
    private sealed record RetrievalCandidate(string Scope, string Text, string Source, double Confidence);

    /// <summary>
    /// A fake <see cref="IChatClient"/> that records the exact message list it receives, so the test can
    /// inspect what a custom <see cref="AIContextProvider"/> actually injected after MAF's own merge logic ran.
    /// </summary>
    private sealed class RecordingFakeChatClient : IChatClient
    {
        public List<ChatMessage>? LastReceivedMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastReceivedMessages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Streaming is not exercised by this proof.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A minimal custom <see cref="AIContextProvider"/> at the simple tier: overrides only
    /// <c>ProvideAIContextAsync</c> and lets the base class handle merging/source-stamping. Applies a
    /// scope/eligibility stub gate (only candidates matching <c>eligibleScope</c> are considered) and a
    /// bounded-payload cap (<c>maxMessages</c>), and labels every injected message with a
    /// <c>Source</c>/<c>Confidence</c> pair via <see cref="ChatMessage.AdditionalProperties"/>.
    /// </summary>
    private sealed class LabeledRetrievalContextProvider(
        IReadOnlyList<RetrievalCandidate> candidates, string eligibleScope, int maxMessages) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            var eligible = candidates.Where(c => c.Scope == eligibleScope); // scope/eligibility stub gate
            var bounded = eligible.Take(maxMessages); // bounded-payload cap

            var messages = bounded
                .Select(c => new ChatMessage(ChatRole.User, c.Text)
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["Source"] = c.Source,
                        ["Confidence"] = c.Confidence,
                    },
                })
                .ToList<ChatMessage>();

            return new ValueTask<AIContext>(new AIContext { Messages = messages });
        }
    }

    [Fact]
    public async Task Custom_provider_injects_labeled_messages_within_scope_and_under_the_payload_cap()
    {
        var candidates = new[]
        {
            new RetrievalCandidate("tenant-a", "relevant experience 1", "experience-run:aaa", 0.92),
            new RetrievalCandidate("tenant-a", "relevant experience 2", "experience-run:bbb", 0.81),
            new RetrievalCandidate("tenant-a", "relevant experience 3, should be capped out", "experience-run:ccc", 0.50),
            new RetrievalCandidate("tenant-b", "wrong scope, must be excluded", "experience-run:zzz", 0.99),
        };

        var provider = new LabeledRetrievalContextProvider(candidates, eligibleScope: "tenant-a", maxMessages: 2);
        var chatClient = new RecordingFakeChatClient();
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [provider] });

        await agent.RunAsync("what should I know?");

        Assert.NotNull(chatClient.LastReceivedMessages);
        var injected = chatClient.LastReceivedMessages!
            .Where(m => m.AdditionalProperties is not null && m.AdditionalProperties.ContainsKey("Source"))
            .ToList();

        // Bounded-payload cap: 2 of the 3 tenant-a candidates were injected, not all 3.
        Assert.Equal(2, injected.Count);

        // Scope/eligibility gate: the tenant-b candidate never appears, capped or not.
        Assert.DoesNotContain(injected, m => m.Text.Contains("wrong scope", StringComparison.Ordinal));
        Assert.DoesNotContain(chatClient.LastReceivedMessages!, m => m.Text.Contains("wrong scope", StringComparison.Ordinal));

        // The capped-out third candidate is excluded, not merely reordered.
        Assert.DoesNotContain(injected, m => Equals(m.AdditionalProperties!["Confidence"], 0.50));

        // Labeled output: source + confidence are both present and correct on the surviving messages.
        Assert.Equal("experience-run:aaa", injected[0].AdditionalProperties!["Source"]);
        Assert.Equal(0.92, injected[0].AdditionalProperties!["Confidence"]);
        Assert.Equal("experience-run:bbb", injected[1].AdditionalProperties!["Source"]);
        Assert.Equal(0.81, injected[1].AdditionalProperties!["Confidence"]);

        // Original caller message is still present alongside the injected ones (default merge concatenates).
        Assert.Contains(chatClient.LastReceivedMessages!, m => m.Text == "what should I know?");
    }

    [Fact]
    public async Task Custom_provider_injects_nothing_when_no_candidate_is_in_scope()
    {
        var candidates = new[]
        {
            new RetrievalCandidate("tenant-b", "not this tenant", "experience-run:zzz", 0.99),
        };

        var provider = new LabeledRetrievalContextProvider(candidates, eligibleScope: "tenant-a", maxMessages: 5);
        var chatClient = new RecordingFakeChatClient();
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [provider] });

        await agent.RunAsync("anything relevant?");

        Assert.NotNull(chatClient.LastReceivedMessages);
        Assert.DoesNotContain(
            chatClient.LastReceivedMessages!,
            m => m.AdditionalProperties is not null && m.AdditionalProperties.ContainsKey("Source"));
    }
}
