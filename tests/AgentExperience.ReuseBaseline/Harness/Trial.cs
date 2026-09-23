using System.Globalization;

namespace AgentExperience.ReuseBaseline.Harness;

/// <summary>Which experimental condition a trial ran under.</summary>
public enum TrialCondition
{
    /// <summary>Stored experience was retrieved and injected into the trial's invocations.</summary>
    MemoryEnabled,

    /// <summary>No context provider was attached, so nothing was retrieved and nothing injected.</summary>
    MemoryDisabled,
}

/// <summary>
/// How a trial ended. Every value here appears in the report: a trial is never dropped, because
/// dropping the trials that went wrong is the cheapest way to make a measurement flattering.
/// </summary>
public enum TrialStatus
{
    /// <summary>The trial ran to the end and its metrics were read back out of the captured run.</summary>
    Completed,

    /// <summary>The trial threw. Whatever metrics it had a value for are kept; the rest are undefined.</summary>
    Errored,

    /// <summary>The trial exceeded its own deadline. Deliberately distinct from <see cref="Errored"/>.</summary>
    TimedOut,
}

/// <summary>
/// What one trial measured. Every value is <see langword="null"/> when the trial has no value for it,
/// never a plausible-looking zero.
/// </summary>
/// <param name="FailedAttempts">
/// <c>run.Attempts.Count(a =&gt; a.Error is not null)</c>, read back out of the capture service's own
/// snapshot. The primary metric.
/// </param>
/// <param name="VerifiedSuccess">
/// Whether <c>run.Outcome?.Status == TaskVerificationStatus.Verified</c> for this trial, where the
/// outcome is the verification aggregator's verdict over the trial's own evidence and its own closed
/// round. A guardrail, reported as a rate per condition.
/// </param>
/// <param name="UnauthorizedToolExecutions">
/// How many invocations the trial's <c>ApprovalRequiredAIFunction</c> boundary denied. The library
/// has no such concept, so the harness owns this measure and instruments the boundary itself.
/// </param>
/// <param name="ToolCalls"><c>run.Attempts.Sum(a =&gt; a.ToolCalls.Count)</c>. Secondary.</param>
/// <param name="ElapsedMilliseconds">
/// A <see cref="System.Diagnostics.Stopwatch"/> around the trial. Secondary, and excluded from the
/// gate by the pre-registration. Never <c>TimeProvider</c>-derived: under a stepping fixture clock
/// every captured duration is a function of how many clock reads happened rather than of time.
/// </param>
public sealed record TrialMetrics(
    int? FailedAttempts,
    bool? VerifiedSuccess,
    int? UnauthorizedToolExecutions,
    int? ToolCalls,
    double? ElapsedMilliseconds);

/// <summary>
/// One trial, retained whatever happened to it: which condition it ran under, which task it ran,
/// what it measured, and how it failed when it did.
/// </summary>
/// <param name="Index">The trial's position in the plan. The condition and the task are derived from it.</param>
/// <param name="Condition">The condition the index assigned. Never rewritten after the fact.</param>
/// <param name="TaskId">The evaluation task this trial ran.</param>
/// <param name="RunId">The trial's own Experience Run identifier.</param>
/// <param name="VerificationRoundId">The trial's own closed verification round.</param>
/// <param name="Status">How the trial ended.</param>
/// <param name="Metrics">What it measured.</param>
/// <param name="FailureClassification">
/// A content-free classification when the trial did not complete -- an exception type name, or the
/// deadline that was exceeded. <see langword="null"/> for a completed trial.
/// </param>
/// <param name="RetrievalFailure">
/// The injection outcome, when a memory-enabled trial's retrieval did not complete. The trial keeps
/// its condition: a memory-enabled trial whose retrieval failed is a memory-enabled trial that got
/// nothing, and silently recounting it as memory-disabled would hide a real difference between the
/// arms.
/// </param>
/// <param name="ExposedExperienceIds">The records injected into this trial, if any.</param>
/// <param name="SawInjectedBlock">
/// Whether a Historical Reference block reached the agent's own context in this trial, as the agent
/// reports it rather than as the injection result claims it. A memory-disabled trial for which this
/// is <see langword="true"/> is not a memory-disabled trial.
/// </param>
/// <param name="StrategiesReadFromContext">
/// The strategies the agent read out of the injected block, in the order the block named them. This
/// is the only route by which anything about a stored record may reach the agent, so it is what the
/// harness checks the memory-enabled arm's advantage against: a trial that resolved its task without
/// reading a strategy out of the block was told the answer some other way.
/// </param>
/// <param name="FeedbackId">The reuse-feedback submission this trial made, or <see langword="null"/> when it made none.</param>
/// <param name="FeedbackOutcome">What the ledger did with the submission, or why none was made.</param>
public sealed record TrialRecord(
    int Index,
    TrialCondition Condition,
    string TaskId,
    Guid RunId,
    Guid VerificationRoundId,
    TrialStatus Status,
    TrialMetrics Metrics,
    string? FailureClassification,
    string? RetrievalFailure,
    IReadOnlyList<Guid> ExposedExperienceIds,
    bool SawInjectedBlock,
    IReadOnlyList<string> StrategiesReadFromContext,
    Guid? FeedbackId,
    string FeedbackOutcome);

