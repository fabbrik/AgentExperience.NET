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
/// its options. <b>The chat history:</b> the history the agent replays is read here the way the agent reads it (from
/// its <see cref="ChatClientAgent.ChatHistoryProvider"/>, or the one a run passes in its options' additional
/// properties; none when the service keeps the conversation under a conversation ID, or when the agent persists history
/// per service call) and goes to the derived task text, so a short follow-up is joined with an earlier turn's request.
/// The history is read once more per run than the agent alone reads it, so a history provider with a cost or a side
/// effect on read (a reducer that runs before retrieval, a remote store) incurs it twice. <b>The tools:</b> the run's
/// own <see cref="ChatClientAgentRunOptions"/> tools followed by the agent's default tools, merged as the agent merges
/// them, with the run options read from <see cref="AIAgent.CurrentRunContext"/>; with
/// <see cref="ReceivingAgentCapabilities.UseRunTools"/> a run-only tool counts. What it still cannot see: messages or
/// tools that a context provider on the agent's options adds, and run options or a session that middleware between
/// this and the agent replaces. An agent that is not a <see cref="ChatClientAgent"/> offers the caller's messages
/// only, and the tools of the <see cref="ChatOptions"/> it reports through <see cref="AIAgent.GetService"/> (none when
/// it reports none); tools it adds itself, server-side tools, and function-invocation middleware's additional tools are
/// never seen. The session is the one the caller passed; with none, nothing is tracked and no history is read (the
/// agent starts an empty one).
/// </para>
/// <para>Like the provider, it never throws into the invocation except for the caller's own cancellation.</para>
/// </remarks>
internal sealed class ExperienceMessageContextProvider(ExperienceContextProvider provider) : MessageAIContextProvider
{
    private readonly AsyncLocal<IReadOnlyList<ChatMessage>?> _unfilteredRequest = new();

    /// <summary>The provider this runs.</summary>
    internal ExperienceContextProvider Provider { get; } = provider;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => Provider.StateKeys;

    /// <summary>
    /// Keeps the unfiltered request messages for the derived task text, with the history a <see cref="ChatClientAgent"/>
    /// replays before them, as the provider does on that agent's options; then runs MAF's own filtering, merging and
    /// source stamping unchanged. The messages handed on to the agent are the caller's: the history is never added.
    /// </summary>
    protected override async ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
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

        if (unfiltered is not null)
        {
            unfiltered = await WithReplayedHistoryAsync(context!.Agent, context.Session, unfiltered, cancellationToken).ConfigureAwait(false);
        }

        _unfilteredRequest.Value = unfiltered;
        try
        {
            return await base.InvokingCoreAsync(context!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _unfilteredRequest.Value = null;
        }
    }

