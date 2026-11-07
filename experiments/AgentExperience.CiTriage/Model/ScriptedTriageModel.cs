using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentExperience.CiTriage.Model;

/// <summary>
/// The <c>--scripted</c> stand-in: no network, no model, the same answer every time. It calls <c>changed_files</c>, reads
/// the first changed file with <c>read_file</c>, then answers <c>flaky</c> when the log says something timed out and
/// otherwise <c>code</c> with the failing commit's first five changed files. It shows the loop working, never that
/// lessons help a real model.
/// </summary>
internal sealed class ScriptedTriageModel : IChatClient
{
    public const string ModelId = "scripted-stand-in";

    private int _callIds;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var calls = list.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToDictionary(call => call.CallId, call => call.Name, StringComparer.Ordinal);
        var results = list.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(result => (Name: calls.GetValueOrDefault(result.CallId), Text: result.Result?.ToString() ?? string.Empty))
            .ToList();

        var changed = results.FirstOrDefault(result => result.Name == TriageAgent.ChangedFilesTool);
        if (changed.Name is null)
        {
            return Task.FromResult(Call(TriageAgent.ChangedFilesTool, new Dictionary<string, object?>()));
        }

        var files = changed.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('(')).ToList();
        if (files.Count > 0 && !results.Any(result => result.Name == TriageAgent.ReadFileTool))
        {
            return Task.FromResult(Call(TriageAgent.ReadFileTool, new Dictionary<string, object?> { ["path"] = files[0] }));
        }

        var prompt = list.LastOrDefault(message => message.Role == ChatRole.User && message.Text.Contains(TriageAgent.LogHeading, StringComparison.Ordinal))?.Text ?? string.Empty;
        var flaky = files.Count == 0 || prompt.Contains("timed out", StringComparison.OrdinalIgnoreCase);
        var reply = JsonSerializer.Serialize(new { kind = flaky ? "flaky" : "code", files = flaky ? [] : files.Take(5).ToArray() });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { ModelId = ModelId });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", defaultModelId: ModelId) : null;

    public void Dispose()
    {
    }

    private ChatResponse Call(string name, Dictionary<string, object?> arguments)
    {
        var id = "call-" + (++_callIds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, arguments)])) { ModelId = ModelId };
    }
}
