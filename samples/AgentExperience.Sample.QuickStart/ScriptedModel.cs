using System.Globalization;
using System.Text.RegularExpressions;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;

namespace AgentExperience.Sample.QuickStart;

/// <summary>
/// A scripted stand-in for a model, so the demo needs no key and no network. It is not a model and is never presented
/// as one: the demo's output says so.
/// </summary>
/// <remarks>
/// It reads only what a real model is sent, the messages. It tries the tool's strategies in their listed order, one
/// call per turn, until one exits 0, with one exception: when its input holds a Historical Reference whose
/// <c>Worked:</c> attempt names a strategy, it tries that one first. So whatever changes in the second run comes from
/// what the library retrieved and injected, not from this script.
/// </remarks>
public sealed partial class ScriptedModel : IChatClient
{
    private int _sequence;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var results = list.SelectMany(message => message.Contents.OfType<FunctionResultContent>());
        if (results.Any(result => result.Result?.ToString()?.StartsWith("exit=0", StringComparison.Ordinal) == true))
        {
            return Reply(new ChatMessage(ChatRole.Assistant, "The refund is released."));
        }

        var tried = list.SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Select(call => call.Arguments?.TryGetValue("strategy", out var value) == true ? value?.ToString() : null)
            .ToHashSet(StringComparer.Ordinal);
        var block = list.Select(message => message.Text).FirstOrDefault(text => text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));
        var order = new List<string>();
        if (Remembered(block) is { } remembered)
        {
            order.Add(remembered);
        }

        order.AddRange(RefundDesk.Strategies);

        if (order.FirstOrDefault(strategy => !tried.Contains(strategy)) is not { } next)
        {
            return Reply(new ChatMessage(ChatRole.Assistant, "Every strategy failed; escalating the ticket."));
        }

        var task = list.LastOrDefault(message => message.Role == ChatRole.User && TicketNumber().IsMatch(message.Text))?.Text ?? string.Empty;
        var callId = "call-" + (++_sequence).ToString(CultureInfo.InvariantCulture);
        return Reply(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, RefundDesk.ToolName, new Dictionary<string, object?>
        {
            ["ticketId"] = TicketNumber().Match(task).Groups[1].Value,
            ["strategy"] = next,
        })]));
    }

    /// <summary>The strategy on the last call of the attempt the block's <c>Worked:</c> line names: the call that worked.</summary>
    internal static string? Remembered(string? block)
    {
        if (block is null || WorkedLine().Match(block) is not { Success: true } worked)
        {
            return null;
        }

        var tried = Regex.Match(block, "^  - attempt " + worked.Groups[1].Value + ": (.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var strategies = StrategyArgument().Matches(tried.Groups[1].Value);
        return strategies.Count > 0 ? strategies[^1].Groups[1].Value : null;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The scripted stand-in does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static Task<ChatResponse> Reply(ChatMessage message) => Task.FromResult(new ChatResponse(message) { ModelId = "scripted-stand-in" });

    [GeneratedRegex(@"#(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TicketNumber();

    [GeneratedRegex(@"^Worked: attempt (\d+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex WorkedLine();

    [GeneratedRegex("strategy=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex StrategyArgument();
}
