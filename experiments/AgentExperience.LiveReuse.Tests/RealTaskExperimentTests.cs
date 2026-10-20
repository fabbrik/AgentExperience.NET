using AgentExperience.LiveReuse.Harness;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

public sealed class RealTaskExperimentTests : IDisposable
{
    private static readonly RunDescriptor Descriptor = new("scripted", ScriptedRealTaskModel.ModelId, "none (offline)", 0.25, 1.50, "test price");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "realtask-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Task<RealTaskResult> RunAsync(IChatClient model, int repeats = 1, LiveBudget? budget = null) =>
        RealTaskExperiment.RunAsync(new RealTaskOptions
        {
            Model = model,
            Descriptor = Descriptor,
            Design = RealTaskDesign.ReadEmbedded() with { Repeats = repeats },
            Budget = budget,
            Clock = new SteppingClock(),
            NoRetryDelay = true,
        });

    [Fact]
    public async Task A_following_model_cuts_the_tool_calls_and_every_comparison_reads_as_designed()
    {
        var result = await RunAsync(new ScriptedRealTaskModel());

        Assert.True(result.Complete);
        Assert.Equal(12, result.Learning.Count);
        Assert.All(result.Learning, run => Assert.True(run.Verified));
        Assert.All(result.Learning, run => Assert.Contains(RealTaskSet.ClusterOf(RealTaskSet.Current.Services[run.Instance]).FixPath, run.StoredFixPaths));
        Assert.Equal(48, result.Trials.Count);
        Assert.Empty(result.Excluded);

        // The library's own retrieval put the sibling's lesson first in every memory-enabled block.
        var enabled = result.Trials.Where(trial => trial.Condition == result.Design.TreatmentLabel).ToList();
        Assert.All(enabled, trial => Assert.Equal(1, trial.SiblingRank));
        Assert.All(enabled, trial => Assert.True(trial.BlockNamedClusterFix));
        Assert.All(result.Trials.Where(trial => trial.Condition == result.Design.ControlLabel), trial => Assert.False(trial.BlockSeen));
        Assert.All(result.Trials.Where(trial => trial.Condition == result.Design.PlaceboLabel), trial => Assert.Empty(trial.BlockPaths));
        Assert.All(result.Trials, trial => Assert.True(trial.Score <= result.Design.EvaluationToolCalls));
        Assert.All(result.Trials.Where(trial => trial.Condition == result.Design.MismatchedLabel), trial => Assert.Null(trial.SiblingRank));

        Assert.Equal(3, enabled.Average(trial => trial.Score));
        Assert.Equal(9, result.Trials.Where(trial => trial.Condition == result.Design.ControlLabel).Average(trial => trial.Score));
        Assert.Equal(ComparisonVerdict.BenefitDemonstrated, result.Reference.Verdict);
        Assert.Equal(ComparisonVerdict.BenefitDemonstrated, result.Content.Verdict);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.MismatchedControl.Verdict);
        Assert.Equal(RealTaskConclusion.RealTaskBenefitAttributableToContent, result.Conclusion);
    }

    [Fact]
    public async Task A_model_that_ignores_the_block_shows_no_benefit_in_any_comparison()
    {
        var result = await RunAsync(new ScriptedRealTaskModel(ScriptedRealTaskBehavior.IgnoresBlock));

        Assert.True(result.Complete);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.Reference.Verdict);
        Assert.Equal(RealTaskConclusion.RealTaskNoDemonstratedBenefit, result.Conclusion);
    }

    [Fact]
    public async Task A_model_that_never_acts_verifies_nothing_so_every_service_is_excluded_and_the_run_is_inconclusive()
    {
        var result = await RunAsync(new ScriptedRealTaskModel(ScriptedRealTaskBehavior.NeverActs));

        Assert.All(result.Learning, run => Assert.NotEqual(true, run.Verified));
        Assert.Empty(result.Trials);
        Assert.Equal(12, result.Excluded.Count);
        Assert.Equal(ComparisonVerdict.Inconclusive, result.Reference.Verdict);
        Assert.Equal(RealTaskConclusion.RealTaskInconclusive, result.Conclusion);
    }

    [Fact]
    public async Task A_budget_cap_stops_the_run_before_the_next_call_and_evaluates_no_verdict()
    {
        var result = await RunAsync(new ScriptedRealTaskModel(), budget: new LiveBudget(40, 10_000_000));

        Assert.False(result.Complete);
        Assert.NotNull(result.StopReason);
        Assert.Equal(ComparisonVerdict.NotEvaluated, result.Reference.Verdict);
        Assert.Equal(RealTaskConclusion.RealTaskNotEvaluated, result.Conclusion);
        Assert.True(result.Total.ModelCalls <= 40);
    }

    [Fact]
    public async Task A_service_is_never_shown_its_own_lesson_and_the_condition_order_rotates()
    {
        var result = await RunAsync(new ScriptedRealTaskModel());
        Assert.All(result.Trials, trial => Assert.DoesNotContain(trial.Service + RealTaskExperiment.LearningTaskSuffix, trial.BlockRecords));

        var design = RealTaskDesign.ReadEmbedded();
        var first = Enumerable.Range(0, 4).Select(index => RealTaskExperiment.ConditionOrder(design, index, 0)[0]).ToList();
        Assert.Equal(4, first.Distinct().Count());
        Assert.Equal(first, Enumerable.Range(0, 4).Select(index => RealTaskExperiment.ConditionOrder(design, 0, index)[0]).ToList());
    }

    [Fact]
    public void The_service_level_gate_counts_services_not_repeats()
    {
        var design = RealTaskDesign.ReadEmbedded();

        // 12 services, 3 repeats each: the treatment is better in 9 of 12 services. At the trial level that is a
        // lopsided 27 of 36, but 9 of 12 services is p = 0.073, which the pre-registered service-level gate does not pass.
        var trials = new List<RealTaskRun>();
        for (var service = 0; service < 12; service++)
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                trials.Add(Trial(service, repeat, design.TreatmentLabel, score: service < 9 ? 3 : 12));
                trials.Add(Trial(service, repeat, design.ControlLabel, score: 9));
            }
        }

        var comparison = RealTaskExperiment.Evaluate(design.TreatmentLabel, design.ControlLabel, trials, Enumerable.Range(0, 12).ToList(), 0, design, complete: true);
        Assert.Equal(9, comparison.ServiceSignTest!.Wins);
        Assert.Equal(3, comparison.ServiceSignTest.Losses);
        Assert.True(comparison.ServiceSignTest.PValue > 0.05);
        Assert.Equal(27, comparison.TrialSignTest!.Wins);
        Assert.True(comparison.TrialSignTest.PValue < 0.05);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, comparison.Verdict);

        // 10 of 12 passes.
        trials = [.. trials.Select(trial => trial.Instance is 9 && trial.Condition == design.TreatmentLabel ? trial with { Score = 3 } : trial)];
        trials = [.. trials.Select(trial => trial.Instance is 10 && trial.Condition == design.TreatmentLabel ? trial with { Score = 3 } : trial)];
        comparison = RealTaskExperiment.Evaluate(design.TreatmentLabel, design.ControlLabel, trials, Enumerable.Range(0, 12).ToList(), 0, design, complete: true);
        Assert.Equal(11, comparison.ServiceSignTest!.Wins);
        Assert.Equal(ComparisonVerdict.BenefitDemonstrated, comparison.Verdict);
    }

    [Fact]
    public void More_exclusions_than_the_limit_is_inconclusive_not_a_failed_gate()
    {
        var design = RealTaskDesign.ReadEmbedded();
        var comparison = RealTaskExperiment.Evaluate(design.TreatmentLabel, design.ControlLabel, [], [0, 1, 2], design.MaxExcludedServices + 1, design, complete: true);
        Assert.Equal(ComparisonVerdict.Inconclusive, comparison.Verdict);
    }

    [Fact]
    public async Task Transient_provider_errors_are_retried_up_to_the_registered_tries_and_others_are_not()
    {
        var flaky = new FlakyClient(failures: 2, new InvalidOperationException("transient"));
        using var retrying = new RetryingChatClient(flaky, 3, [0], noDelay: true, isTransient: _ => true);
        var response = await retrying.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("ok", response.Text);
        Assert.Equal(3, flaky.Calls);

        var hopeless = new FlakyClient(failures: 5, new InvalidOperationException("transient"));
        using var giving = new RetryingChatClient(hopeless, 3, [0], noDelay: true, isTransient: _ => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => giving.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Equal(3, hopeless.Calls);

        var permanent = new FlakyClient(failures: 5, new InvalidOperationException("bad request"));
        using var refusing = new RetryingChatClient(permanent, 3, [0], noDelay: true, isTransient: _ => false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Equal(1, permanent.Calls);
    }

    [Theory]
    [InlineData("ClientResultException (HTTP 503)", true)]
    [InlineData("ClientResultException (HTTP 429)", true)]
    [InlineData("AnthropicApiException (HTTP 529)", true)]
    [InlineData("ClientResultException (HTTP 400)", false)]
    [InlineData("ClientResultException (HTTP 401)", false)]
    [InlineData("TaskCanceledException", false)]
    public void Only_429_and_5xx_are_transient(string classification, bool transient) =>
        Assert.Equal(transient, RetryingChatClient.IsTransientClassification(classification));

    [Fact]
    public async Task A_scripted_command_prints_the_report_and_writes_nothing()
    {
        var output = new StringWriter();
        var exit = await LiveReuseHost.RunAsync(["--experiment", "realtask", "--scripted"], _ => null, output, new StringWriter(), new SteppingClock());

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        Assert.Contains("Scripted run, not a model", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("RealTaskBenefitAttributableToContent", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task An_unconfigured_live_run_skips_successfully_and_writes_no_ledger_row()
    {
        var output = new StringWriter();
        var calledFactory = false;
        var exit = await LiveReuseHost.RunAsync(
            ["--experiment", "realtask"],
            name => name == LiveConfiguration.ResultsDirectoryVariable ? _directory : null,
            output,
            new StringWriter(),
            new SteppingClock(),
            _ => { calledFactory = true; return new ScriptedRealTaskModel(); });

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        Assert.StartsWith("SKIPPED:", output.ToString(), StringComparison.Ordinal);
        Assert.False(calledFactory);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task A_live_run_writes_the_ledger_before_the_first_call_and_never_the_key()
    {
        const string FakeKey = "sk-test-0123456789-not-a-real-key";
        var environment = new Dictionary<string, string?>
        {
            ["AGENTEXPERIENCE_LIVE_PROVIDER"] = "anthropic",
            ["ANTHROPIC_API_KEY"] = FakeKey,
            ["AGENTEXPERIENCE_LIVE_RESULTS_DIR"] = _directory,
        };
        var ledgerAtFirstCall = string.Empty;
        var output = new StringWriter();
        var error = new StringWriter();

        // A model that records the ledger the first time it is called and then fails every call: all twelve learning runs
        // error, so every service is excluded and the run ends inconclusive without a trial.
        var model = new ObservingClient(() => ledgerAtFirstCall = File.ReadAllText(Path.Combine(_directory, "realtask-ledger.tsv")));
        var exit = await LiveReuseHost.RunAsync(["--experiment", "realtask"], environment.GetValueOrDefault, output, error, new SteppingClock(), _ => model);

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        Assert.Contains("\tstarted\t", ledgerAtFirstCall, StringComparison.Ordinal);
        Assert.DoesNotContain("complete", ledgerAtFirstCall, StringComparison.Ordinal);

        var files = Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["realtask-anthropic-claude-haiku-4-5-2026-09-26.json", "realtask-anthropic-claude-haiku-4-5-2026-09-26.md", "realtask-ledger.tsv"], files);
        var ledger = File.ReadAllLines(Path.Combine(_directory, "realtask-ledger.tsv"));
        Assert.Equal(3, ledger.Length);
        Assert.Contains("complete: RealTaskInconclusive", ledger[2], StringComparison.Ordinal);

        var everything = string.Join("\n", Directory.GetFiles(_directory).Select(File.ReadAllText)) + output + error;
        Assert.DoesNotContain(FakeKey, everything, StringComparison.Ordinal);
        Assert.Contains("api.anthropic.com", everything, StringComparison.Ordinal);
    }

    private static RealTaskRun Trial(int service, int repeat, string condition, int score) => new(
        Sequence: 1,
        Phase: "evaluation",
        Instance: service,
        Service: "s" + service.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Cluster: "c",
        Condition: condition,
        Repeat: repeat,
        Status: RunStatus.Completed,
        Classification: null,
        Verified: true,
        ToolCalls: score,
        TestRuns: 1,
        Score: score,
        WrittenPaths: [],
        ReadsBeforeFirstWrite: 0,
        FirstWriteWasClusterFix: null,
        UnauthorizedRequests: 0,
        BlockSeen: false,
        BlockPaths: [],
        BlockRecords: [],
        Usage: UsageTotals.Zero,
        LatencyMilliseconds: 0,
        StoredFixPaths: []);

    private sealed class FlakyClient(int failures, Exception failure) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            ++Calls <= failures ? Task.FromException<ChatResponse>(failure) : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ObservingClient(Action onFirstCall) : IChatClient
    {
        private bool _called;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!_called)
            {
                _called = true;
                onFirstCall();
            }

            return Task.FromException<ChatResponse>(new InvalidOperationException("provider unavailable"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
