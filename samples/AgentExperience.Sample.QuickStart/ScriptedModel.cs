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
/// <c>Worked:</c> attempt has a call marked <c>[returned]</c> with a strategy, it tries the first such one first. So whatever changes in the second run comes from
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

    /// <summary>
    /// The strategy of the first call marked <c>[returned]</c> that carries one, on the <c>Tried:</c> line of the attempt
    /// the block's <c>Worked:</c> line names: the call that did not fail, wherever it sits on the line and wherever
    /// <c>strategy</c> sits among its arguments. <see langword="null"/> when there is no such line or no returned call on
    /// it carries a strategy.
    /// </summary>
    internal static string? Remembered(string? block)
    {
        if (block is null || WorkedLine().Match(block) is not { Success: true } worked)
        {
            return null;
        }

        var tried = Regex.Match(block, "^  - attempt " + worked.Groups[1].Value + ": (.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        foreach (Match call in ReturnedCall().Matches(tried.Groups[1].Value))
        {
            // Walk the arguments one by one, so a value can never be read as a key.
            foreach (Match argument in Argument().Matches(call.Groups[1].Value))
            {
                if (argument.Groups["key"].Value == "strategy" && argument.Groups["quoted"].Success)
                {
                    return argument.Groups["quoted"].Value;
                }
            }
        }

        return null;
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

    /// <summary>
    /// One call's argument list followed by its own <c>[returned]</c> marker. A quoted value is matched whole (the
    /// writer never lets one hold a double quote), so a parenthesis or a marker inside a value cannot end the list.
    /// </summary>
    [GeneratedRegex("\\(((?:\"[^\"]*\"|\\([^()\"]*\\)|[^\"()])*)\\) \\[returned\\]", RegexOptions.CultureInvariant)]
    private static partial Regex ReturnedCall();

    /// <summary>One <c>key=value</c> of a call's argument list, each starting where the previous one ended.</summary>
    [GeneratedRegex("\\G(?:, )?(?<key>[^=,\"]+)=(?:\"(?<quoted>[^\"]*)\"(?:\\[\\.\\.\\.\\])?|\\([^)]*\\)|[^,\"]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Argument();
}
