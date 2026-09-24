using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.CompatibilityProof;

/// <summary>
/// Story 1.7, Track 1 (AC1): proves <c>Microsoft.Agents.AI</c> 1.22.0's <see cref="AIContextProvider"/>
/// invocation hooks observe both successful and failing <see cref="ChatClientAgent"/> invocations, using a
/// deterministic fake <see cref="IChatClient"/> so the proof needs no live model credentials.
/// </summary>
/// <remarks>
/// <para>
/// Grounding: <c>story-1-7-research-digest.md</c>, Track 1. Package: <c>Microsoft.Agents.AI</c> 1.22.0 (GA).
/// The digest was written against 1.20.0; story 5.1 moved the pin and re-checked the call sites below at 1.22.0.
/// Source: https://www.nuget.org/packages/Microsoft.Agents.AI/ .
/// </para>
/// <para>
/// <strong>Which agent types this hook actually fires for</strong> (confirmed against
/// <c>ChatClientAgent.cs</c> at tag <c>dotnet-1.22.0</c> for the types below; the digest's finding on
/// <c>A2AAgent</c>/custom subclasses is a documented, not independently re-verified, observation since
/// those types are out of scope for this proof project's dependencies):
/// <list type="bullet">
/// <item><description><see cref="ChatClientAgent"/>: both the invoking-context hook
/// (<c>AIContextProvider.InvokingCoreAsync</c>, called from <c>PrepareSessionAndMessagesAsync</c>) and the
/// invoked-context hook (<c>InvokedCoreAsync</c>, called from both the success path in
/// <c>RunCoreAsync</c>/<c>RunStreamingAsync</c> and the failure path via
/// <c>NotifyProvidersOfFailureAtEndOfRunAsync</c>) fire for every run. <em>This file</em> proves only the
/// invoked-side half (<c>InvokedCoreAsync</c>/<c>StoreAIContextAsync</c>, both success and failure) -- the
/// invoking-side half is proven end-to-end by <c>ContextProviderFitProof.cs</c>'s tests, which inspect the
/// exact messages a fake <c>IChatClient</c> receives after <c>InvokingCoreAsync</c>/<c>ProvideAIContextAsync</c>
/// ran.
/// Source: https://github.com/microsoft/agent-framework/blob/dotnet-1.22.0/dotnet/src/Microsoft.Agents.AI/ChatClient/ChatClientAgent.cs</description></item>
/// <item><description>Hand-rolled <see cref="AIAgent"/> subclasses: <see cref="AIContextProvider"/> hooks are
/// <em>not</em> automatic -- they fire only if the subclass's own <c>RunCoreAsync</c> override chooses to call
/// <see cref="AIContextProvider.InvokingAsync"/>/<see cref="AIContextProvider.InvokedAsync"/> itself, the way
/// <see cref="ChatClientAgent"/> does internally.</description></item>
/// <item><description><c>A2AAgent</c> (remote-proxy agent, separate <c>Microsoft.Agents.AI.A2A</c> package, not
/// referenced by this proof project): function-invocation middleware is documented as not applying, since tools
/// execute server-side for that agent type.
/// Source: https://learn.microsoft.com/en-us/agent-framework/agents/middleware/</description></item>
/// </list>
/// </para>
/// </remarks>
public class MafHooksProof
{
    /// <summary>
    /// A deterministic fake <see cref="IChatClient"/>: returns a fixed assistant reply for any input, except
    /// when the last message's text contains <see cref="FailTrigger"/>, in which case it synchronously throws
    /// <see cref="ThrownException"/>. No network or model call is made, so results are fully reproducible.
    /// </summary>
    private sealed class DeterministicFakeChatClient : IChatClient
    {
        public const string FailTrigger = "fail-me";

