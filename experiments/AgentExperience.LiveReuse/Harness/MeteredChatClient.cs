using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>The hard caps on one run. A run stops cleanly before the first model call that would start at or past either.</summary>
/// <param name="MaxModelCalls">The most model calls the run may make, learning phase included.</param>
/// <param name="MaxTotalTokens">The most input plus output tokens the run may consume, as the provider reports them.</param>
public sealed record LiveBudget(int MaxModelCalls, long MaxTotalTokens);

/// <summary>The budget cap was reached. The experiment stops, records where, and evaluates no verdict.</summary>
public sealed class BudgetExhaustedException(string message) : Exception(message);

/// <summary>Model usage, summed.</summary>
public sealed record UsageTotals(int ModelCalls, long InputTokens, long OutputTokens, double ModelLatencyMilliseconds)
{
    public static UsageTotals Zero { get; } = new(0, 0, 0, 0);

    public long TotalTokens => InputTokens + OutputTokens;

    public UsageTotals Minus(UsageTotals earlier) => new(
        ModelCalls - earlier.ModelCalls,
        InputTokens - earlier.InputTokens,
        OutputTokens - earlier.OutputTokens,
        ModelLatencyMilliseconds - earlier.ModelLatencyMilliseconds);

    public UsageTotals Plus(UsageTotals other) => new(
        ModelCalls + other.ModelCalls,
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        ModelLatencyMilliseconds + other.ModelLatencyMilliseconds);
}

/// <summary>
/// Wraps the provider's <see cref="IChatClient"/>: counts every model call and the tokens the provider reports, times
/// each call, remembers which model identities the provider answered as, enforces the budget, and keeps the messages
/// of the most recent call so the harness can read what the model was actually shown.
/// </summary>
/// <remarks>
/// It sits directly on the provider client, inside MAF's function-invocation loop, so it sees each individual
/// request, including the ones the loop makes after a tool result. It never logs a message, a prompt or a header.
/// </remarks>
internal sealed class MeteredChatClient(IChatClient inner, LiveBudget budget, TimeProvider clock, TimeSpan minimumInterval) : DelegatingChatClient(inner)
{
    private readonly SortedSet<string> _modelIds = new(StringComparer.Ordinal);
    private UsageTotals _totals = UsageTotals.Zero;
    private long? _lastCallTimestamp;

    public UsageTotals Totals => _totals;

    /// <summary>The model identities the provider reported in its responses, sorted.</summary>
    public IReadOnlyCollection<string> ModelIds => _modelIds;

    /// <summary>How many responses reported no token usage at all. Reported, because a zero there is not a measured zero.</summary>
    public int CallsWithoutUsage { get; private set; }

    /// <summary>The Historical Reference block in the most recent trial's first model call, if it carried one.</summary>
    public string? FirstBlockSeen { get; private set; }

    /// <summary>Whether any model call since <see cref="ResetObservation"/> carried a Historical Reference block.</summary>
    public bool AnyBlockSeen { get; private set; }

    /// <summary>Forgets what the previous trial showed the model. Called at the start of each run.</summary>
    public void ResetObservation()
    {
        FirstBlockSeen = null;
        AnyBlockSeen = false;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (_totals.ModelCalls >= budget.MaxModelCalls)
        {
            throw new BudgetExhaustedException($"The model-call cap of {budget.MaxModelCalls} was reached.");
        }

        if (_totals.TotalTokens >= budget.MaxTotalTokens)
        {
            throw new BudgetExhaustedException($"The token cap of {budget.MaxTotalTokens} was reached ({_totals.TotalTokens} used).");
        }

        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        Observe(list);

        if (minimumInterval > TimeSpan.Zero && _lastCallTimestamp is { } last)
        {
            var wait = minimumInterval - clock.GetElapsedTime(last);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
            }
        }

        var started = clock.GetTimestamp();
        _lastCallTimestamp = started;

        // Counted before the call returns, so a call that throws still counts against the cap: the provider may
        // have billed it.
        _totals = _totals with { ModelCalls = _totals.ModelCalls + 1 };

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _totals = _totals with { ModelLatencyMilliseconds = _totals.ModelLatencyMilliseconds + clock.GetElapsedTime(started).TotalMilliseconds };
        }

        if (response.Usage is { } usage && (usage.InputTokenCount is not null || usage.OutputTokenCount is not null))
        {
            _totals = _totals with
            {
                InputTokens = _totals.InputTokens + (usage.InputTokenCount ?? 0),
                OutputTokens = _totals.OutputTokens + (usage.OutputTokenCount ?? 0),
            };
        }
        else
        {
            CallsWithoutUsage++;
        }

        if (!string.IsNullOrWhiteSpace(response.ModelId))
        {
            _modelIds.Add(response.ModelId);
        }

        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The live-reuse harness never streams, so every call is metered by GetResponseAsync.");

    private void Observe(IReadOnlyList<ChatMessage> messages)
    {
        var block = messages
            .Select(message => message.Text)
            .FirstOrDefault(text => text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));

        if (block is null)
        {
            return;
        }

        AnyBlockSeen = true;
        FirstBlockSeen ??= block;
    }
}
