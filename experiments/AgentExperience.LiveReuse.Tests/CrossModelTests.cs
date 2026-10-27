using AgentExperience.LiveReuse.Harness;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

/// <summary>Real-task amendment 7: the learning phase may use a different model from the evaluation phase.</summary>
public sealed class CrossModelTests : IDisposable
{
    private const string FakeKey = "sk-or-fake-cross-model-key-should-never-appear";
    private const string Learning = "anthropic/claude-haiku-4.5";
    private const string Evaluation = "openai/gpt-oss-120b";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "realtask-cross-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static (ConfigurationOutcome Outcome, LiveConfiguration? Configuration, string Message) Read(params (string Name, string Value)[] extra)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTEXPERIENCE_LIVE_PROVIDER"] = "openrouter",
            ["OPENROUTER_API_KEY"] = FakeKey,
            ["OPENROUTER_MODEL"] = Evaluation,
        };
        foreach (var (name, value) in extra)
        {
            map[name] = value;
        }

        return LiveConfiguration.Read(name => map.GetValueOrDefault(name), 2600, 11_000_000);
    }

    [Fact]
    public void Unset_is_a_same_model_run_and_the_descriptor_is_unchanged()
    {
        var (outcome, configuration, _) = Read();
        Assert.Equal(ConfigurationOutcome.Ready, outcome);
        Assert.Null(configuration!.Learning);
        Assert.Equal(configuration.Describe(), configuration.DescribeRealTask());
        Assert.Equal(Evaluation, configuration.DescribeRealTask().LedgerModel);
        Assert.Same(configuration, configuration.ForLearning());
    }

    [Fact]
    public void A_learning_model_equal_to_the_evaluation_model_is_treated_as_unset()
    {
        var (outcome, configuration, _) = Read((LiveConfiguration.LearningModelVariable, Evaluation));
        Assert.Equal(ConfigurationOutcome.Ready, outcome);
        Assert.Null(configuration!.Learning);
        Assert.Equal(configuration.Describe(), configuration.DescribeRealTask());
    }

    [Fact]
    public void A_learning_model_names_the_pair_and_its_own_client_on_the_same_provider()
    {
        var (outcome, configuration, _) = Read((LiveConfiguration.LearningModelVariable, Learning));
        Assert.Equal(ConfigurationOutcome.Ready, outcome);

        var descriptor = configuration!.DescribeRealTask();
        Assert.Equal(Evaluation, descriptor.RequestedModel);
        Assert.Equal(Learning, descriptor.LearningModel);
        Assert.Equal(Learning + "→" + Evaluation, descriptor.LedgerModel);
        Assert.Null(descriptor.LearningInputUsdPerMillionTokens);
        Assert.Contains("learning model", descriptor.LearningPriceSource, StringComparison.Ordinal);

        // The other experiments never see it.
        Assert.Null(configuration.Describe().LearningModel);

        var learning = configuration.ForLearning();
        Assert.Equal(LiveProvider.OpenRouter, learning.Provider);
        Assert.Equal(Learning, learning.Model);
        Assert.Equal(configuration.Endpoint, learning.Endpoint);
        Assert.Null(learning.Learning);
        using var client = learning.CreateChatClient();
        Assert.NotNull(client);
        Assert.DoesNotContain(FakeKey, learning.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_learning_prices_come_from_their_own_variables_and_bad_values_are_refused()
    {
        var (outcome, configuration, _) = Read(
            (LiveConfiguration.LearningModelVariable, Learning),
            (LiveConfiguration.LearningInputPriceVariable, "1"),
            (LiveConfiguration.LearningOutputPriceVariable, "5"),
            (LiveConfiguration.InputPriceVariable, "0.15"),
            (LiveConfiguration.OutputPriceVariable, "0.6"));
        Assert.Equal(ConfigurationOutcome.Ready, outcome);
        var descriptor = configuration!.DescribeRealTask();
        Assert.Equal(1, descriptor.LearningInputUsdPerMillionTokens);
        Assert.Equal(5, descriptor.LearningOutputUsdPerMillionTokens);
        Assert.Equal(0.15, descriptor.InputUsdPerMillionTokens);
        Assert.Equal(1, descriptor.LearningPhaseDescriptor().InputUsdPerMillionTokens);
        Assert.Equal(1, configuration.ForLearning().InputPrice);

        Assert.Equal(ConfigurationOutcome.Invalid, Read((LiveConfiguration.LearningModelVariable, Learning), (LiveConfiguration.LearningInputPriceVariable, "1")).Outcome);
        Assert.Equal(ConfigurationOutcome.Invalid, Read((LiveConfiguration.LearningModelVariable, Learning), (LiveConfiguration.LearningInputPriceVariable, "-1"), (LiveConfiguration.LearningOutputPriceVariable, "5")).Outcome);
    }

    [Theory]
    [InlineData("openrouter", "a b")]
    [InlineData("openrouter", "model;drop")]
    [InlineData("anthropic", "anthropic/claude-haiku-4.5")]
    [InlineData("gemini", "gemini:flash")]
    public void A_malformed_learning_model_is_a_configuration_error(string provider, string learning)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTEXPERIENCE_LIVE_PROVIDER"] = provider,
            ["OPENROUTER_API_KEY"] = FakeKey,
            ["OPENROUTER_MODEL"] = Evaluation,
            ["ANTHROPIC_API_KEY"] = FakeKey,
            ["GEMINI_API_KEY"] = FakeKey,
            [LiveConfiguration.LearningModelVariable] = learning,
        };
        var (outcome, configuration, message) = LiveConfiguration.Read(name => map.GetValueOrDefault(name), 2600, 11_000_000);
        Assert.Equal(ConfigurationOutcome.Invalid, outcome);
        Assert.Null(configuration);
        Assert.Contains(LiveConfiguration.LearningModelVariable, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_learning_model_stops_a_live_run_before_anything_is_spent_or_written()
    {
        var environment = new Dictionary<string, string?>
        {
            ["AGENTEXPERIENCE_LIVE_PROVIDER"] = "openrouter",
            ["OPENROUTER_API_KEY"] = FakeKey,
            ["OPENROUTER_MODEL"] = Evaluation,
            [LiveConfiguration.LearningModelVariable] = "a b",
            ["AGENTEXPERIENCE_LIVE_RESULTS_DIR"] = _directory,
        };
        var called = false;
        var exit = await LiveReuseHost.RunAsync(["--experiment", "realtask"], environment.GetValueOrDefault, new StringWriter(), new StringWriter(), new SteppingClock(), _ => { called = true; return new ScriptedRealTaskModel(); });

        Assert.Equal(LiveReuseHost.ExitConfigurationError, exit);
        Assert.False(called);
        Assert.False(Directory.Exists(_directory));

        var scripted = await LiveReuseHost.RunAsync(["--experiment", "realtask", "--scripted"], name => name == LiveConfiguration.LearningModelVariable ? "a b" : null, new StringWriter(), new StringWriter(), new SteppingClock());
        Assert.Equal(LiveReuseHost.ExitConfigurationError, scripted);
    }

    [Fact]
    public async Task Every_learning_call_goes_to_the_learning_model_and_every_trial_call_to_the_evaluation_model()
    {
        var evaluation = new ScriptedRealTaskModel();
        var learning = new ScriptedRealTaskModel();
        var result = await RealTaskExperiment.RunAsync(new RealTaskOptions
        {
            Model = evaluation,
            LearningModel = learning,
            Descriptor = new RunDescriptor("scripted", ScriptedRealTaskModel.ModelId, "none (offline)", null, null, "none") { LearningModel = Learning },
            Design = RealTaskDesign.ReadEmbedded() with { Repeats = 1 },
            Clock = new SteppingClock(),
            NoRetryDelay = true,
        });

        Assert.True(result.Complete);
        Assert.Equal(result.Learning.Sum(run => run.Usage.ModelCalls), learning.Calls.Count);
        Assert.Equal(result.Trials.Sum(run => run.Usage.ModelCalls), evaluation.Calls.Count);
        Assert.True(learning.Calls.Count > 0);
        Assert.True(evaluation.Calls.Count > 0);
        Assert.Equal(result.Total.ModelCalls, learning.Calls.Count + evaluation.Calls.Count);

        // The scripted stand-in plays both models identically, so the result is the same-model scripted result.
        Assert.Equal(RealTaskConclusion.RealTaskBenefitAttributableToContent, result.Conclusion);
    }

    [Fact]
    public async Task A_scripted_cross_model_run_reports_the_pair()
    {
        var output = new StringWriter();
        var exit = await LiveReuseHost.RunAsync(
            ["--experiment", "realtask", "--scripted"],
            name => name == LiveConfiguration.LearningModelVariable ? Learning : null,
            output,
            new StringWriter(),
            new SteppingClock());

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        var report = output.ToString();
        Assert.Contains($"# Real-task experiment: scripted / learning {Learning} → evaluation {ScriptedRealTaskModel.ModelId} / 2026-09-26", report, StringComparison.Ordinal);
        Assert.Contains($"`{Learning}→{ScriptedRealTaskModel.ModelId}`", report, StringComparison.Ordinal);
        Assert.Contains("| Learning model (learning runs only) |", report, StringComparison.Ordinal);
        Assert.Contains("| Evaluation model (every trial) |", report, StringComparison.Ordinal);
        Assert.Contains("The learning phase's cost was not estimated", report, StringComparison.Ordinal);
        Assert.DoesNotContain("| Model requested |", report, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task A_live_cross_model_run_records_the_pair_in_the_ledger_and_report_and_calls_each_client_in_its_phase()
    {
        var environment = new Dictionary<string, string?>
        {
            ["AGENTEXPERIENCE_LIVE_PROVIDER"] = "openrouter",
            ["OPENROUTER_API_KEY"] = FakeKey,
            ["OPENROUTER_MODEL"] = Evaluation,
            [LiveConfiguration.LearningModelVariable] = Learning,
            ["AGENTEXPERIENCE_LIVE_PRICE_INPUT_PER_MTOK"] = "0.15",
            ["AGENTEXPERIENCE_LIVE_PRICE_OUTPUT_PER_MTOK"] = "0.6",
            ["AGENTEXPERIENCE_LIVE_MAX_CALLS"] = "200",
            ["AGENTEXPERIENCE_LIVE_RESULTS_DIR"] = _directory,
        };

        // One scripted stand-in per model the host asks for: the factory is called with the evaluation configuration and
        // with the learning configuration, and each client counts its own calls.
        var clients = new Dictionary<string, ScriptedRealTaskModel>(StringComparer.Ordinal);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await LiveReuseHost.RunAsync(
            ["--experiment", "realtask"],
            environment.GetValueOrDefault,
            output,
            error,
            new SteppingClock(),
            configuration => clients[configuration.Model] = new ScriptedRealTaskModel());

        // The 200-call cap stops the run in the evaluation phase, after every learning run.
        Assert.Equal(LiveReuseHost.ExitStoppedAtBudget, exit);
        Assert.Equal([Learning, Evaluation], clients.Keys.Order(StringComparer.Ordinal));
        Assert.True(clients[Learning].Calls.Count > 0);
        Assert.True(clients[Evaluation].Calls.Count > 0);

        var baseName = "realtask-openrouter-anthropic-claude-haiku-4.5-to-openai-gpt-oss-120b-2026-09-26";
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, baseName + ".json"));
        var document = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Assert.Equal(Learning, (string?)document["learningModel"]);
        Assert.Equal(Evaluation, (string?)document["requestedModel"]);
        Assert.Equal(Learning + "→" + Evaluation, (string?)document["ledgerModel"]);
        Assert.Null(document["estimatedCostUsd"]);
        Assert.Null(document["estimatedLearningCostUsd"]);
        var learningRuns = document["learning"]!.AsArray();
        Assert.Equal(12, learningRuns.Count);
        Assert.Equal(learningRuns.Sum(run => (int)run!["usage"]!["modelCalls"]!), clients[Learning].Calls.Count);
        Assert.Equal(document["trials"]!.AsArray().Sum(run => (int)run!["usage"]!["modelCalls"]!), clients[Evaluation].Calls.Count);

        var report = await File.ReadAllTextAsync(Path.Combine(_directory, baseName + ".md"));
        Assert.Contains($"learning {Learning} → evaluation {Evaluation}", report, StringComparison.Ordinal);
        Assert.Contains($"| Evaluation model (every trial) | `{Evaluation}`; prices USD 0.15 / 0.6", report, StringComparison.Ordinal);
        Assert.Contains("The learning phase's cost was not estimated", report, StringComparison.Ordinal);

        var ledgerPath = Path.Combine(_directory, "realtask-ledger.tsv");
        var ledger = File.ReadAllLines(ledgerPath);
        Assert.Equal(3, ledger.Length);
        Assert.All(ledger.Skip(1), row => Assert.Equal(Learning + "→" + Evaluation, row.Split('\t')[3]));
        Assert.Contains("stopped at budget cap", ledger[2], StringComparison.Ordinal);
        var counted = new RunLedger(ledgerPath);
        Assert.Equal(1, counted.CountFor("openrouter", Learning + "→" + Evaluation));
        Assert.Equal(0, counted.CountFor("openrouter", Evaluation));

        var everything = string.Join("\n", Directory.GetFiles(_directory).Select(File.ReadAllText)) + output + error;
        Assert.DoesNotContain(FakeKey, everything, StringComparison.Ordinal);
    }

    [Fact]
    public void A_same_model_ledger_row_is_unchanged()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "ledger.tsv");
        new RunLedger(path).Append("r1", SteppingClock.Now, new RunDescriptor("openrouter", Evaluation, "openrouter.ai", null, null, "none"), "blob", "started", "-");
        Assert.Equal("r1\t2026-09-26T12:00:00Z\topenrouter\topenai/gpt-oss-120b\topenrouter.ai\tblob\tstarted\t-", File.ReadAllLines(path)[1]);
    }

    [Fact]
    public async Task The_phase_switch_routes_each_call_to_the_current_phase()
    {
        var evaluation = new ScriptedRealTaskModel(ScriptedRealTaskBehavior.NeverActs);
        var learning = new ScriptedRealTaskModel(ScriptedRealTaskBehavior.NeverActs);
        using var router = new PhaseRoutingChatClient(evaluation, learning);
        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        router.UseLearning(true);
        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        router.UseLearning(false);
        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        Assert.Equal(2, evaluation.Calls.Count);
        Assert.Equal(2, learning.Calls.Count);
    }
}