        public static readonly InvalidOperationException ThrownException = new("deterministic fake chat client failure");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var lastText = messages.LastOrDefault()?.Text ?? string.Empty;
            if (lastText.Contains(FailTrigger, StringComparison.Ordinal))
            {
                throw ThrownException;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "deterministic ok")));
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
    /// An "advanced tier" <see cref="AIContextProvider"/> that overrides <c>InvokedCoreAsync</c> directly
    /// (rather than the "simple tier" <c>StoreAIContextAsync</c>) and records every
    /// <see cref="AIContextProvider.InvokedContext.InvokeException"/> it observes, per the digest's finding
    /// that this is the override point required to see failed invocations.
    /// </summary>
    private sealed class FailureObservingContextProvider : AIContextProvider
    {
        public List<Exception?> ObservedInvokeExceptions { get; } = [];

        public List<int> ObservedResponseMessageCounts { get; } = [];

        // No MAAI001 suppression. The one this file used to carry was dead code: at both 1.20.0 and 1.22.0,
        // only the InvokingContext/InvokedContext constructors in AIContextProvider.cs are [Experimental], and
        // overriding InvokedCoreAsync/StoreAIContextAsync constructs neither. If a later pin marks these
        // members experimental, the build (warnings as errors) says so.
        protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            ObservedInvokeExceptions.Add(context.InvokeException);
            ObservedResponseMessageCounts.Add(context.ResponseMessages?.Count() ?? 0);
            return default;
        }
    }

    /// <summary>
    /// A "simple tier" <see cref="AIContextProvider"/> that overrides only <c>StoreAIContextAsync</c> (never
    /// <c>InvokedCoreAsync</c>), used to prove -- by actually running a failing invocation, not just reading
    /// the source -- that this tier alone never observes a failure.
    /// </summary>
    private sealed class SimpleTierOnlyContextProvider : AIContextProvider
    {
        public int StoreAIContextAsyncCallCount { get; private set; }

        protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            StoreAIContextAsyncCallCount++;
            return default;
        }
    }

    private static ChatClientAgent CreateAgent(AIContextProvider provider) =>
        new(new DeterministicFakeChatClient(), new ChatClientAgentOptions { AIContextProviders = [provider] });

    [Fact]
    public async Task InvokedCoreAsync_observes_a_successful_invocation_with_a_null_InvokeException()
    {
        var provider = new FailureObservingContextProvider();
        var agent = CreateAgent(provider);

        var response = await agent.RunAsync("succeed please");

        Assert.NotNull(response);
        var invokeException = Assert.Single(provider.ObservedInvokeExceptions);
        Assert.Null(invokeException);
        Assert.Equal(1, Assert.Single(provider.ObservedResponseMessageCounts));
    }

    [Fact]
    public async Task InvokedCoreAsync_observes_a_failing_invocation_via_InvokeException()
    {
        var provider = new FailureObservingContextProvider();
        var agent = CreateAgent(provider);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync(DeterministicFakeChatClient.FailTrigger));

        Assert.Same(DeterministicFakeChatClient.ThrownException, thrown);
        var observedException = Assert.Single(provider.ObservedInvokeExceptions);
        Assert.Same(DeterministicFakeChatClient.ThrownException, observedException);
    }

    [Fact]
    public async Task Simple_tier_StoreAIContextAsync_override_is_never_called_for_a_failed_invocation()
    {
        // This is the digest's critical finding, proven by actually running it rather than asserted from
        // reading the source: AIContextProvider's default InvokedCoreAsync short-circuits to `return default;`
        // before calling StoreAIContextAsync whenever InvokedContext.InvokeException is not null. A provider
        // that only overrides the "simple tier" therefore never sees failed invocations.
        var provider = new SimpleTierOnlyContextProvider();
        var agent = CreateAgent(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync(DeterministicFakeChatClient.FailTrigger));

        Assert.Equal(0, provider.StoreAIContextAsyncCallCount);
    }

    [Fact]
    public async Task Simple_tier_StoreAIContextAsync_override_is_called_for_a_successful_invocation()
    {
        // Contrast case: the same simple-tier provider *does* get called when the invocation succeeds,
        // confirming the short-circuit above is specific to the failure path, not a general limitation.
        var provider = new SimpleTierOnlyContextProvider();
        var agent = CreateAgent(provider);

        await agent.RunAsync("succeed please");

        Assert.Equal(1, provider.StoreAIContextAsyncCallCount);
    }
}
