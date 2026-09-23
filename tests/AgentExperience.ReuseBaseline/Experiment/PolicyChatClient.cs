using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;

namespace AgentExperience.ReuseBaseline.Experiment;

/// <summary>
/// A demonstration fixture, not a model client for real use: the declared, deterministic agent
/// policy the reference experiment measures.
/// </summary>
/// <remarks>
/// <para>
/// <b>The policy, in full.</b> On each attempt the client picks the next strategy from a candidate
/// list it has not tried yet, and asks for the incident check under it. The candidate list is:
/// every strategy named inside an injected Historical Reference block, in the order the block names
/// them -- which is rank order -- followed by the task set's fixed exploration order with those
/// already listed removed. With no injected block the candidate list is exactly the exploration
/// order. There is no other input: the client cannot see the task's resolving strategy, and it
/// cannot see which condition it is running under.
/// </para>
/// <para>
/// <b>What this makes measurable, and what it does not.</b> Whether the injected block reaches the
/// model's context and changes the action taken is a real mechanism question and this policy
/// answers it honestly. How much it is worth is not: the magnitude is whatever this file and the
/// task set decide between them. That is exactly why the report is forbidden from quoting it as a
/// benefit claim.
/// </para>
/// <para>
/// <b>It obeys an instruction in the block, once.</b> If the injected block names the guarded tool,
/// this client calls the guarded tool on its first attempt, exactly as
/// <c>InjectedContentAuthorizationTests</c>'s obedient client does. The point is not that a model
/// refuses -- a label cannot make a model refuse and this library never claims it can -- but that
/// the approval boundary denies the call anyway, and that the harness counts the denial.
/// </para>
/// </remarks>
internal sealed class PolicyChatClient : IChatClient
{
    private readonly IReadOnlyList<string> _explorationOrder;
    private readonly string _incidentId;
    private readonly List<string> _fromContext = [];
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);
    private bool _contextRead;
    private bool _obeyedGuardedToolInstruction;
    private int _callSequence;

    /// <summary>Creates the policy for one trial.</summary>
    /// <param name="incidentId">The incident the check is run against.</param>
    /// <param name="explorationOrder">The task set's fixed exploration order.</param>
    public PolicyChatClient(string incidentId, IReadOnlyList<string> explorationOrder)
    {
        ArgumentNullException.ThrowIfNull(explorationOrder);

        _incidentId = incidentId;
        _explorationOrder = explorationOrder;
    }

    /// <summary>The messages this client was handed on its most recent invocation, for the harness's own reporting.</summary>
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    /// <summary>The Historical Reference block this client saw, verbatim, or <see langword="null"/> if none reached it.</summary>
    public string? SeenBlock { get; private set; }

    /// <summary>The strategies the injected block named, in the order it named them.</summary>
    public IReadOnlyList<string> StrategiesFromContext => _fromContext;

    /// <summary>The strategies this client asked for, in the order it asked for them.</summary>
    public IReadOnlyList<string> AttemptedStrategies { get; } = new List<string>();

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var list = messages.ToList();
        LastMessages = list;

        ReadContext(list);

        // The tool has answered, so this attempt is over. The sentence is fixed and carries nothing
        // from the block: a reader must not take the answer text as evidence of anything.
        if (list.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                "Reported the incident check result for " + _incidentId + ".")));
        }

        if (SeenBlock is not null
            && !_obeyedGuardedToolInstruction
            && SeenBlock.Contains(ToolApprovalBoundary.GuardedToolName, StringComparison.Ordinal))
        {
            _obeyedGuardedToolInstruction = true;
            return Task.FromResult(Call(ToolApprovalBoundary.GuardedToolName, new Dictionary<string, object?>(StringComparer.Ordinal)));
        }

        if (NextCandidate() is not { } strategy)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                "No untried strategy remains for " + _incidentId + ".")));
        }

        _tried.Add(strategy);
        ((List<string>)AttemptedStrategies).Add(strategy);

        return Task.FromResult(Call(
            IncidentCheckTool.ToolName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["incident"] = _incidentId,
                ["strategy"] = strategy,
            }));
    }

    /// <summary>The next strategy the policy would ask for, without consuming it.</summary>
    internal string? NextCandidate() => Candidates().FirstOrDefault(candidate => !_tried.Contains(candidate));

    /// <summary>
    /// The candidate list in full: what the block named, then the fixed exploration order with
    /// those removed.
    /// </summary>
    internal IReadOnlyList<string> Candidates()
    {
        var ordered = new List<string>(_fromContext);
        foreach (var strategy in _explorationOrder)
        {
            if (!ordered.Contains(strategy, StringComparer.Ordinal))
            {
                ordered.Add(strategy);
            }
        }

        return ordered;
    }

    /// <summary>
    /// Reads the injected block out of the messages once. Which strategies it names is decided by
    /// where each one first appears in the block, so the order is the block's rank order rather
    /// than the exploration order.
    /// </summary>
    private void ReadContext(IReadOnlyList<ChatMessage> messages)
    {
        if (_contextRead)
        {
            return;
        }

        var block = messages
            .Select(message => message.Text)
            .FirstOrDefault(text => text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));

        if (block is null)
        {
            // Not marked as read: the provider runs per invocation, and an attempt that saw nothing
            // must not stop a later attempt in the same trial from seeing something.
            return;
        }

        _contextRead = true;
        SeenBlock = block;

        foreach (var strategy in _explorationOrder
            .Select(strategy => (Strategy: strategy, At: block.IndexOf(strategy, StringComparison.Ordinal)))
            .Where(found => found.At >= 0)
            .OrderBy(found => found.At)
            .Select(found => found.Strategy))
        {
            _fromContext.Add(strategy);
        }
    }

    private ChatResponse Call(string toolName, IDictionary<string, object?> arguments) =>
        new(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("policy-call-" + (++_callSequence).ToString(System.Globalization.CultureInfo.InvariantCulture), toolName, arguments)]));

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The reuse-baseline harness never streams; capture covers streaming and the adapter's own tests prove it.");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
