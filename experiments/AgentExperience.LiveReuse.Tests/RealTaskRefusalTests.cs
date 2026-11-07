using System.ClientModel;
using System.ClientModel.Primitives;
using AgentExperience.LiveReuse.Harness;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

public sealed class RealTaskRefusalTests : IDisposable
{
    /// <summary>The git blob id of <c>preregistration.realtask.json</c> as it stands; any change to the file fails until this and the amendment count are updated.</summary>
    private const string RegisteredBlobId = "3a582cfa7bfd3528ea2e66e5a286849fcdc42bdd";

    private const int RegisteredAmendments = 7;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "realtask-refusal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void The_pre_registration_is_the_checked_in_file_and_every_change_is_a_recorded_amendment()
    {
        var onDisk = File.ReadAllBytes(Path.Combine(TestSupport.ExperimentDirectory(), "preregistration.realtask.json"));
        var embedded = RealTaskDesign.ReadEmbedded();
        Assert.Equal(LivePreregistration.ComputeGitBlobId(onDisk), embedded.GitBlobId);
        Assert.True(embedded.GitBlobId == RegisteredBlobId, $"preregistration.realtask.json changed (blob {embedded.GitBlobId}, registered {RegisteredBlobId}). Record an entry in 'amendments' and update RegisteredBlobId and RegisteredAmendments.");
        Assert.Equal(RegisteredAmendments, embedded.AmendmentCount);
    }

    [Fact]
    public void Only_the_registered_http_statuses_are_a_provider_refusal()
    {
        var design = RealTaskDesign.ReadEmbedded();
        Assert.True(RealTaskExperiment.IsProviderRefusal(design, "AnthropicBadRequestException (HTTP 400)"));
        Assert.True(RealTaskExperiment.IsProviderRefusal(design, "ClientResultException (HTTP 429)"));
        Assert.True(RealTaskExperiment.IsProviderRefusal(design, "ClientResultException (HTTP 401)"));
        Assert.True(RealTaskExperiment.IsProviderRefusal(design, "ClientResultException (HTTP 403)"));
        Assert.False(RealTaskExperiment.IsProviderRefusal(design, "ClientResultException (HTTP 503)"));
        Assert.False(RealTaskExperiment.IsProviderRefusal(design, "TaskCanceledException"));
        Assert.False(RealTaskExperiment.IsProviderRefusal(design, null));
    }

