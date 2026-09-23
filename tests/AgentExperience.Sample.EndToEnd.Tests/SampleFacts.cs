using AgentExperience.Abstractions;
using AgentExperience.MicrosoftAgentFramework;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Runs the sample in its default mode and hands the test back everything it wrote, not only what
/// it said it wrote.
/// </summary>
/// <remarks>
/// The sample's stores are its own demonstration doubles, so a test can read them the way an
/// operator would read the real ones: the Experience Record the store holds, the run capture holds,
/// the ledger row the feedback service wrote, and the session state bag the capture middleware
/// populated. Asserting those instead of the printed lines is what stops the sample from passing by
/// printing a fixed script.
/// </remarks>
internal static class SampleFacts
{
    /// <summary>Runs the seven stages with the in-memory doubles: no Docker, no database, no network.</summary>
    public static Task<SampleExecution> RunAsync(CancellationToken cancellationToken = default) =>
        SampleHost.ExecuteAsync(TextWriter.Null, postgresConnectionString: null, cancellationToken);

    /// <summary>The Experience Record the store holds, read back from the store rather than from the transcript.</summary>
    public static ExperienceRecord PersistedRecord(this SampleExecution execution) =>
        execution.Run.PersistedRecord ?? throw new InvalidOperationException("The sample did not persist a record.");

    /// <summary>
    /// The record the store holds once the whole run is over, which is after the reuse feedback was
    /// submitted. Comparing it with <see cref="PersistedRecord"/> -- the same record as stage 4 read
    /// it, before stage 7 -- is what "nothing moved" means as a fact rather than a sentence.
    /// </summary>
    public static ExperienceRecord RecordAfterFeedback(this SampleExecution execution)
    {
        var records = (execution.Records ?? throw new InvalidOperationException("The sample was not composed with the in-memory store.")).Snapshot();
        return Assert.Single(records);
    }

    /// <summary>Run A exactly as capture holds it: both attempts, their tool calls, and their arguments.</summary>
    public static ExperienceRun CapturedRun(this SampleExecution execution) =>
        execution.Run.CapturedRunA ?? throw new InvalidOperationException("The sample did not capture run A.");

    /// <summary>The Historical Reference block run B's model was handed, verbatim.</summary>
    public static string InjectedBlock(this SampleExecution execution) =>
        execution.Run.InjectedBlock ?? throw new InvalidOperationException("The sample injected no Historical Reference.");

    /// <summary>The one row the reuse-feedback ledger holds after the run.</summary>
    public static RecordedExperienceReuseFeedback LedgerRow(this SampleExecution execution)
    {
        var rows = (execution.Feedback ?? throw new InvalidOperationException("The sample was not composed with the in-memory ledger.")).Rows;
        return Assert.Single(rows).Value;
    }

    /// <summary>
    /// The run ID the capture middleware wrote into run B's session state, read out of the state bag
    /// by the test itself. This is the value frozen rule 8 is about: a host must take run B's run ID
    /// from here and never from anything the agent produced.
    /// </summary>
    public static Guid RunIdFromSessionState(this SampleExecution execution)
    {
        var session = execution.Run.RunBSession ?? throw new InvalidOperationException("The sample created no session for run B.");
        Assert.True(
            session.StateBag.TryGetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, out var text),
            $"Run B's session carries no '{ExperienceCaptureAgentBuilderExtensions.RunIdStateKey}'.");
        return Guid.Parse(text!);
    }
}
