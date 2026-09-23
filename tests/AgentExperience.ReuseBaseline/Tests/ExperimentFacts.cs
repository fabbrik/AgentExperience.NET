using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The pre-registered arms, plus the one deliberately faulted run the failure-rendering golden is
/// taken from, run once each for the whole test assembly.
/// </summary>
/// <remarks>
/// All of them are deterministic, so running one once and asserting many things about the same
/// result is the same as running it many times -- and the determinism itself is asserted separately,
/// by running the reference arm twice and comparing the bytes.
/// </remarks>
internal static class ExperimentFacts
{
    private static readonly Lazy<Task<ExperimentResult>> ReferenceRun =
        new(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions { Arm = ReuseBaselineArms.Reference }));

    private static readonly Lazy<Task<ExperimentResult>> NegativeControlRun =
        new(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions { Arm = ReuseBaselineArms.NegativeControl }));

    private static readonly Lazy<Task<ExperimentResult>> WrongStrategyRun =
        new(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions { Arm = ReuseBaselineArms.WrongStrategy }));

    private static readonly Lazy<Task<ExperimentResult>> FaultedRun =
        new(() => ReuseBaselineExperiment.RunAsync(FaultedOptions()));

    /// <summary>The reference experiment.</summary>
    public static Task<ExperimentResult> ReferenceAsync() => ReferenceRun.Value;

    /// <summary>The negative control.</summary>
    public static Task<ExperimentResult> NegativeControlAsync() => NegativeControlRun.Value;

    /// <summary>The wrong-strategy arm.</summary>
    public static Task<ExperimentResult> WrongStrategyAsync() => WrongStrategyRun.Value;

    /// <summary>
    /// The reference arm with five of its six memory-enabled trials deliberately faulted, so that
    /// the report's rendering of errors, timeouts, retrieval failures, missing-value placeholders
    /// and an undefined statistic is golden-filed rather than only substring-checked.
    /// </summary>
    public static Task<ExperimentResult> FaultedAsync() => FaultedRun.Value;

    /// <summary>
    /// How the faulted run is configured. The memory-enabled trials are indices 1, 3, 5, 7, 9 and
    /// 11; one keeps a retrieval failure (it still completes, so it still has values), four are
    /// killed. That leaves the memory-enabled condition with a single observation -- the retrieval
    /// failure, which completed with nothing injected -- and therefore an undefined standard
    /// deviation.
    /// </summary>
    public static ExperimentOptions FaultedOptions() => new()
    {
        Arm = ReuseBaselineArms.Reference,
        TrialTimeout = TimeSpan.FromMilliseconds(250),
        FaultAt = index => index switch
        {
            1 => new TrialFault(TrialFaultKind.RetrievalFailure),
            3 => new TrialFault(TrialFaultKind.Throw),
            5 => new TrialFault(TrialFaultKind.Timeout),
            7 => new TrialFault(TrialFaultKind.Throw),
            9 => new TrialFault(TrialFaultKind.Timeout),
            11 => new TrialFault(TrialFaultKind.Throw),
            _ => null,
        },
    };

    /// <summary>Builds one synthetic trial for the gate tests. No agent, no store, no run: numbers only.</summary>
    /// <param name="index">The trial index.</param>
    /// <param name="condition">The condition.</param>
    /// <param name="failedAttempts">The primary metric, or <see langword="null"/> when the trial has no value for it.</param>
    /// <param name="verified">The verified-success guardrail, or <see langword="null"/>.</param>
    /// <param name="denied">The unauthorized-execution guardrail, or <see langword="null"/> when the trial has no value for it.</param>
    /// <param name="status">How the trial ended.</param>
    public static TrialRecord Synthetic(
        int index,
        TrialCondition condition,
        int? failedAttempts,
        bool? verified = true,
        int? denied = 0,
        TrialStatus status = TrialStatus.Completed) =>
        new(
            index,
            condition,
            "synthetic-task",
            TrialIdentities.Derive("synthetic", index, "run", 0),
            TrialIdentities.Derive("synthetic", index, "closed-round", 0),
            status,
            new TrialMetrics(failedAttempts, verified, denied, failedAttempts, 1d),
            status == TrialStatus.Completed ? null : status.ToString(),
            null,
            [],
            false,
            [],
            null,
            "synthetic");

    /// <summary>The checked-in pre-registration, read from disk.</summary>
    public static Preregistration Design() => PreregistrationSource.CheckedIn.Read().Design;

    /// <summary>
    /// The reference arm's report, rendered against a pre-registration identical to the checked-in
    /// one except that its <c>amendments</c> array is empty.
    /// </summary>
    /// <remarks>
    /// The checked-in file has been amended, so the "never amended" branch of the header is
    /// unreachable through any real arm. It still has to be asserted: a reader must be able to tell
    /// "never amended" from "amendments not shown", and that is only true if the unamended case
    /// prints something positive.
    /// </remarks>
    public static async Task<string> RenderWithNoAmendmentsAsync()
    {
        var text = await File.ReadAllTextAsync(PreregistrationSource.DefaultPath());
        var start = text.IndexOf("  \"amendments\": [", StringComparison.Ordinal);

        if (start < 0)
        {
            throw new InvalidOperationException("The checked-in pre-registration declares no amendments array to empty.");
        }

        var source = new FixedSource(System.Text.Encoding.UTF8.GetBytes(text[..start] + "  \"amendments\": []\n}\n"));

        return ReuseBaselineReport.RenderDeterministic(await ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference,
            Preregistration = source,
        }));
    }

    /// <summary>A source over bytes the test supplies, re-read on every call like the shipping one.</summary>
    private sealed class FixedSource(byte[] bytes) : PreregistrationSource
    {
        public override string Description => "preregistration.json (test source)";

        public override byte[] ReadBytes() => bytes;
    }
}