    /// <summary>The provider's block for this invocation, or nothing; MAF stamps and merges it.</summary>
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var unfiltered = _unfilteredRequest.Value;
        _unfilteredRequest.Value = null;
        return Provider.ProvideMessagesAsync(context.Agent, context.Session, context.RequestMessages, unfiltered, ToolsOf(context.Agent), cancellationToken);
    }

    /// <summary>Hands the invocation's end to the provider, which settles the delivery it staged.</summary>
    protected override ValueTask InvokedCoreAsync(AIContextProvider.InvokedContext context, CancellationToken cancellationToken = default) =>
        Provider.InvokedAsync(context, cancellationToken);

    /// <summary>
    /// <paramref name="request"/> with the chat history that the chat client agent <paramref name="agent"/> runs replays
    /// before it, loaded and stamped exactly as that agent loads it for the context providers on its options; or
    /// <paramref name="request"/> unchanged when the agent replays none, there is no session, or the history cannot be
    /// read.
    /// </summary>
    private static async ValueTask<IReadOnlyList<ChatMessage>> WithReplayedHistoryAsync(
        AIAgent? agent,
        AgentSession? session,
        IReadOnlyList<ChatMessage> request,
        CancellationToken cancellationToken)
    {
        try
        {
            // With no session the agent starts an empty one: there is no history to replay. A session of another type
            // the agent refuses.
            if (agent is null || session is not ChatClientAgentSession chatSession || agent.GetService<ChatClientAgent>() is not { } chatClientAgent)
            {
                return request;
            }

            var history = HistoryProviderOf(chatClientAgent, chatSession, RunOptionsOf(agent));
            if (history is null)
            {
                return request;
            }

            // The call the agent makes, with the same arguments, before it runs the context providers on its options.
#pragma warning disable MAAI001 // The only public way to ask a ChatHistoryProvider for its history (docs/compatibility-evidence.md).
            var replayed = await history.InvokingAsync(new ChatHistoryProvider.InvokingContext(chatClientAgent, chatSession, request), cancellationToken).ConfigureAwait(false);
#pragma warning restore MAAI001
            return replayed?.ToArray() ?? request;
        }
#pragma warning disable CA1031 // A history that cannot be read joins nothing; the agent's own read reports the failure.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return request;
        }
    }

    /// <summary>
    /// The history provider a <see cref="ChatClientAgent"/> loads its replayed history from for this run, by the rules
    /// the agent applies: none when it persists history per service call or when the service keeps the conversation (a
    /// conversation ID on the session or in the chat options), else the agent's own; and in either case the provider a
    /// run passes in the additional properties instead.
    /// </summary>
    private static ChatHistoryProvider? HistoryProviderOf(ChatClientAgent agent, ChatClientAgentSession session, AgentRunOptions? runOptions)
    {
        var agentOptions = agent.GetService<ChatClientAgentOptions>();
        if (agentOptions?.RequirePerServiceCallChatHistoryPersistence == true)
        {
            return null;
        }

        var agentChat = agentOptions?.ChatOptions;
        var runChat = (runOptions as ChatClientAgentRunOptions)?.ChatOptions;
        var conversationId = runChat?.ConversationId ?? agentChat?.ConversationId;
        var history = string.IsNullOrWhiteSpace(conversationId) && string.IsNullOrWhiteSpace(session.ConversationId)
            ? agent.ChatHistoryProvider
            : null;

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

        return merged is not null && merged.TryGetValue<ChatHistoryProvider>(out var passed) ? passed : history;
    }

    /// <summary>
    /// The tools the invocation offers. Over a <see cref="ChatClientAgent"/>: the run's own
    /// <see cref="ChatClientAgentRunOptions"/> tools followed by the agent's default tools, as the agent merges them for
    /// the context providers on its options. For any other agent, the tools of the <see cref="ChatOptions"/> it reports
    /// through <see cref="AIAgent.GetService"/>; else none.
    /// </summary>
    private static IList<AITool>? ToolsOf(AIAgent? agent)
    {
        try
        {
            if (agent is null)
            {
                return null;
            }

            if (agent.GetService<ChatClientAgent>() is null)
            {
                return agent.GetService<ChatOptions>()?.Tools;
            }

            var agentTools = agent.GetService<ChatClientAgentOptions>()?.ChatOptions?.Tools;
            var runTools = (RunOptionsOf(agent) as ChatClientAgentRunOptions)?.ChatOptions?.Tools;
            if (agentTools is not { Count: > 0 })
            {
                return runTools;
            }

            return runTools is { Count: > 0 } ? [.. runTools, .. agentTools] : [.. agentTools];
        }
        catch (Exception)
        {
            // An agent whose tools cannot be read offers none; the capability gate then fails closed.
            return null;
        }
    }

    /// <summary>
    /// The run options <paramref name="agent"/> was invoked with, from MAF's <see cref="AIAgent.CurrentRunContext"/>,
    /// which the agent-level providers run under; <see langword="null"/> when the current run is not that agent's.
    /// </summary>
    private static AgentRunOptions? RunOptionsOf(AIAgent agent) =>
        AIAgent.CurrentRunContext is { } run && ReferenceEquals(run.Agent, agent) ? run.RunOptions : null;
}