    [Fact]
    public async Task A_provider_that_refuses_every_call_stops_the_run_early_and_is_not_complete()
    {
        var model = new RefusingClient(400);
        var result = await RealTaskExperiment.RunAsync(new RealTaskOptions
        {
            Model = model,
            Descriptor = new RunDescriptor("anthropic", "claude-haiku-4-5", "api.anthropic.com", 1, 5, "test price"),
            Clock = new SteppingClock(),
            NoRetryDelay = true,
        });

        Assert.False(result.Complete);
        Assert.StartsWith("the provider refused", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(RealTaskConclusion.RealTaskNotEvaluated, result.Conclusion);
        Assert.Equal(3, result.Learning.Count);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public void A_dropped_connection_is_retried_and_counts_as_a_refusal_but_a_timeout_does_not()
    {
        var design = RealTaskDesign.ReadEmbedded();
        Assert.True(RealTaskExperiment.IsProviderRefusal(design, "ClientResultException"));
        Assert.True(RealTaskExperiment.IsProviderRefusal(design, "HttpRequestException"));
        Assert.True(RetryingChatClient.IsTransientClassification("ClientResultException"));
        Assert.False(RealTaskExperiment.IsProviderRefusal(design, "model call timed out after 900 s"));
        Assert.False(RetryingChatClient.IsTransientClassification("model call timed out after 900 s"));
        Assert.False(RealTaskExperiment.IsProviderRefusal(design, "InvalidOperationException"));
    }

    [Fact]
    public void A_reply_with_no_completion_is_retried_and_counts_as_a_refusal()
    {
        var design = RealTaskDesign.ReadEmbedded();
        Assert.True(RealTaskExperiment.IsProviderRefusal(design, LiveReuseExperiment.EmptyCompletion));
        Assert.True(RetryingChatClient.IsTransientClassification(LiveReuseExperiment.EmptyCompletion));
        Assert.Equal(LiveReuseExperiment.EmptyCompletion, LiveReuseExperiment.Classify(new EmptyCompletionException(new ArgumentOutOfRangeException("index"))));
        Assert.Equal("ArgumentOutOfRangeException", LiveReuseExperiment.Classify(new ArgumentOutOfRangeException("index")));
    }

    [Fact]
    public async Task The_guard_turns_a_reply_with_no_choices_into_an_empty_completion()
    {
        using var guard = new EmptyCompletionGuard(new ThrowingChatClient(new ArgumentOutOfRangeException("index")));
        await Assert.ThrowsAsync<EmptyCompletionException>(() => guard.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
        using var other = new EmptyCompletionGuard(new ThrowingChatClient(new ArgumentOutOfRangeException("count")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => other.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
    }

    [Fact]
    public async Task A_refusal_is_not_retried_but_a_503_is()
    {
        Assert.False(RetryingChatClient.IsTransient(new ClientResultException(new FakeResponse(400))));
        Assert.True(RetryingChatClient.IsTransient(new ClientResultException(new FakeResponse(503))));
        Assert.True(RetryingChatClient.IsTransient(new ClientResultException(new FakeResponse(429))));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_refused_live_run_is_recorded_as_stopped_and_leaves_the_confirmatory_slot_free()
    {
        var environment = new Dictionary<string, string?>
        {
            ["AGENTEXPERIENCE_LIVE_PROVIDER"] = "anthropic",
            ["ANTHROPIC_API_KEY"] = "sk-test-0123456789-not-a-real-key",
            ["AGENTEXPERIENCE_LIVE_RESULTS_DIR"] = _directory,
        };

        var exit = await LiveReuseHost.RunAsync(["--experiment", "realtask"], environment.GetValueOrDefault, new StringWriter(), new StringWriter(), new SteppingClock(), _ => new RefusingClient(401));

        Assert.Equal(LiveReuseHost.ExitStoppedAtBudget, exit);
        var ledger = File.ReadAllLines(Path.Combine(_directory, "realtask-ledger.tsv"));
        Assert.Contains("stopped: provider refused", ledger[^1], StringComparison.Ordinal);
        Assert.Equal(0, new RunLedger(Path.Combine(_directory, "realtask-ledger.tsv")).CountCompleteFor("anthropic", "claude-haiku-4-5"));
        Assert.Equal(1, new RunLedger(Path.Combine(_directory, "realtask-ledger.tsv")).CountFor("anthropic", "claude-haiku-4-5"));
    }

    [Fact]
    public async Task A_run_after_only_stopped_and_voided_runs_is_still_the_confirmatory_one()
    {
        var environment = new Dictionary<string, string?>
        {
            ["AGENTEXPERIENCE_LIVE_PROVIDER"] = "anthropic",
            ["ANTHROPIC_API_KEY"] = "sk-test-0123456789-not-a-real-key",
            ["AGENTEXPERIENCE_LIVE_RESULTS_DIR"] = _directory,
        };

        // A stopped run, then a second run refused the same way: neither is complete, so neither uses the slot.
        await LiveReuseHost.RunAsync(["--experiment", "realtask"], environment.GetValueOrDefault, new StringWriter(), new StringWriter(), new SteppingClock(), _ => new RefusingClient(401));
        await LiveReuseHost.RunAsync(["--experiment", "realtask"], environment.GetValueOrDefault, new StringWriter(), new StringWriter(), new SteppingClock(), _ => new RefusingClient(401));

        var report = Directory.GetFiles(_directory, "realtask-anthropic-*.md").OrderBy(File.GetLastWriteTimeUtc).Last();
        Assert.Contains("0 earlier complete run(s)", File.ReadAllText(report), StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_model_run_has_its_own_registered_repeats_and_timeout()
    {
        var design = RealTaskDesign.ReadEmbedded();
        Assert.Equal(3, design.Repeats);
        Assert.Equal(1, design.LocalRepeats);
        Assert.Equal(900, design.LocalCallTimeoutSeconds);
    }

    [Fact]
    public void A_voided_run_is_not_counted_as_complete()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "void-ledger.tsv");
        File.WriteAllText(path, "run\tutc\tprovider\tmodel\thost\tpreregistration\tevent\treport\n"
            + "r1\tt\tp\tm\th\tb\tcomplete: RealTaskInconclusive\tx.md\n"
            + "r2\tt\tp\tm\th\tb\tcomplete: RealTaskInconclusive\ty.md\n"
            + "r1\tt\tp\tm\th\tb\tvoid: amendment\t-\n");
        Assert.Equal(1, new RunLedger(path).CountCompleteFor("p", "m"));
    }

    private sealed class RefusingClient(int status) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<ChatResponse>(new ClientResultException(new FakeResponse(status)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeResponse(int status) : PipelineResponse
    {
        private Stream? _content = new MemoryStream();

        public override int Status => status;

        public override string ReasonPhrase => "fake";

        public override Stream? ContentStream
        {
            get => _content;
            set => _content = value;
        }

        public override BinaryData Content => BinaryData.Empty;

        protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(BinaryData.Empty);

        public override void Dispose()
        {
        }
    }
}

internal sealed class ThrowingChatClient(Exception exception) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw exception;

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw exception;

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
