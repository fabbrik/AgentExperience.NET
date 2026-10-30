using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.13's checks that need no database: every inject-options fixture is what a real .NET context provider,
/// on a <see cref="Microsoft.Agents.AI.ChatClientAgent"/>, ranked, asked, injected and reported for the case's options.
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
            var run = InjectOptionsContract.Run(definition);
            ContractFiles.Verify($"{directory}/records.json", ContractJson.Write(InjectContract.ToRecordsJson(run.Ranked)), drift);
            ContractFiles.Verify($"{directory}/options.json", ContractJson.Write(definition.Options), drift);
            if (definition.Estimate is { } estimate)
            {
                ContractFiles.Verify($"{directory}/estimate.json", ContractJson.Write(BenefitSelectContract.EstimateJson(estimate)), drift);
            }

            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(InjectOptionsContract.ExpectedJson(run)), drift);
            ContractFiles.Verify($"{directory}/expected-block.txt", run.Block?.Text ?? string.Empty, drift);
        }

        ContractFiles.VerifyNoStray(
            InjectOptionsContract.Family,
            InjectOptionsContract.Cases.Select(entry => entry.Name),
            ["records.json", "options.json", "estimate.json", "expected.json", "expected-block.txt"],
            drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_inject_options_cases_cover_every_rule()
    {
        var runs = InjectOptionsContract.Cases.ToDictionary(entry => entry.Name, entry => InjectOptionsContract.Run(entry.Case));

        IEnumerable<InjectionOmissionReason> Reasons(string name) => runs[name].Result.Omitted.Select(entry => entry.Reason);

        // Each ToolRiskClass as the maximum: the stricter, the more is omitted; Critical omits nothing.
        Assert.Equal(3, Reasons("risk-low").Count(reason => reason == InjectionOmissionReason.RiskClassExceeded));
        Assert.Equal(2, Reasons("risk-medium").Count(reason => reason == InjectionOmissionReason.RiskClassExceeded));
        Assert.Equal(1, Reasons("risk-high").Count(reason => reason == InjectionOmissionReason.RiskClassExceeded));
        Assert.Empty(runs["risk-critical"].Result.Omitted);
        Assert.Equal(3, runs["risk-critical"].Request.Limit);

        // The gate's and the model-authored omissions carry no detail, so no tool name leaves the provider.
        Assert.All(
            runs.Values.SelectMany(entry => entry.Result.Omitted)
                .Where(entry => entry.Reason is InjectionOmissionReason.ToolUnavailable or InjectionOmissionReason.RiskClassExceeded or InjectionOmissionReason.ModelAuthored),
            entry => Assert.Null(entry.Detail));

        Assert.Equal([InjectionOmissionReason.ToolUnavailable, InjectionOmissionReason.ToolUnavailable], Reasons("tool-unavailable"));
        Assert.Equal(
            [InjectionOmissionReason.RiskClassExceeded, InjectionOmissionReason.ToolUnavailable, InjectionOmissionReason.ToolUnavailable],
            Reasons("tool-check-before-risk"));
        Assert.Single(runs["no-tools-available"].Result.InjectedExperienceIds);
        Assert.Equal(2, runs["run-tools"].Result.InjectedExperienceIds.Count);
        Assert.Single(runs["run-tools-none-offered"].Result.InjectedExperienceIds);
        Assert.Equal([InjectionOmissionReason.ToolUnavailable], Reasons("blank-recorded-tool"));
        Assert.Equal([InjectionOmissionReason.RiskClassExceeded], Reasons("blank-recorded-tool-risk"));
        Assert.Equal([InjectionOmissionReason.ToolUnavailable], Reasons("no-approach-passes"));
        Assert.Equal(3, runs["no-approach-passes"].Result.InjectedExperienceIds.Count);
        Assert.Equal([InjectionOmissionReason.ToolUnavailable], Reasons("approach-cut-at-twenty"));

        // Limits: the gate takes no slot, the byte budget comes last, and the window is widened only for a gate.
        Assert.Equal(InjectionOmissionReason.OverRecordLimit, Reasons("gate-takes-no-slot").Last());
        Assert.Equal(InjectionOmissionReason.OverByteBudget, Reasons("gate-and-byte-budget").Last());
        Assert.Equal(6, runs["request-limit-gated-window"].Request.Limit);
        Assert.Single(runs["request-limit-gated-window"].Result.Omitted);
        Assert.Equal(InjectionOutcome.NothingToInject, runs["everything-gated"].Result.Outcome);
        Assert.Null(runs["everything-gated"].Block);
        Assert.Equal(InjectionOmissionReason.MeasuredHarmful, Reasons("gate-and-measured-harmful").First());
        Assert.Contains(InjectionOmissionReason.RiskClassExceeded, Reasons("gate-and-measured-harmful"));

        // The model-authored policy.
        Assert.Contains(HistoricalReferenceWriter.ModelAuthoredLine, runs["model-authored-include"].Block!.Text, StringComparison.Ordinal);
        Assert.False(runs["model-authored-include"].QueryExcludedModelAuthored);
        Assert.True(runs["model-authored-exclude"].QueryExcludedModelAuthored);
        Assert.Equal(2, runs["model-authored-exclude"].Ranked.Count);

        // The host's decision: after the gate, before the byte budget, told about authorship, keeping the slot.
        var host = runs["host-decision"];
        Assert.Equal(
            [InjectionOmissionReason.RiskClassExceeded, InjectionOmissionReason.HostDenied, InjectionOmissionReason.HostDenied, InjectionOmissionReason.OverByteBudget],
            host.Result.Omitted.Select(entry => entry.Reason));
        Assert.Contains(host.Decisions, entry => entry.ModelAuthored);
        Assert.Contains(host.Result.Omitted, entry => entry.Detail == "risk policy: no retries");
        Assert.Contains(host.Result.Omitted, entry => entry.Detail == "The host injection decision denied the record.");

        // Failure detail and role.
        Assert.Contains("\"System.TimeoutException", runs["failure-detail-excerpt"].Block!.Text, StringComparison.Ordinal);
        Assert.Contains("→ failed\n", runs["failure-detail-none"].Block!.Text, StringComparison.Ordinal);
        Assert.Contains("retry_refund(delay=30)", runs["approach-arguments"].Block!.Text, StringComparison.Ordinal);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, runs["message-role-system-verbose"].Block!.Role);
    }
}
