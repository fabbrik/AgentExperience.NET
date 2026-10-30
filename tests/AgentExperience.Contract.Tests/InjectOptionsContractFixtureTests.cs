using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.13's checks that need no database: every inject-options fixture is what the .NET context provider asks
/// retrieval for, selects, renders and reports for the case's options.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void Every_inject_options_fixture_is_what_the_context_provider_injects()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in InjectOptionsContract.Cases)
        {
            var directory = $"fixtures/{InjectOptionsContract.Family}/{name}";
            var recordsJson = ContractJson.Write(InjectContract.ToRecordsJson(definition.Records));
            var optionsJson = ContractJson.Write(definition.Options);
            ContractFiles.Verify($"{directory}/records.json", recordsJson, drift);
            ContractFiles.Verify($"{directory}/options.json", optionsJson, drift);

            // The records and options as checked in, as a port reads them.
            var records = InjectContract.FromRecordsJson(ContractFiles.Read($"{directory}/records.json") ?? recordsJson);
            var options = ContractJson.Read<InjectOptionsInput>(ContractFiles.Read($"{directory}/options.json") ?? optionsJson);
            var run = InjectOptionsContract.Run(records, options);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(InjectOptionsContract.ExpectedJson(run)), drift);
            ContractFiles.Verify($"{directory}/expected-block.txt", run.Payload.Text, drift);
        }

        ContractFiles.VerifyNoStray(
            InjectOptionsContract.Family,
            InjectOptionsContract.Cases.Select(entry => entry.Name),
            ["records.json", "options.json", "expected.json", "expected-block.txt"],
            drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_inject_options_cases_cover_every_rule()
    {
        var runs = InjectOptionsContract.Cases.ToDictionary(
            entry => entry.Name,
            entry => (entry.Case, Run: InjectOptionsContract.Run(entry.Case.Records, entry.Case.Options)));

        IEnumerable<InjectionOmissionReason> Reasons(string name) => runs[name].Run.Omitted.Select(entry => entry.Reason);

        // Each ToolRiskClass as the maximum: the stricter, the more is omitted; Critical omits nothing.
        Assert.Equal(3, Reasons("risk-low").Count(reason => reason == InjectionOmissionReason.RiskClassExceeded));
        Assert.Equal(2, Reasons("risk-medium").Count(reason => reason == InjectionOmissionReason.RiskClassExceeded));
        Assert.Equal(1, Reasons("risk-high").Count(reason => reason == InjectionOmissionReason.RiskClassExceeded));
        Assert.Empty(runs["risk-critical"].Run.Omitted);
        Assert.Equal(3, runs["risk-critical"].Run.Request.Limit);

        // The gate's omissions carry no detail, so no tool name leaves the provider.
        Assert.All(
            runs.Values.SelectMany(entry => entry.Run.Omitted)
                .Where(entry => entry.Reason is InjectionOmissionReason.ToolUnavailable or InjectionOmissionReason.RiskClassExceeded or InjectionOmissionReason.ModelAuthored),
            entry => Assert.Null(entry.Detail));

        Assert.Equal(
            [InjectionOmissionReason.ToolUnavailable, InjectionOmissionReason.ToolUnavailable],
            Reasons("tool-unavailable"));
        Assert.Equal(
            [InjectionOmissionReason.RiskClassExceeded, InjectionOmissionReason.ToolUnavailable, InjectionOmissionReason.ToolUnavailable],
            Reasons("tool-check-before-risk"));
        Assert.Single(runs["no-tools-available"].Run.Payload.ExperienceIds);
        Assert.Equal(2, runs["run-tools"].Run.Payload.ExperienceIds.Count);

        // Limits: the gate takes no slot, the byte budget comes last, and the window is widened only for a gate.
        Assert.Equal(InjectionOmissionReason.OverRecordLimit, Reasons("gate-takes-no-slot").Last());
        Assert.Equal(InjectionOmissionReason.OverByteBudget, Reasons("gate-and-byte-budget").Last());
        Assert.Equal(6, runs["request-limit-gated-window"].Run.Request.Limit);
        Assert.Single(runs["request-limit-gated-window"].Run.Omitted);
        Assert.Equal(InjectionOutcome.NothingToInject, runs["everything-gated"].Run.Outcome);
        Assert.Equal(string.Empty, runs["everything-gated"].Run.Payload.Text);

        // The model-authored policy.
        Assert.Contains(HistoricalReferenceWriter.ModelAuthoredLine, runs["model-authored-include"].Run.Payload.Text, StringComparison.Ordinal);
        Assert.False(runs["model-authored-include"].Run.Request.ExcludeModelAuthored);
        Assert.True(runs["model-authored-exclude"].Run.Request.ExcludeModelAuthored);
        Assert.Equal([InjectionOmissionReason.ModelAuthored, InjectionOmissionReason.ModelAuthored], Reasons("model-authored-exclude"));
        Assert.Equal(InjectionOmissionReason.ModelAuthored, Reasons("model-authored-before-the-gate").First());

        // Failure detail and role.
        Assert.Contains("\"System.TimeoutException", runs["failure-detail-excerpt"].Run.Payload.Text, StringComparison.Ordinal);
        Assert.Contains("→ failed\n", runs["failure-detail-none"].Run.Payload.Text, StringComparison.Ordinal);
        Assert.Contains("retry_refund(delay=30)", runs["approach-arguments"].Run.Payload.Text, StringComparison.Ordinal);
        Assert.Equal(HistoricalReferenceMessageRole.System, runs["message-role-system-verbose"].Run.Role);
    }
}
