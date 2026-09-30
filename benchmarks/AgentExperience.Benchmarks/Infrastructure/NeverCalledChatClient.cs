using Microsoft.Extensions.AI;

namespace AgentExperience.Benchmarks.Infrastructure;

/// <summary>
/// The chat client behind the agent the injection benchmark invokes its context provider for. A context provider needs
/// an agent to be invoked for, never a model: this client throws if anything calls it, so a benchmark that reached a
/// model would fail rather than measure it.
/// </summary>
internal sealed class NeverCalledChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The benchmarks never call a model.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The benchmarks never call a model.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
