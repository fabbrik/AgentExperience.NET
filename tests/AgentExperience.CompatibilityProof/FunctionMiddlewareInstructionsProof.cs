using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.CompatibilityProof;

/// <summary>
/// Story 28.3: the mechanism recall at the moment of failure rests on. An agent-level function middleware
/// (<see cref="FunctionInvocationDelegatingAgentBuilderExtensions.Use(AIAgentBuilder, Func{AIAgent, FunctionInvocationContext, Func{FunctionInvocationContext, CancellationToken, ValueTask{object}}, CancellationToken, ValueTask{object}})"/>)
/// that appends to <see cref="FunctionInvocationContext.Options"/>' <see cref="ChatOptions.Instructions"/> after the
/// tool returned or threw puts that text in front of the model's next call in the same run, and of no other run, while
/// the tool's result (or its exception) reaches the model exactly as it would have without the middleware. Run against
/// the pinned floor here and against the newest 1.x by CI's floating leg.
/// </summary>
/// <remarks>
/// Why it holds: MAF's function middleware rebuilds the run's chat client per run with a client that clones the
/// <see cref="ChatOptions"/> it is handed, and <see cref="FunctionInvokingChatClient"/> hands that clone both to each
/// <see cref="FunctionInvocationContext"/> and to every model call of its loop (a clone it makes between iterations
/// copies <see cref="ChatOptions.Instructions"/> with the rest).
/// </remarks>
public class FunctionMiddlewareInstructionsProof
{
    private const string Hint = "HINT-AFTER-FAILURE";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Instructions_appended_after_a_failed_call_reach_the_next_model_call_of_that_run_only(bool throws, bool streaming)
    {
        var model = new TwoTurnModel();
        var tool = AIFunctionFactory.Create(
            (string input) => throws ? throw new InvalidOperationException("tool failed") : "exit=2",
            "run_tests");
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Instructions = "Base instructions.", Tools = [tool] },
        })
            .AsBuilder()
            .Use(async (innerAgent, context, next, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                finally
                {
                    context.Options!.Instructions += "\n\n" + Hint;
                }
            })
            .Build();

        await Run(agent, streaming);
        await Run(agent, streaming);

        // Each run: the first call has the base instructions only, the second (after the tool) has the hint once.
        Assert.Equal(4, model.Instructions.Count);
        foreach (var run in new[] { 0, 2 })
        {
            Assert.Equal("Base instructions.", model.Instructions[run]);
            Assert.Equal("Base instructions.\n\n" + Hint, model.Instructions[run + 1]);
        }

        // The tool's result reached the model as it was: the exception as an error result, the return value as returned.
        var result = Assert.Single(model.Results);
        if (throws)
        {
            Assert.IsType<InvalidOperationException>(result.Exception);
        }
        else
        {
            Assert.Null(result.Exception);
            Assert.Equal("exit=2", result.Result?.ToString());
        }
    }

    private static async Task Run(AIAgent agent, bool streaming)
    {
        if (streaming)
        {
            await foreach (var _ in agent.RunStreamingAsync("go"))
            {
            }
        }
        else
        {
            await agent.RunAsync("go");
        }
    }

    /// <summary>Calls the tool on a run's first turn, answers on its second; records each call's instructions and the first tool result.</summary>
    private sealed class TwoTurnModel : IChatClient
    {
        public List<string?> Instructions { get; } = [];

        public List<FunctionResultContent> Results { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Instructions.Add(options?.Instructions);
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToList();
            if (results.Count == 0)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-" + Instructions.Count, "run_tests", new Dictionary<string, object?> { ["input"] = "x" })])));
            }

            if (Results.Count == 0)
            {
                Results.AddRange(results);
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
