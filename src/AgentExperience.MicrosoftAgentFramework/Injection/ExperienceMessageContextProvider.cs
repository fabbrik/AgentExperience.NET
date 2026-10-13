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
/// account and the same settling. What differs is what the agent level can see. The request is the caller's messages
/// only, not the chat history a <see cref="ChatClientAgent"/> replays, so a short follow-up is not joined with an
/// earlier turn's request. The tools are the <see cref="ChatClientAgent"/>'s default tools
/// (<see cref="ChatClientAgentOptions.ChatOptions"/>), not the ones a run adds through its run options, so with
/// <see cref="ReceivingAgentCapabilities.UseRunTools"/> a record whose approach calls a run-only tool is withheld, as
/// for any tool the provider does not see. An agent that is not a <see cref="ChatClientAgent"/> offers the tools of the
/// <see cref="ChatOptions"/> it reports through <see cref="AIAgent.GetService"/>, and none when it reports none; tools it
/// adds itself, server-side tools, and function-invocation middleware's additional tools are never seen. The session is
/// the one the caller passed; with none, nothing is tracked.
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
    /// Keeps the unfiltered request messages for the derived task text, as the provider does on a
    /// <see cref="ChatClientAgent"/>, then runs MAF's own filtering, merging and source stamping unchanged.
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
        return Provider.ProvideMessagesAsync(context.Agent, context.Session, context.RequestMessages, unfiltered, DefaultTools(context.Agent), cancellationToken);
    }

    /// <summary>Hands the invocation's end to the provider, which settles the delivery it staged.</summary>
    protected override ValueTask InvokedCoreAsync(AIContextProvider.InvokedContext context, CancellationToken cancellationToken = default) =>
        Provider.InvokedAsync(context, cancellationToken);

    /// <summary>
    /// The default tools of the chat client agent <paramref name="agent"/> runs; for any other agent, the tools of the
    /// <see cref="ChatOptions"/> it reports through <see cref="AIAgent.GetService"/>; else none.
    /// </summary>
    private static IList<AITool>? DefaultTools(AIAgent? agent)
    {
        try
        {
            if (agent is null)
            {
                return null;
            }

            return agent.GetService<ChatClientAgent>() is not null
                ? agent.GetService<ChatClientAgentOptions>()?.ChatOptions?.Tools
                : agent.GetService<ChatOptions>()?.Tools;
        }
        catch (Exception)
        {
            // An agent whose tools cannot be read offers none; the capability gate then fails closed.
            return null;
        }
    }
}
