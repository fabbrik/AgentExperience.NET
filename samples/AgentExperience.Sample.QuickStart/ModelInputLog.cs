using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;

namespace AgentExperience.Sample.QuickStart;

/// <summary>
/// Sits between the agent and the model and keeps the Historical Reference block, if any, from what the model was
/// sent. It reads the model's real input, so it shows the same thing for the stand-in and for a real model.
/// </summary>
public sealed class ModelInputLog(IChatClient model) : DelegatingChatClient(model)
{
    /// <summary>The block in the model's input since the last <see cref="Reset"/>, or <see langword="null"/> when there was none.</summary>
    public string? Block { get; private set; }

    public void Reset() => Block = null;

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        Block ??= list.Select(message => message.Text).FirstOrDefault(text => text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));
        return base.GetResponseAsync(list, options, cancellationToken);
    }
}
