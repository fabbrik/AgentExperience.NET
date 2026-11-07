using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Cli.Engine;

/// <summary>
/// Asks the library a question the way a Microsoft Agent Framework agent would, so the answer is the library's own: a
/// real <see cref="ChatClientAgent"/> wired with <c>UseAgentExperience</c>, whose "model" is this scripted client. It calls
/// no model. For the block it records what the context provider put in front of the model; for a failure hint it makes
/// the one failing call and records what recall added to the next model call's instructions.
/// </summary>
internal sealed class ProbeChatClient(string? failingTool, IReadOnlyCollection<Guid> delivered) : IChatClient
{
    private int _calls;

    /// <summary>The Historical Reference block the first model call was handed, if any.</summary>
    public string? Block { get; private set; }

    /// <summary>The hint recall appended to the instructions after the failing call, if any.</summary>
    public string? Hint { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        if (call == 1)
        {
            Block = Extract(string.Join("\n", messages.Select(message => message.Text)), HistoricalReferenceWriter.BlockBegin);
            if (failingTool is not null)
            {
                // What this run was already given (its block, earlier hints) is noted, so recall does not repeat it.
                if (delivered.Count > 0 && CaptureScope.Current?.Recall is { } recall)
                {
                    recall.Note(delivered);
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("aexp-probe-call", failingTool, new Dictionary<string, object?>())])));
            }
        }
        else if (call == 2 && failingTool is not null)
        {
            Hint = Extract(options?.Instructions, HistoricalReferenceWriter.RecallBegin);
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The probe is never streamed.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    /// <summary>The block that starts with <paramref name="begin"/>, through its end marker.</summary>
    internal static string? Extract(string? text, string begin)
    {
        if (text is null)
        {
            return null;
        }

        var start = text.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = text.IndexOf(HistoricalReferenceWriter.BlockEnd, start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..(end + HistoricalReferenceWriter.BlockEnd.Length)];
    }
}

/// <summary>Runs one probe agent invocation and waits for the records it started to be written.</summary>
internal static class Probe
{
    /// <summary>
    /// One invocation of a probe agent over <paramref name="services"/> with <paramref name="task"/> as the user's words.
    /// With <paramref name="failingTool"/>, the scripted model calls that tool once; it returns <paramref name="failureText"/>,
    /// which the probe's classifier marks failed.
    /// </summary>
    public static async Task<ProbeChatClient> RunAsync(
        IServiceProvider services,
        string task,
        string? failingTool,
        string? failureText,
        IReadOnlyCollection<Guid> delivered,
        CancellationToken cancellationToken)
    {
        var client = new ProbeChatClient(failingTool, delivered);
        var tools = failingTool is null
            ? new List<AITool>()
            : [AIFunctionFactory.Create(() => failureText ?? string.Empty, failingTool, "The failed call being asked about.")];

        AIAgent agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "aexp",
            ChatOptions = new ChatOptions { Tools = tools },
        }).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync(task, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The holdout assignment and injection records are written in the background; a short-lived process waits for
        // them (each is bounded by the library's own timeout) instead of exiting under them.
        await services.GetRequiredService<ExperienceContextProvider>().InjectionRecordsSettledAsync().ConfigureAwait(false);
        return client;
    }
}
