using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// The one-call setup's agent-level injection: runs an <see cref="ExperienceContextProvider"/> as a MAF
/// <see cref="MessageAIContextProvider"/>, so <c>UseAgentExperience</c> can wire injection through
/// <see cref="AIAgentBuilder.UseAIContextProviders"/> on an agent that was not given the provider on its options.
/// </summary>
/// <remarks>
/// <para>
/// The provider runs unchanged: MAF's own filtering, merging and source stamping, the same block, the same session
/// account and the same settling. Over a <see cref="ChatClientAgent"/> it also sees what that agent would hand it on
/// its options, by the rules the agent applies before it runs the providers on its options:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>The chat history</b> the agent replays goes to the derived task text, so a short follow-up is joined with an
/// earlier turn's request. It is read only when the agent would read it: not under a conversation ID (on the session or
/// in the chat options), not when the agent persists history per service call, not on a run that continues a
/// background response, and not when the agent would refuse the run. The history provider is the agent's own, or the
/// one a run passes in the additional properties. An <see cref="InMemoryChatHistoryProvider"/> is read without side
/// effects, from its stored messages: its reducer is not run a second time, so with a reducer that runs before
/// retrieval, or an output filter, the join reads the history as stored rather than the reduced or filtered view the
/// model gets (the latest turns, which the join reads, are the ones a reducer keeps). Any other history provider is
/// asked the way the agent asks it, so it is read once more per run than the agent alone reads it, with whatever cost
/// or side effect a read has; that read is bounded by <see cref="HistoryReadTimeout"/> and the caller's cancellation.
/// A read that fails or times out joins nothing, never fails the run, and is reported on the injection span
/// (<c>agentexperience.history_read</c>). With no session the agent starts an empty one, and so does this read.
/// </description></item>
/// <item><description>
/// <b>The tools</b> are the run's own <see cref="ChatClientAgentRunOptions"/> tools followed by the agent's default
/// tools, merged as the agent merges them, with the run options read from <see cref="AIAgent.CurrentRunContext"/>; with
/// <see cref="ReceivingAgentCapabilities.UseRunTools"/> a run-only tool counts.
/// </description></item>
/// <item><description>
/// <b>A continued background response</b> (a continuation token on the run options or the chat options) gets no
/// injection, as the agent runs no providers for it.
/// </description></item>
/// </list>
/// <para>
/// What it still cannot see: messages or tools that a context provider on the agent's options adds, and run options or
/// a session that middleware between this and the agent replaces. When the current run context is not this pipeline's,
/// no history is read and only the default tools are seen. An agent that is not a <see cref="ChatClientAgent"/> offers
/// the caller's messages only, and the tools of the <see cref="ChatOptions"/> it reports through
/// <see cref="AIAgent.GetService"/> (none when it reports none); tools it adds itself, server-side tools, and
/// function-invocation middleware's additional tools are never seen. The session tracked is the one the caller passed;
/// with none, nothing is tracked.
/// </para>
/// <para>Like the provider, it never throws into the invocation except for the caller's own cancellation.</para>
/// </remarks>
internal sealed class ExperienceMessageContextProvider(ExperienceContextProvider provider) : MessageAIContextProvider
{
    /// <summary>
    /// The bound on one agent-level read of a custom history provider: 500 milliseconds, as
    /// <c>RetrievalPolicy.DefaultTimeout</c>, measured with <see cref="ExperienceInjectionOptions.TimeProvider"/>. Past
    /// it the read is abandoned and nothing is joined.
    /// </summary>
    internal static readonly TimeSpan HistoryReadTimeout = TimeSpan.FromMilliseconds(500);

    private const string HistoryReadFailed = "failed";
    private const string HistoryReadTimedOut = "timed_out";

    private readonly AsyncLocal<IReadOnlyList<ChatMessage>?> _unfilteredRequest = new();
    private readonly AsyncLocal<string?> _historyRead = new();

    /// <summary>The provider this runs.</summary>
    internal ExperienceContextProvider Provider { get; } = provider;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => Provider.StateKeys;

