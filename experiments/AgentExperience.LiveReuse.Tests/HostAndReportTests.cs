using AgentExperience.LiveReuse.Harness;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.LiveReuse.Tests;

public sealed class HostAndReportTests : IDisposable
{
    private const string FakeKey = "not-a-real-host-test-key-must-not-leak";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "live-reuse-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task With_no_provider_variables_the_host_skips_successfully_and_writes_nothing()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var calledFactory = false;

        var exit = await LiveReuseHost.RunAsync([], _ => null, output, error, new SteppingClock(), _ => { calledFactory = true; return new ScriptedOperatorModel(); });

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        Assert.StartsWith("SKIPPED:", output.ToString(), StringComparison.Ordinal);
        Assert.False(calledFactory);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task A_misconfigured_provider_is_a_configuration_error_not_a_skip()
    {
        var error = new StringWriter();
        var exit = await LiveReuseHost.RunAsync([], name => name == "AGENTEXPERIENCE_LIVE_PROVIDER" ? "gemini" : null, new StringWriter(), error, new SteppingClock());
        Assert.Equal(LiveReuseHost.ExitConfigurationError, exit);
        Assert.Contains("GEMINI_API_KEY", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A full host run with a fake key and a fake Azure endpoint, the model swapped for the scripted one: the report
    /// and the raw results are written, and neither they nor the console carry the key or the endpoint's path.
    /// </summary>
    [Theory]
    [InlineData("gemini")]
    [InlineData("azure")]
    public async Task A_run_writes_the_report_and_raw_results_with_no_key_and_only_the_endpoint_host(string provider)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTEXPERIENCE_LIVE_PROVIDER"] = provider,
            ["GEMINI_API_KEY"] = FakeKey,
            ["AZURE_OPENAI_ENDPOINT"] = "https://contoso-private-resource.openai.azure.com/openai/v1/",
            ["AZURE_OPENAI_API_KEY"] = FakeKey,
            ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-4.1-mini",
            ["AGENTEXPERIENCE_LIVE_RESULTS_DIR"] = _directory,
        };
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await LiveReuseHost.RunAsync([], environment.GetValueOrDefault, output, error, new SteppingClock(), _ => new ScriptedOperatorModel());

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        var files = Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var model = provider == "gemini" ? "gemini-3.1-flash-lite" : "gpt-4.1-mini";
        Assert.Equal([$"{provider}-{model}-2026-09-26.json", $"{provider}-{model}-2026-09-26.md", "ledger.tsv"], files);

        var everything = string.Join("\n", Directory.GetFiles(_directory).Select(File.ReadAllText)) + output + error;
        Assert.DoesNotContain(FakeKey, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("/openai/v1", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1beta/openai", everything, StringComparison.Ordinal);
        // Gemini's host is public; an Azure host is the resource name, so only its domain is published.
        Assert.Contains(provider == "gemini" ? "generativelanguage.googleapis.com" : "<resource>.openai.azure.com", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso-private-resource", everything, StringComparison.Ordinal);

        // A second run the same day never overwrites the first, and the ledger records both, the second as a replication.
        await LiveReuseHost.RunAsync([], environment.GetValueOrDefault, new StringWriter(), new StringWriter(), new SteppingClock(), _ => new ScriptedOperatorModel());
        Assert.Equal(5, Directory.GetFiles(_directory).Length);
        var ledger = File.ReadAllLines(Path.Combine(_directory, "ledger.tsv"));
        Assert.Equal(5, ledger.Length);
        Assert.Contains("\tstarted\t", ledger[1], StringComparison.Ordinal);
        Assert.Contains("\tcomplete: ", ledger[2], StringComparison.Ordinal);
        Assert.Contains("1 earlier ledger entry", File.ReadAllText(Directory.GetFiles(_directory, "*-run2.md").Single()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scripted_command_prints_a_report_marked_as_scripted_and_writes_nothing()
    {
        var output = new StringWriter();
        var exit = await LiveReuseHost.RunAsync(["--scripted"], _ => null, output, new StringWriter(), new SteppingClock());

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        Assert.Contains("SCRIPTED RUN. No model was called.", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task The_report_and_raw_results_are_deterministic_for_the_same_run()
    {
        var first = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());
        var second = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());

        Assert.Equal(LiveReuseReport.Markdown(first), LiveReuseReport.Markdown(second));
        Assert.Equal(LiveReuseReport.Json(first), LiveReuseReport.Json(second));
    }

    /// <summary>The raw results carry numbers, never what the model was told: no task text, no instructions, no block.</summary>
    [Fact]
    public async Task The_raw_results_hold_no_prompt()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());
        var json = LiveReuseReport.Json(result);
        var markdown = LiveReuseReport.Markdown(result);

        foreach (var text in new[] { json, markdown })
        {
            Assert.DoesNotContain(LiveReuseExperiment.Instructions, text, StringComparison.Ordinal);
            Assert.DoesNotContain(HistoricalReferenceWriter.BlockBegin, text, StringComparison.Ordinal);
            Assert.DoesNotContain(WorkLog.Heading, text, StringComparison.Ordinal);
            Assert.All(MigrationTaskSet.Current.Instances, instance => Assert.DoesNotContain(instance.EvaluationText, text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_report_has_every_section_and_the_per_trial_table_has_every_trial()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());
        var report = LiveReuseReport.Markdown(result);

        foreach (var heading in new[] { "## Run", "## Verdict", "## Metrics by condition", "## Learning phase", "## Per-trial results", "## Tokens and cost", "## Limitations", "## Raw results" })
        {
            Assert.Contains("\n" + heading + "\n", report, StringComparison.Ordinal);
        }

        Assert.Contains(result.Design.GitBlobId, report, StringComparison.Ordinal);
        Assert.Contains(result.Design.GateExpression, report, StringComparison.Ordinal);
        Assert.Contains("SCRIPTED RUN", report, StringComparison.Ordinal);
        Assert.Contains("Est. cost", report, StringComparison.Ordinal);
        Assert.Equal(48, result.Trials.Count);
        Assert.All(result.Trials, trial => Assert.Contains($"| {trial.Sequence} | {trial.Instance} | {trial.Service} | {trial.Condition} |", report, StringComparison.Ordinal));
    }

    [Fact]
    public void The_cost_is_tokens_times_the_named_prices()
    {
        var cost = LiveReuseReport.Cost(TestSupport.ScriptedDescriptor, new UsageTotals(10, 1_000_000, 200_000, 0));
        Assert.Equal(0.25 + 0.30, cost!.Value, 10);
        Assert.Null(LiveReuseReport.Cost(TestSupport.ScriptedDescriptor with { InputUsdPerMillionTokens = null }, new UsageTotals(1, 1, 1, 0)));
    }
}
