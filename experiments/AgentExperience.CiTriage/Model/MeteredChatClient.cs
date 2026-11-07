using Microsoft.Extensions.AI;

namespace AgentExperience.CiTriage.Model;

/// <summary>
/// The hard caps on one pass. A pass stops cleanly before the first model call that would start at or past any of them.
/// </summary>
/// <param name="MaxModelCalls">The most model calls.</param>
/// <param name="MaxTotalTokens">The most input plus output tokens, as the provider reports them.</param>
/// <param name="MaxUsd">The most estimated spend, from the tokens and the prices below.</param>
/// <param name="InputUsdPerMillion">The assumed input price, USD per million tokens.</param>
/// <param name="OutputUsdPerMillion">The assumed output price, USD per million tokens.</param>
public sealed record Budget(int MaxModelCalls, long MaxTotalTokens, decimal MaxUsd, decimal InputUsdPerMillion, decimal OutputUsdPerMillion)
{
    /// <summary>About USD 0.50 on <c>openai/gpt-oss-120b</c> at the assumed prices.</summary>
    public static Budget Default { get; } = new(MaxModelCalls: 400, MaxTotalTokens: 4_000_000, MaxUsd: 0.50m, InputUsdPerMillion: 0.10m, OutputUsdPerMillion: 0.50m);

    public decimal Cost(long inputTokens, long outputTokens) =>
        ((inputTokens * InputUsdPerMillion) + (outputTokens * OutputUsdPerMillion)) / 1_000_000m;
}

/// <summary>A cap was reached. The pass stops, writes what ran, and exits non-zero.</summary>
public sealed class BudgetExhaustedException(string message) : Exception(message);

/// <summary>
/// Counts every model call and the tokens the provider reports, and enforces the <see cref="Budget"/>. It sits directly
/// on the provider client, inside MAF's function-invocation loop, so it sees each request, including the ones after a
/// tool result. It never logs a message, a prompt or a header. (After LiveReuse's metered client.)
/// </summary>
internal sealed class MeteredChatClient(IChatClient inner, Budget budget) : DelegatingChatClient(inner)
{
    public int ModelCalls { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public decimal Cost => budget.Cost(InputTokens, OutputTokens);

    /// <summary>Whether any call since <see cref="ResetObservation"/> carried a Historical Reference block.</summary>
    public string? BlockSeen { get; private set; }

    public void ResetObservation() => BlockSeen = null;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (ModelCalls >= budget.MaxModelCalls)
        {
            throw new BudgetExhaustedException($"The model-call cap of {budget.MaxModelCalls} was reached.");
        }

        if (InputTokens + OutputTokens >= budget.MaxTotalTokens)
        {
            throw new BudgetExhaustedException($"The token cap of {budget.MaxTotalTokens} was reached.");
        }

        if (Cost >= budget.MaxUsd)
        {
            throw new BudgetExhaustedException($"The spend cap of USD {budget.MaxUsd} was reached.");
        }

        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        BlockSeen ??= list.Select(message => message.Text).FirstOrDefault(text => text.Contains("=== BEGIN HISTORICAL REFERENCE", StringComparison.Ordinal));

        // Counted before the call returns, so a call that throws still counts: the provider may have billed it.
        ModelCalls++;
        var response = await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        if (response.Usage is { } usage)
        {
            InputTokens += usage.InputTokenCount ?? 0;
            OutputTokens += usage.OutputTokenCount ?? 0;
        }

        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The triage agent never streams, so every call is metered by GetResponseAsync.");
}
