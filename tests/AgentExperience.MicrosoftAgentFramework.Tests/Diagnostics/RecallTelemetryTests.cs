using System.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;

/// <summary>
/// Recall at the moment of failure emits one <c>recall</c> span and one count per failed call it looks at, with its
/// outcome, how the records matched and how many there were, and nothing of the hint, the signature or the tool.
/// </summary>
[Collection(TelemetryCollection.Name)]
public class RecallTelemetryTests
{
    [Fact]
    public async Task A_recall_reports_its_outcome_match_and_record_count_and_nothing_of_the_hint()
    {
        var host = new RecallAtFailureTests.Host();
        await using var services = host.Build(options => options.Recall = new ExperienceRecallOptions());
        var agent = host.Agent(services);
        await agent.RunAsync("Repair the failing alpha build quickly");
        host.Reset();

        using var probe = TelemetryProbe.Start();
        await agent.RunAsync("Payments pipeline broke overnight");

        Assert.Equal(ExperienceRecallOutcome.Hinted, Assert.Single(host.Recalls).Outcome);
        var span = Assert.Single(probe.LibraryActivities, activity => activity.OperationName == "agentexperience.recall");
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("recall", span.GetTagItem("agentexperience.operation"));
        Assert.Equal(nameof(ExperienceRecallOutcome.Hinted), span.GetTagItem("agentexperience.outcome"));
        Assert.Equal(nameof(AgentExperience.Abstractions.ExperienceSignatureMatch.Exact), span.GetTagItem("agentexperience.signature.match"));
        Assert.Equal(1, span.GetTagItem("agentexperience.record_count"));

        var counted = Assert.Single(probe.For("agentexperience.operation.count", "recall"));
        Assert.Equal(nameof(ExperienceRecallOutcome.Hinted), counted.Tags["outcome"]);
        Assert.Equal(false, counted.Tags["nested"]);
        Assert.Empty(probe.For("agentexperience.operation.failures", "recall"));

        // Nothing of the hint, the fix it names, the tool or the error reaches a span or a measurement.
        foreach (var value in probe.EverySpanTagValue.Concat(probe.EveryMeasurementTagValue))
        {
            Assert.DoesNotContain("pin-version", value, StringComparison.Ordinal);
            Assert.DoesNotContain("run_tests", value, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperationException", value, StringComparison.Ordinal);
            Assert.DoesNotContain("EARLIER FIX", value, StringComparison.Ordinal);
        }
    }
}
