using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentExperience.CiTriage.Model;

/// <summary>
/// The OpenRouter client: the OpenAI SDK pointed at OpenRouter's OpenAI-compatible endpoint, with the provider routing
/// policy and the empty-completion guard copied from <c>experiments/AgentExperience.LiveReuse</c> (internal there, so
/// copied rather than referenced). The key is used here and nowhere else, and never printed.
/// </summary>
internal static class OpenRouter
{
    public const string KeyVariable = "OPENROUTER_API_KEY";
    public const string ModelVariable = "OPENROUTER_MODEL";
    public const string DefaultModel = "openai/gpt-oss-120b";
    public static readonly Uri Endpoint = new("https://openrouter.ai/api/v1/");

    public static IChatClient Create(string apiKey, string model)
    {
        var options = new OpenAIClientOptions { Endpoint = Endpoint };
        options.AddPolicy(new OpenRouterRoutingPolicy(), PipelinePosition.PerCall);
        return new EmptyCompletionGuard(new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(model).AsIChatClient());
    }
}

/// <summary>A reply that carried no completion: an HTTP 200 whose body had no choices (a gateway's error body), twice.</summary>
public sealed class EmptyCompletionException(Exception inner) : Exception("The provider answered with no completion.", inner);

/// <summary>
/// Turns the OpenAI SDK's failure on a reply with no choices into one retry, then <see cref="EmptyCompletionException"/>.
/// OpenRouter answers HTTP 200 with an error body when its upstream host fails, and the SDK then throws an index
/// <see cref="ArgumentOutOfRangeException"/> from <c>ChatCompletion.Role</c>. (LiveReuse's guard, plus the one retry.)
/// </summary>
internal sealed class EmptyCompletionGuard(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        try
        {
            return await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException first) when (first.ParamName == "index")
        {
            try
            {
                return await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "index")
            {
                throw new EmptyCompletionException(ex);
            }
        }
    }
}

/// <summary>
/// Adds <c>"provider": {"require_parameters": true}</c> to every OpenRouter request body, so a request is only routed to
/// hosts that support every parameter in it (the tools included).
/// </summary>
internal sealed class OpenRouterRoutingPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    internal static byte[] WithRouting(ReadOnlySpan<byte> body)
    {
        var node = JsonNode.Parse(body) as JsonObject
            ?? throw new InvalidOperationException("An OpenRouter request body is not a JSON object.");
        node["provider"] = new JsonObject { ["require_parameters"] = true };
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static void Rewrite(PipelineMessage message)
    {
        if (message.Request.Content is not { } content)
        {
            return;
        }

        using var buffer = new MemoryStream();
        content.WriteTo(buffer, CancellationToken.None);
        message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(WithRouting(buffer.ToArray())));
    }
}