    /// <summary>
    /// Keeps the unfiltered request messages for the derived task text, with the history a <see cref="ChatClientAgent"/>
    /// replays before them, as the provider does on that agent's options; then runs MAF's own filtering, merging and
    /// source stamping unchanged. The messages handed on to the agent are the caller's: the history is never added. A
    /// run that continues a background response is handed on untouched.
    /// </summary>
    protected override async ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (context is not null && IsContinuation(context.Agent))
        {
            // The agent runs no context providers when it continues a background response.
            return context.RequestMessages;
        }

        IReadOnlyList<ChatMessage>? unfiltered = null;
        try
        {
            if (context?.RequestMessages is { } messages)
            {
                if (messages is IReadOnlyList<ChatMessage> list)
                {
                    unfiltered = list;
                }
                else
                {
                    var materialized = messages.ToArray();
                    context.RequestMessages = materialized;
                    unfiltered = materialized;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never into the invocation: without it the derived text reads the filtered messages.
        }

        string? historyRead = null;
        if (unfiltered is not null)
        {
            (unfiltered, historyRead) = await WithReplayedHistoryAsync(context!.Agent, context.Session, unfiltered, cancellationToken).ConfigureAwait(false);
        }

        _unfilteredRequest.Value = unfiltered;
        _historyRead.Value = historyRead;
        try
        {
            return await base.InvokingCoreAsync(context!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _unfilteredRequest.Value = null;
            _historyRead.Value = null;
        }
    }

    /// <summary>The provider's block for this invocation, or nothing; MAF stamps and merges it.</summary>
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var unfiltered = _unfilteredRequest.Value;
        var historyRead = _historyRead.Value;
        _unfilteredRequest.Value = null;
        _historyRead.Value = null;
        return Provider.ProvideMessagesAsync(context.Agent, context.Session, context.RequestMessages, unfiltered, ToolsOf(context.Agent), historyRead, cancellationToken);
    }

    /// <summary>Hands the invocation's end to the provider, which settles the delivery it staged.</summary>
    protected override ValueTask InvokedCoreAsync(AIContextProvider.InvokedContext context, CancellationToken cancellationToken = default) =>
        Provider.InvokedAsync(context, cancellationToken);

    /// <summary>
    /// <paramref name="request"/> with the chat history that the chat client agent <paramref name="agent"/> runs replays
    /// before it, as that agent loads it for the context providers on its options; or <paramref name="request"/>
    /// unchanged when the agent replays none, would refuse the run, or the history cannot be read in time (then with
    /// why, for the span).
    /// </summary>
    private async ValueTask<(IReadOnlyList<ChatMessage> Messages, string? Problem)> WithReplayedHistoryAsync(
        AIAgent? agent,
        AgentSession? session,
        IReadOnlyList<ChatMessage> request,
        CancellationToken cancellationToken)
    {
        ChatClientAgent chatClientAgent;
        ChatHistoryProvider history;
        try
        {
            if (agent is null
                || agent.GetService<ChatClientAgent>() is not { } found
                || !TryRunOptionsOf(agent, out var runOptions)
                || HistoryProviderOf(found, session, runOptions) is not { } resolved)
            {
                return (request, null);
            }

            chatClientAgent = found;
            history = resolved;
        }
#pragma warning disable CA1031 // Rules that cannot be read join nothing; the agent applies its own.
        catch (Exception)
#pragma warning restore CA1031
        {
            return (request, null);
        }

        if (history is InMemoryChatHistoryProvider inMemory)
        {
            // Read without side effects: the stored messages, stamped as the agent stamps them. With no session the
            // agent starts an empty one, so there is nothing to join.
            try
            {
                if (session is null)
                {
                    return (request, null);
                }

                var sourceId = inMemory.GetType().FullName;
                ChatMessage[] stored = [.. inMemory.GetMessages(session)];
                return ([.. stored.Select(message => message.WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory, sourceId)), .. request], null);
            }
#pragma warning disable CA1031 // A history that cannot be read joins nothing; the agent's own read reports the failure.
            catch (Exception)
#pragma warning restore CA1031
            {
                return (request, HistoryReadFailed);
            }
        }

        return await AskAsync(chatClientAgent, history, session, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks a history provider for the history, with the call and the arguments the agent uses, within
    /// <see cref="HistoryReadTimeout"/>. Only the caller's own cancellation escapes.
    /// </summary>
    private async ValueTask<(IReadOnlyList<ChatMessage> Messages, string? Problem)> AskAsync(
        ChatClientAgent agent,
        ChatHistoryProvider history,
        AgentSession? session,
        IReadOnlyList<ChatMessage> request,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(HistoryReadTimeout, Provider.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<IEnumerable<ChatMessage>>? read = null;
        try
        {
            read = ReadAsync(agent, history, session, request, linked.Token);
            var replayed = await read.WaitAsync(linked.Token).ConfigureAwait(false);
            return (replayed?.ToArray() ?? request, null);
        }
#pragma warning disable CA1031 // A history that cannot be read joins nothing; the agent's own read reports the failure.
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            Observe(read);
            return (request, timeout.IsCancellationRequested ? HistoryReadTimedOut : HistoryReadFailed);
        }
        catch (Exception)
        {
            Observe(read);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        static async Task<IEnumerable<ChatMessage>> ReadAsync(
            ChatClientAgent agent,
            ChatHistoryProvider history,
            AgentSession? session,
            IReadOnlyList<ChatMessage> request,
            CancellationToken cancellationToken)
        {
            // With no session the agent starts one; the read is given one the same way, and it is thrown away.
            session ??= await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            // The call the agent makes, with the same arguments, before it runs the context providers on its options.
#pragma warning disable MAAI001 // The only public way to ask a ChatHistoryProvider for its history (docs/compatibility-evidence.md).
            return await history.InvokingAsync(new ChatHistoryProvider.InvokingContext(agent, session, request), cancellationToken).ConfigureAwait(false);
#pragma warning restore MAAI001
        }
    }

    /// <summary>Observes an abandoned read's fault, so it never surfaces as an unobserved task exception.</summary>
    private static void Observe(Task? read) =>
        read?.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>
    /// The history provider a <see cref="ChatClientAgent"/> reads its replayed history from for this run, or
    /// <see langword="null"/> when it reads none or would refuse the run. Mirrors, in order, the checks
    /// <c>ChatClientAgent.PrepareSessionAndMessagesAsync</c> and <c>ResolveChatHistoryProvider</c> make before the agent
    /// loads its history (MAF 1.22.0 to 1.24.0).
    /// </summary>
    private static ChatHistoryProvider? HistoryProviderOf(ChatClientAgent agent, AgentSession? session, AgentRunOptions? runOptions)
    {
        var agentOptions = agent.GetService<ChatClientAgentOptions>();
        var agentChat = agentOptions?.ChatOptions;
        var runChat = (runOptions as ChatClientAgentRunOptions)?.ChatOptions;

        // The chat options the run starts from: the run's own when it has them, else the agent's.
        var configured = runChat ?? agentChat;
#pragma warning disable MEAI001 // Background responses and continuation tokens: read only to mirror the agent's own checks.
        var background = runOptions?.AllowBackgroundResponses ?? configured?.AllowBackgroundResponses;
        var continuation = runOptions?.ContinuationToken ?? configured?.ContinuationToken;
#pragma warning restore MEAI001
        if ((background == true && session is null) || continuation is not null)
        {
            // Refused without a session; or continued, with no history and no providers.
            return null;
        }

        ChatClientAgentSession? chatSession = null;
        if (session is not null && (chatSession = session as ChatClientAgentSession) is null)
        {
            // A session of another type the agent refuses.
            return null;
        }

        var sessionConversation = chatSession?.ConversationId;
        var chatConversation = runChat?.ConversationId ?? agentChat?.ConversationId;
        if (!string.IsNullOrWhiteSpace(sessionConversation) && !string.IsNullOrWhiteSpace(chatConversation) && sessionConversation != chatConversation)
        {
            // Two conversation IDs for one run: the agent refuses it.
            return null;
        }

        if (agentOptions?.RequirePerServiceCallChatHistoryPersistence == true)
        {
            return null;
        }

        var serviceKept = !string.IsNullOrWhiteSpace(chatConversation) || !string.IsNullOrWhiteSpace(sessionConversation);
        var history = serviceKept ? null : agent.ChatHistoryProvider;

        // The agent's merge: the run options' own additional properties win, then the run's chat options', then its own.
        AdditionalPropertiesDictionary? merged = null;
        foreach (var properties in new[] { runOptions?.AdditionalProperties, runChat?.AdditionalProperties, agentChat?.AdditionalProperties })
        {
            if (properties is null)
            {
                continue;
            }

            merged ??= [];
            foreach (var property in properties)
            {
                merged.TryAdd(property.Key, property.Value);
            }
        }

        if (merged is null || !merged.TryGetValue<ChatHistoryProvider>(out var passed))
        {
            return history;
        }

        if (agentOptions?.ThrowOnChatHistoryProviderConflict == true && serviceKept)
        {
            // A history provider passed for a service-kept conversation: the agent refuses the run.
            return null;
        }

        if (passed is not null && agent.AIContextProviders is { } providers
            && passed.StateKeys.Any(key => providers.Any(contextProvider => contextProvider.StateKeys.Contains(key))))
        {
            // A state key the agent's context providers already use: the agent refuses the run.
            return null;
        }

        return passed;
    }

    /// <summary>
    /// The tools the invocation offers, as a list of its own. Over a <see cref="ChatClientAgent"/>: the run's own
    /// <see cref="ChatClientAgentRunOptions"/> tools followed by the agent's default tools, as the agent merges them for
    /// the context providers on its options. For any other agent, the tools of the <see cref="ChatOptions"/> it reports
    /// through <see cref="AIAgent.GetService"/>; else none.
    /// </summary>
    private static List<AITool>? ToolsOf(AIAgent? agent)
    {
        try
        {
            if (agent is null)
            {
                return null;
            }

            if (agent.GetService<ChatClientAgent>() is null)
            {
                return agent.GetService<ChatOptions>()?.Tools is { } reported ? [.. reported] : null;
            }

            var agentTools = agent.GetService<ChatClientAgentOptions>()?.ChatOptions?.Tools;
            var runTools = TryRunOptionsOf(agent, out var runOptions) ? (runOptions as ChatClientAgentRunOptions)?.ChatOptions?.Tools : null;
            if (agentTools is not { Count: > 0 })
            {
                return runTools is null ? null : [.. runTools];
            }

            return runTools is { Count: > 0 } ? [.. runTools, .. agentTools] : [.. agentTools];
        }
        catch (Exception)
        {
            // An agent whose tools cannot be read offers none; the capability gate then fails closed.
            return null;
        }
    }

    /// <summary>Whether this run continues a background response, read from the run options and the chat options.</summary>
    private static bool IsContinuation(AIAgent? agent)
    {
        try
        {
            if (agent?.GetService<ChatClientAgent>() is null || !TryRunOptionsOf(agent, out var runOptions))
            {
                return false;
            }

            var agentChat = agent.GetService<ChatClientAgentOptions>()?.ChatOptions;
            var configured = (runOptions as ChatClientAgentRunOptions)?.ChatOptions ?? agentChat;
#pragma warning disable MEAI001 // Continuation tokens: read only to mirror the agent, which runs no providers for one.
            return (runOptions?.ContinuationToken ?? configured?.ContinuationToken) is not null;
#pragma warning restore MEAI001
        }
#pragma warning disable CA1031 // Options that cannot be read: injection goes on as before.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// The run options <paramref name="agent"/> was invoked with, from MAF's <see cref="AIAgent.CurrentRunContext"/>, which
    /// the agent-level providers run under. <see langword="false"/> when the current run context is not that agent's:
    /// then nothing about the run is known, and no history is read.
    /// </summary>
    private static bool TryRunOptionsOf(AIAgent agent, out AgentRunOptions? runOptions)
    {
        if (AIAgent.CurrentRunContext is { } run && ReferenceEquals(run.Agent, agent))
        {
            runOptions = run.RunOptions;
            return true;
        }

        runOptions = null;
        return false;
    }
}