/// <summary>
/// Which condition and which task a trial index runs, derived from the index and the
/// pre-registration alone.
/// </summary>
/// <remarks>
/// Frozen rule 8: the assignment is derived, not chosen. There is no stored list of assignments to
/// disagree with this, and the tests assert the assignment from the index rather than from anything
/// the harness recorded -- so "no human picked which tasks ran under which condition" is checkable
/// by reading four lines.
/// </remarks>
public static class TrialPlan
{
    /// <summary>
    /// The condition trial <paramref name="index"/> runs under: the pre-registration's starting
    /// condition on even indices, the other one on odd indices.
    /// </summary>
    /// <param name="index">The trial's position in the plan.</param>
    /// <param name="startingCondition">The condition the pre-registration fixed for index 0.</param>
    public static TrialCondition ConditionFor(int index, TrialCondition startingCondition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return index % 2 == 0
            ? startingCondition
            : startingCondition == TrialCondition.MemoryEnabled ? TrialCondition.MemoryDisabled : TrialCondition.MemoryEnabled;
    }

    /// <summary>
    /// The evaluation task trial <paramref name="index"/> runs: consecutive pairs share a task, so
    /// each evaluation task is run exactly once under each condition.
    /// </summary>
    /// <remarks>
    /// There is deliberately no modulo here. An earlier version wrapped with
    /// <c>index / 2 % Count</c>, which meant a seventh evaluation task would never run and removing
    /// one would double-weight the first -- "a subset chosen after the fact", arriving by accident.
    /// An index past the end of the set is a refusal.
    /// </remarks>
    /// <param name="index">The trial's position in the plan.</param>
    /// <param name="evaluationTasks">The pre-registered evaluation set, in its declared order.</param>
    /// <exception cref="TaskSetException">There are no evaluation tasks, or the index names none of them.</exception>
    public static ReuseBaselineTask TaskFor(int index, IReadOnlyList<ReuseBaselineTask> evaluationTasks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(evaluationTasks);

        if (evaluationTasks.Count == 0)
        {
            throw new TaskSetException("There are no evaluation tasks to assign.");
        }

        if (index / 2 >= evaluationTasks.Count)
        {
            throw new TaskSetException(string.Format(
                CultureInfo.InvariantCulture,
                "Trial index {0} maps to evaluation task {1} and the set declares {2}. The assignment does not wrap: "
                    + "wrapping would run some tasks twice and others never, which is a subset nobody declared.",
                index,
                index / 2,
                evaluationTasks.Count));
        }

        return evaluationTasks[index / 2];
    }

    /// <summary>One trial of the built plan: its index, its condition, and the task it runs.</summary>
    /// <param name="Index">The trial's position in the plan.</param>
    /// <param name="Condition">The condition the index assigned.</param>
    /// <param name="Task">The evaluation task the index assigned.</param>
    public sealed record PlannedTrial(int Index, TrialCondition Condition, ReuseBaselineTask Task);

    /// <summary>
    /// Builds the whole plan from the evaluation set, then checks its length against the
    /// pre-registered trial count.
    /// </summary>
    /// <remarks>
    /// The plan is built from the task set -- two trials per evaluation task, one under each
    /// condition -- and only then compared against the pre-registration. That ordering is the point:
    /// comparing the declared count against itself, which an earlier version did, is a check that
    /// cannot fail.
    /// </remarks>
    /// <param name="preregistration">The design.</param>
    /// <param name="evaluationTasks">The pre-registered evaluation set, in its declared order.</param>
    /// <exception cref="PreregistrationException">The built plan's length is not the pre-registered trial count.</exception>
    public static IReadOnlyList<PlannedTrial> Build(Preregistration preregistration, IReadOnlyList<ReuseBaselineTask> evaluationTasks)
    {
        ArgumentNullException.ThrowIfNull(preregistration);
        ArgumentNullException.ThrowIfNull(evaluationTasks);

        var plan = new List<PlannedTrial>(evaluationTasks.Count * 2);
        for (var index = 0; index < evaluationTasks.Count * 2; index++)
        {
            plan.Add(new PlannedTrial(
                index,
                ConditionFor(index, preregistration.StartingCondition),
                TaskFor(index, evaluationTasks)));
        }

        RequireDeclaredTrialCount(plan.Count, preregistration);

        return plan;
    }

    /// <summary>
    /// Refuses a plan whose length is not the pre-registered trial count.
    /// </summary>
    /// <param name="plannedTrials">How many trials the built plan contains.</param>
    /// <param name="preregistration">The design.</param>
    /// <exception cref="PreregistrationException">The two disagree.</exception>
    public static void RequireDeclaredTrialCount(int plannedTrials, Preregistration preregistration)
    {
        ArgumentNullException.ThrowIfNull(preregistration);

        if (plannedTrials != preregistration.TrialCount)
        {
            throw new PreregistrationException(string.Format(
                CultureInfo.InvariantCulture,
                "The plan built from the evaluation set is {0} trial(s) -- two per evaluation task -- and the pre-registration "
                    + "fixed {1}. trialCount must equal 2 x evaluationTasks.length. A trial count that is decided at run time "
                    + "is not a pre-registered trial count.",
                plannedTrials,
                preregistration.TrialCount));
        }
    }
}
