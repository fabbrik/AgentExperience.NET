using System.Text.Json;
using AgentExperience.LiveReuse.Harness;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

/// <summary>
/// What a real provider does that the scripted model does not: JSON-typed arguments, calls to tools that do not exist,
/// replies with no tool call. The harness must record each, never replay a function call, and never crash.
/// </summary>
public sealed class ProviderShapeTests
{
    [Fact]
    public async Task Json_typed_arguments_from_a_provider_give_the_same_result_as_plain_strings()
    {
        var plain = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());
        var json = await TestSupport.RunScriptedAsync(new JsonArguments(new ScriptedOperatorModel()));

        Assert.Equal(plain.Conclusion, json.Conclusion);
        Assert.Equal(
            plain.Trials.Select(trial => (trial.Condition, trial.FailedAttempts, trial.BlockStrategy)),
            json.Trials.Select(trial => (trial.Condition, trial.FailedAttempts, trial.BlockStrategy)));
        Assert.All(json.Learning, run => Assert.NotNull(run.StoredStrategy));
    }

    [Fact]
    public async Task No_request_ever_replays_a_function_call_to_the_model()
    {
        var recording = new Recording(new ScriptedOperatorModel());
        await TestSupport.RunScriptedAsync(recording);

        Assert.NotEmpty(recording.Requests);
        Assert.All(recording.Requests, request => Assert.DoesNotContain(request.SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent));
    }

    [Fact]
    public async Task A_call_to_a_tool_that_does_not_exist_is_logged_as_text_and_counted_and_the_attempt_fails()
    {
        var recording = new Recording(new UnknownToolModel());
        var result = await TestSupport.RunScriptedAsync(recording, new LiveBudget(100_000, 100_000_000));

        Assert.True(result.Complete);
        Assert.All(result.Trials, trial =>
        {
            Assert.Equal(RunStatus.Completed, trial.Status);
            Assert.False(trial.Verified);
            Assert.Equal(result.Design.EvaluationAttemptLimit, trial.FailedAttempts);

            // Each attempt stops at the per-attempt tool-call limit.
            Assert.Equal(result.Design.EvaluationAttemptLimit * result.Design.ToolCallsPerAttempt, trial.ToolCalls);
        });

        Assert.Contains(recording.Requests, request => request.Any(message => message.Text.Contains("no tool named \"drop_database\" exists", StringComparison.Ordinal)));
        Assert.All(recording.Requests, request => Assert.DoesNotContain(request.SelectMany(message => message.Contents), content => content is FunctionResultContent));
    }

    /// <summary>Real models batch calls: describe_service and apply_migration in one response must both run.</summary>
    [Fact]
    public async Task Batched_calls_in_one_response_all_run_and_nothing_is_replayed()
    {
        var recording = new Recording(new BatchingModel());
        var result = await TestSupport.RunScriptedAsync(recording);

        Assert.True(result.Complete);
        Assert.All(result.Learning, run => Assert.NotNull(run.StoredStrategy));
        Assert.DoesNotContain(recording.Requests.SelectMany(request => request), message => message.Text.Contains("no tool named", StringComparison.Ordinal));
        Assert.All(recording.Requests, request => Assert.DoesNotContain(request.SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent));
        Assert.Equal(ComparisonVerdict.BenefitDemonstrated, result.Reference.Verdict);
    }

    /// <summary>A call whose arguments do not bind is answered with a rejection, not replayed and not fatal.</summary>
    [Fact]
    public async Task A_call_with_missing_arguments_is_rejected_as_text_and_the_run_continues()
    {
        var recording = new Recording(new MissingArgumentThenScripted(new ScriptedOperatorModel()));
        var result = await TestSupport.RunScriptedAsync(recording);

        Assert.True(result.Complete);
        Assert.All(result.Trials.Concat(result.Learning), run => Assert.Equal(RunStatus.Completed, run.Status));
        Assert.Contains(recording.Requests, request => request.Any(message => message.Text.Contains("exit=65 the call was rejected (ArgumentException)", StringComparison.Ordinal)));
        Assert.All(recording.Requests, request => Assert.DoesNotContain(request.SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent));
    }

    [Fact]
    public void Failures_are_classified_by_type_and_status_with_aggregates_unwrapped()
    {
        Assert.Equal("TaskCanceledException", LiveReuseExperiment.Classify(new AggregateException(new TaskCanceledException("secret"))));
        Assert.Equal("InvalidOperationException", LiveReuseExperiment.Classify(new InvalidOperationException("secret")));
    }

    /// <summary>Calls describe_service and apply_migration together in its first response, as real models do.</summary>
    private sealed class BatchingModel : IChatClient
    {
        private readonly ScriptedOperatorModel _inner = new();
        private int _sequence;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await _inner.GetResponseAsync(messages, options, cancellationToken);
            var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().SingleOrDefault();
            if (call?.Name != MigrationEnvironment.DescribeToolName)
            {
                return response;
            }

            // Ask the scripted model what it would do next, as if describe_service had already answered.
            var after = messages.Append(new ChatMessage(ChatRole.User, WorkLogHeadingWithDescribe(call))).ToList();
            var next = (await _inner.GetResponseAsync(after, options, cancellationToken)).Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("batch-" + ++_sequence, call.Name, call.Arguments), new FunctionCallContent("batch-" + ++_sequence, next.Name, next.Arguments)]))
            {
                Usage = response.Usage,
                ModelId = response.ModelId,
            };
        }

        private static string WorkLogHeadingWithDescribe(FunctionCallContent call) =>
            "Work log for this ticket so far (tool outputs verbatim):\n1. You called describe_service(service=\"" + LiveReuseExperiment.TextOf(call.Arguments!["service"]) + "\") -> (batched)";

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Every first call of a run is an apply_migration with no arguments; then the scripted model takes over.</summary>
    private sealed class MissingArgumentThenScripted(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            return list.Any(message => message.Text.Contains("Work log", StringComparison.Ordinal))
                ? base.GetResponseAsync(list, options, cancellationToken)
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("bad-" + Guid.NewGuid().ToString("N"), MigrationEnvironment.ApplyToolName, new Dictionary<string, object?>())]))
                {
                    Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 1 },
                });
        }
    }

    private sealed class Recording(IChatClient inner) : DelegatingChatClient(inner)
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Requests.Add(list);
            return base.GetResponseAsync(list, options, cancellationToken);
        }
    }

    /// <summary>Re-shapes every argument as a JsonElement, as the OpenAI adapter hands them back.</summary>
    private sealed class JsonArguments(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            foreach (var call in response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
            {
                foreach (var key in call.Arguments!.Keys.ToList())
                {
                    call.Arguments[key] = JsonSerializer.SerializeToElement(call.Arguments[key]);
                }
            }

            return response;
        }
    }

    private sealed class UnknownToolModel : IChatClient
    {
        private int _sequence;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("unknown-" + ++_sequence, "drop_database", new Dictionary<string, object?>())]))
            {
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 1 },
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
