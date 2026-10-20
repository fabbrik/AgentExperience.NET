using System.Globalization;
using System.Text;
using AgentExperience.Core.Capture;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// The conversation the model sees, as text, for the real-task experiment: every tool call it made with the tool's
/// output verbatim (newlines kept, because the outputs are code), every reply it gave without a tool call, and the
/// harness's fixed follow-up after a failed attempt. Like <see cref="WorkLog"/>, the harness never replays a function
/// call; each model call is a fresh request carrying this log.
/// </summary>
internal sealed class RealWorkLog
{
    public const string Heading = "Work log for this ticket so far (tool outputs verbatim):";
    public const string Continue = "Continue with the ticket.";
    public const string FollowUp = "The tests are not passing yet. Keep working on the ticket.";
    private const int MaxOutputLength = 2_000;
    private const int MaxArgumentLength = 120;
    private const int MaxReplyLength = 300;

    private readonly List<string> _entries = [];
    private bool _followUpPending;

    public int Count => _entries.Count;

    public IReadOnlyList<ChatMessage> Messages(string ticket)
    {
        if (_entries.Count == 0)
        {
            return [new ChatMessage(ChatRole.User, ticket)];
        }

        var text = new StringBuilder(Heading).Append('\n');
        for (var index = 0; index < _entries.Count; index++)
        {
            text.Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(_entries[index]).Append('\n');
        }

        text.Append(_followUpPending ? FollowUp : Continue);
        return [new ChatMessage(ChatRole.User, ticket), new ChatMessage(ChatRole.User, text.ToString())];
    }

    public void AddCall(RawToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var arguments = string.Join(", ", call.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var value = LiveReuseExperiment.TextOf(pair.Value) ?? string.Empty;
            return pair.Key == "content"
                ? pair.Key + "=(" + value.Length.ToString(CultureInfo.InvariantCulture) + " characters)"
                : pair.Key + "=\"" + Clip(value.ReplaceLineEndings(" "), MaxArgumentLength) + "\"";
        }));
        _entries.Add($"You called {call.ToolName}({arguments}) ->\n{Clip(call.Result ?? call.Error ?? "(no output)", MaxOutputLength)}");
        _followUpPending = false;
    }

    public void AddUnansweredCall(FunctionCallContent call, bool toolExists)
    {
        ArgumentNullException.ThrowIfNull(call);
        var name = Clip((call.Name ?? string.Empty).ReplaceLineEndings(" "), 100);
        _entries.Add(toolExists
            ? $"You called {name}(...) -> not run, because the same response also called a tool that does not exist; nothing was changed"
            : $"You called {name}(...) -> no tool named \"{name}\" exists; nothing was run");
        _followUpPending = false;
    }

    public void AddReply(string? text)
    {
        _entries.Add($"You replied without calling a tool: \"{Clip((string.IsNullOrWhiteSpace(text) ? "(empty)" : text).ReplaceLineEndings(" "), MaxReplyLength)}\"");
        _followUpPending = false;
    }

    public void AddFollowUp() => _followUpPending = true;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}

/// <summary>
/// Retries a model call that failed with HTTP 429 or 5xx, up to the pre-registered number of tries, before the metered
/// client counts it as made. Any other failure, and cancellation, is rethrown at once.
/// </summary>
internal sealed class RetryingChatClient(IChatClient inner, int maxTries, IReadOnlyList<int> delaysMilliseconds, bool noDelay, Func<Exception, bool>? isTransient = null) : DelegatingChatClient(inner)
{
    private readonly Func<Exception, bool> _isTransient = isTransient ?? IsTransient;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        for (var tries = 1; ; tries++)
        {
            try
            {
                return await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (tries < maxTries && !cancellationToken.IsCancellationRequested && _isTransient(ex))
            {
                if (!noDelay && delaysMilliseconds.Count > 0)
                {
                    await Task.Delay(delaysMilliseconds[Math.Min(tries - 1, delaysMilliseconds.Count - 1)], cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Whether the failure is an HTTP 429 or 5xx, read from the same classification the report records.</summary>
    internal static bool IsTransient(Exception exception) => IsTransientClassification(LiveReuseExperiment.Classify(exception));

    internal static bool IsTransientClassification(string classification) =>
        classification.Contains("(HTTP 429)", StringComparison.Ordinal) || classification.Contains("(HTTP 5", StringComparison.Ordinal);
}
