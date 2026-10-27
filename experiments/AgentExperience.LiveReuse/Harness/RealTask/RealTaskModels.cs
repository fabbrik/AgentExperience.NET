using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// One learning run or evaluation trial of the real-task experiment, as recorded. No prompt, no message text, no key.
/// <c>ToolCalls</c> counts calls up to and including the first green <c>run_tests</c> when the trial verified; <c>Score</c> is
/// the primary metric: <c>ToolCalls</c> when the trial verified, otherwise the cap.
/// </summary>
public sealed record RealTaskRun(
    int Sequence,
    string Phase,
    int Instance,
    string Service,
    string Cluster,
    string Condition,
    int Repeat,
    RunStatus Status,
    string? Classification,
    bool? Verified,
    int ToolCalls,
    int TestRuns,
    int Score,
    IReadOnlyList<string> WrittenPaths,
    int ReadsBeforeFirstWrite,
    bool? FirstWriteWasClusterFix,
    int UnauthorizedRequests,
    bool BlockSeen,
    IReadOnlyList<string> BlockPaths,
    IReadOnlyList<string> BlockRecords,
    UsageTotals Usage,
    double LatencyMilliseconds,
    IReadOnlyList<string> StoredFixPaths)
{
    /// <summary>Whether the block named the file this cluster's fix lives in.</summary>
    public bool BlockNamedClusterFix { get; init; }

    /// <summary>The 1-based rank of the sibling's learning record in the block, or <see langword="null"/>.</summary>
    public int? SiblingRank { get; init; }
}

/// <summary>A service left out of every comparison, and why.</summary>
public sealed record RealTaskExclusion(string Service, string Reason);

/// <summary>What the real-task experiment concluded, by the rule the pre-registration states.</summary>
public enum RealTaskConclusion
{
    RealTaskBenefitAttributableToContent,
    RealTaskBenefitNotAttributableToContent,
    RealTaskNoDemonstratedBenefit,
    RealTaskInconclusive,
    RealTaskNotEvaluated,
}

/// <summary>One comparison: the service-level gate and the trial-level reading that is never gated.</summary>
public sealed record RealTaskComparison(
    string Treatment,
    string Control,
    int Services,
    int ExcludedServices,
    double? TreatmentMeanToolCalls,
    double? ControlMeanToolCalls,
    double? TreatmentSuccessRate,
    double? ControlSuccessRate,
    int TreatmentRefusedWrites,
    int ControlRefusedWrites,
    SignTestResult? ServiceSignTest,
    SignTestResult? TrialSignTest,
    IReadOnlyList<GateTerm> Terms,
    ComparisonVerdict Verdict);

/// <summary>Everything a real-task run produced.</summary>
public sealed record RealTaskResult(
    RunDescriptor Descriptor,
    RealTaskDesign Design,
    string TaskSetDigest,
    LiveBudget Budget,
    DateTimeOffset StartedAt,
    IReadOnlyList<RealTaskRun> Learning,
    IReadOnlyList<RealTaskRun> Trials,
    bool Complete,
    string? StopReason,
    IReadOnlyList<RealTaskExclusion> Excluded,
    RealTaskComparison Reference,
    RealTaskComparison Content,
    RealTaskComparison MismatchedControl,
    RealTaskConclusion Conclusion,
    IReadOnlyList<string> ModelIds,
    int CallsWithoutUsage,
    UsageTotals Total)
{
    /// <summary>How many runs of the same provider and model the ledger held when this one started.</summary>
    public int? EarlierLedgerEntries { get; init; }
}

/// <summary>How to run the real-task experiment.</summary>
public sealed class RealTaskOptions
{
    /// <summary>The model. Any <see cref="IChatClient"/>: a provider's, or a scripted one offline.</summary>
    public required IChatClient Model { get; init; }

    /// <summary>
    /// The learning phase's model in a cross-model run (amendment 7), or <see langword="null"/> when <see cref="Model"/>
    /// does both phases. Learning runs call only this; trials call only <see cref="Model"/>. Both count against one budget.
    /// </summary>
    public IChatClient? LearningModel { get; init; }

    /// <summary>What the model is, for the report.</summary>
    public required RunDescriptor Descriptor { get; init; }

    /// <summary>The design: the embedded pre-registration in every live run.</summary>
    public RealTaskDesign Design { get; init; } = RealTaskDesign.ReadEmbedded();

    /// <summary>The task set; checked against <see cref="Design"/>.</summary>
    public RealTaskSet TaskSet { get; init; } = RealTaskSet.Current;

    /// <summary>The caps. <see langword="null"/> takes the pre-registered defaults.</summary>
    public LiveBudget? Budget { get; init; }

    /// <summary>Times latency and the report date.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The least time between two model calls.</summary>
    public TimeSpan MinimumCallInterval { get; init; } = TimeSpan.Zero;

    /// <summary>The longest one model call may take before its trial is recorded as errored.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Progress lines, one per run. Never carries a prompt, a message or a key.</summary>
    public TextWriter? Progress { get; init; }

    /// <summary>Called as each learning run and trial is recorded.</summary>
    public Action<RealTaskRun>? OnRunRecorded { get; init; }

    /// <summary>Overrides the pre-registered retry delays (the tests use zero).</summary>
    public bool NoRetryDelay { get; init; }
}
