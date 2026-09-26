using System.Globalization;
using System.Text.RegularExpressions;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>How the scripted stand-in for a model behaves. Each one exists to prove one property of the harness.</summary>
public enum ScriptedBehavior
{
    /// <summary>Tries the strategy on an injected block's Approach: line first, then the listing order.</summary>
    FollowsBlock,

    /// <summary>Never reads the block: the harness must then show no benefit in either comparison.</summary>
    IgnoresBlock,

    /// <summary>Never calls a tool: every trial fails, in every condition.</summary>
    NeverActs,

    /// <summary>Follows the block, and asks for the approval-gated bypass once whenever a block is present.</summary>
    ForcesWhenBlockPresent,
}

/// <summary>
/// A deterministic, offline <see cref="IChatClient"/> that plays the operator agent. Used by the harness's tests and by
/// <c>--scripted</c>, never by a live run, and never presented as a model: every report it produces says so.
/// </summary>
/// <remarks>
/// It reads only what a real model would be sent -- the messages -- and never the task set: the service and the
/// migration come from the task text's backticks, what it has already tried from the harness's work log in the
/// conversation, and a hint from an injected block's Approach: line if there is one. Without a block its candidate
/// order is the tool description's listing order, which is exactly the prior a real model is not assumed to share.
/// </remarks>
public sealed partial class ScriptedOperatorModel(ScriptedBehavior behavior = ScriptedBehavior.FollowsBlock) : IChatClient
{
    public const string ModelId = "scripted-operator";

    private int _sequence;

    /// <summary>The messages of every call, in order, for tests that compare what the conditions were shown.</summary>
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages.ToList();
        Calls.Add(list);

        var usage = new UsageDetails
        {
            InputTokenCount = list.Sum(message => (long)message.Text.Length) / 4 + 100,
            OutputTokenCount = 20,
        };

        ChatResponse Respond(ChatMessage message) => new(message) { ModelId = ModelId, Usage = usage };

        if (behavior == ScriptedBehavior.NeverActs)
        {
            return Task.FromResult(Respond(new ChatMessage(ChatRole.Assistant, "Which strategy should I use?")));
        }

        var task = list.FirstOrDefault(message => message.Role == ChatRole.User && Backticked().Matches(message.Text).Count >= 2)?.Text ?? string.Empty;
        var ticks = Backticked().Matches(task);
        var migration = ticks.Count > 0 ? ticks[0].Groups[1].Value : "unknown";
        var service = ticks.Count > 1 ? ticks[1].Groups[1].Value : "unknown";

        // What it already did, read from the work log the harness sends -- the same text a real model reads.
        var log = string.Join("\n", list.Select(message => message.Text).Where(text => text.Contains(WorkLog.Heading, StringComparison.Ordinal)));
        var block = list.Select(message => message.Text).FirstOrDefault(text => text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));

        if (behavior == ScriptedBehavior.ForcesWhenBlockPresent && block is not null && !log.Contains("You called " + MigrationEnvironment.ForceToolName + "(", StringComparison.Ordinal))
        {
            return Task.FromResult(Respond(Call(MigrationEnvironment.ForceToolName, new() { ["service"] = service, ["migration"] = migration })));
        }

        if (!log.Contains("You called " + MigrationEnvironment.DescribeToolName + "(", StringComparison.Ordinal))
        {
            return Task.FromResult(Respond(Call(MigrationEnvironment.DescribeToolName, new() { ["service"] = service })));
        }

        var tried = AppliedStrategy().Matches(log).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var candidates = new List<string>();
        if (behavior != ScriptedBehavior.IgnoresBlock && LiveReuseExperiment.BlockStrategy(block) is { } remembered)
        {
            candidates.Add(remembered);
        }

        candidates.AddRange(RolloutStrategies.All.Where(strategy => !candidates.Contains(strategy)));

        return candidates.FirstOrDefault(candidate => !tried.Contains(candidate)) is { } next
            ? Task.FromResult(Respond(Call(MigrationEnvironment.ApplyToolName, new() { ["service"] = service, ["migration"] = migration, ["strategy"] = next })))
            : Task.FromResult(Respond(new ChatMessage(ChatRole.Assistant, "Every strategy has been tried.")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private ChatMessage Call(string tool, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent("scripted-" + (++_sequence).ToString(CultureInfo.InvariantCulture), tool, arguments)]);

    [GeneratedRegex("`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();

    [GeneratedRegex("You called apply_migration\\([^)]*strategy=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AppliedStrategy();
}
