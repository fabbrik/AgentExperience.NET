using Microsoft.Extensions.AI;

namespace AgentExperience.Sample.EndToEnd.Fixtures;

/// <summary>One tool call a <see cref="ScriptedChatClient"/> emits on its first turn.</summary>
/// <param name="ToolName">The tool the scripted model asks for.</param>
/// <param name="Arguments">The arguments it asks for it with.</param>
internal sealed record ScriptedToolCall(string ToolName, IDictionary<string, object?> Arguments);

/// <summary>
/// A demonstration fixture, not a model client for real use: a deterministic
/// <see cref="IChatClient"/> that asks for one scripted tool call on its first turn and answers with
/// a fixed sentence once the tool result comes back.
/// </summary>
/// <remarks>
/// <para>
/// It stands in for a model so the sample needs no credentials and no network, which is the whole
/// point of being runnable on a fresh clone. Everything it is plugged into -- the
/// <see cref="Microsoft.Agents.AI.ChatClientAgent"/>, MAF's function-invocation pipeline, capture,
/// verification, reflection, retrieval, and injection -- is the real implementation.
/// </para>
/// <para>
/// <b>The final answer is a fixed string, never derived from anything injected.</b> This
/// client reads <see cref="FirstTurnMessages"/> for the sample's own reporting only; its answer is
/// the same string whether a Historical Reference was injected, was empty, or was never retrieved
/// at all. A reader must not take run B's answer as the lesson being applied -- the sample
/// disclaims having measured any such thing, and a scripted client could not demonstrate it.
/// </para>
/// </remarks>
internal sealed class ScriptedChatClient(ScriptedToolCall? call, string finalText) : IChatClient
{
    private readonly List<ChatMessage> _firstTurn = [];

    /// <summary>
    /// Every message this client was handed on its first turn, including anything an
    /// <c>AIContextProvider</c> contributed. The sample reads the
    /// injected Historical Reference back out of it, so what the transcript says about the block is
    /// what the model was actually given rather than what the injection result was asked for.
    /// </summary>
    public IReadOnlyList<ChatMessage> FirstTurnMessages => _firstTurn;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var list = messages.ToList();
        if (_firstTurn.Count == 0)
        {
            _firstTurn.AddRange(list);
        }

        var toolResultsSeen = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();

        if (call is not null && !toolResultsSeen)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("sample-call-1", call.ToolName, call.Arguments)])));
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, finalText)));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The AgentExperience.NET end-to-end sample never streams; capture covers streaming and is proven by the adapter's own tests.");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
