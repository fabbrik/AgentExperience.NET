using System.Diagnostics;
using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Sanitization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.ReuseBaseline.Harness;
using AgentExperience.Sample.EndToEnd.Doubles;
using AgentExperience.Sample.EndToEnd.Fixtures;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.ReuseBaseline.Experiment;

/// <summary>
/// The harness observed something that would make every number it reports meaningless, and stopped
/// rather than reporting them.
/// </summary>
/// <remarks>
/// There are two of these, and both are about attribution rather than about a trial going wrong. A
/// trial that errors or times out is <em>recorded</em>. A trial whose approval boundary did not hold,
/// or whose cost cannot be accounted for by the strategies the agent read out of its own context, is
/// not a trial this harness knows how to report -- the second one is precisely how a harness that
/// planted the answer would look.
/// </remarks>
/// <param name="message">What was observed, and why it invalidates the measurement.</param>
public sealed class HarnessIntegrityException(string message) : Exception(message);

/// <summary>The kinds of trial failure the harness can be asked to inject, so that its handling of them is reachable.</summary>
public enum TrialFaultKind
{
    /// <summary>The trial throws part-way through.</summary>
    Throw,

    /// <summary>
    /// The trial hangs: it awaits work that never completes on its own, so the only thing that can
    /// end it is its deadline. It cannot finish first, whatever the machine's load.
    /// </summary>
    Timeout,

    /// <summary>The candidate source throws, so retrieval fails and nothing is injected.</summary>
    RetrievalFailure,

    /// <summary>
    /// The resolving attempt's evidence is filed in the round the host never closes, so the
    /// verification aggregator cannot verify a run the deterministic task check says succeeded. The
    /// two readings then disagree, which is the branch the report's NOTES section exists to carry.
    /// </summary>
    MisfiledEvidence,
}

/// <summary>A fault the harness injects into one trial. Used by the tests only; never by the reference experiment.</summary>
/// <param name="Kind">What goes wrong.</param>
public sealed record TrialFault(TrialFaultKind Kind);

/// <summary>One arm of the experiment: an identity, a purpose, and the task set it runs.</summary>
/// <param name="Id">The arm's identity, which also seeds every identifier its trials use.</param>
/// <param name="Purpose">What the arm is for, printed in the report.</param>
/// <param name="TaskSet">The versioned task set, whose learning and evaluation sets must be disjoint.</param>
public sealed record ExperimentArm(string Id, string Purpose, ReuseBaselineTaskSet TaskSet);

/// <summary>How one run of the harness is configured.</summary>
public sealed record ExperimentOptions
{
    /// <summary>The arm to run.</summary>
    public required ExperimentArm Arm { get; init; }

    /// <summary>Where the pre-registration is read from, and re-read from when the report renders.</summary>
    public PreregistrationSource Preregistration { get; init; } = PreregistrationSource.CheckedIn;

    /// <summary>
    /// The per-trial deadline. Frozen intent AD-F's binding: a memory-enabled trial must not be
    /// allowed to hang, and the bound is explicit rather than inherited from a test runner.
    /// </summary>
    public TimeSpan TrialTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The clock the per-trial deadline runs on. <see cref="TimeProvider.System"/> in every
    /// pre-registered arm, so a hung trial is ended by real time passing.
    /// </summary>
    /// <remarks>
    /// A test that golden-files which trials timed out must not let that depend on how fast the
    /// machine is: a 250 ms deadline on the system clock is a race every clean trial can lose under
    /// CPU contention. Such a test supplies a clock that only moves when it is told to, so a clean
    /// trial's deadline can never elapse, and moves it past the deadline from
    /// <see cref="OnTrialHanging"/>, so a <see cref="TrialFaultKind.Timeout"/> trial's always does.
    /// </remarks>
    public TimeProvider DeadlineClock { get; init; } = TimeProvider.System;

    /// <summary>The retrieval timeout, set explicitly for the same reason.</summary>
    public TimeSpan RetrievalTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How many attempts one trial may make before it gives up.</summary>
    public int MaxAttemptsPerTrial { get; init; } = 8;

    /// <summary>
    /// Wraps the reflector the learning phase uses. The reference experiment leaves this at
    /// <see langword="null"/>, which is <see cref="WorkingApproachReflector"/> over the default.
    /// </summary>
    public Func<IExperienceReflector, IExperienceReflector>? DecorateReflector { get; init; }

    /// <summary>The fault to inject into a trial index, if any. Always <see langword="null"/> in the reference experiment.</summary>
    public Func<int, TrialFault?>? FaultAt { get; init; }

    /// <summary>
    /// Hands the trial the guarded tool <em>unwrapped</em>, so its body can actually run. Test-only,
    /// and always <see langword="null"/> in every pre-registered arm.
    /// </summary>
    /// <remarks>
    /// It exists so that the harness's own "the tool body never ran" guard is reachable. A guard that
    /// has never been seen to fire is not a guard, and the assertion the report makes about
    /// <c>unauthorized_tool_executions</c> -- that a denial means the call did not happen -- rests on
    /// it entirely.
    /// </remarks>
    public Func<int, bool>? UnguardTheGuardedToolAt { get; init; }

    /// <summary>
    /// Called with the trial index once a <see cref="TrialFaultKind.Timeout"/> trial has started to
    /// hang, and before anything else happens to it. A test driving <see cref="DeadlineClock"/> by
    /// hand advances it past <see cref="TrialTimeout"/> here; with the system clock it is not needed.
    /// </summary>
    public Action<int>? OnTrialHanging { get; init; }

    /// <summary>Called as each learning record is produced. Used by the tests to prove nothing was written before a refusal.</summary>
    public Action<LearnedRecord>? OnRecordLearned { get; init; }

    /// <summary>Called as each trial is recorded. Used by the tests to prove no trial ran before a refusal.</summary>
    public Action<TrialRecord>? OnTrialRecorded { get; init; }
}

/// <summary>One Experience Record the learning phase produced.</summary>
/// <param name="TaskId">The learning task it came from.</param>
/// <param name="ExperienceId">The record.</param>
/// <param name="Status">Its lifecycle status.</param>
/// <param name="ReuseConfidence">Its reuse confidence, which must clear retrieval's floor to be reachable at all.</param>
/// <param name="FailedAttempts">How many attempts the learning run failed before it resolved its task.</param>
/// <param name="WorkingStrategy">The strategy the reflector read out of the run's final successful attempt.</param>
/// <param name="LessonNamesGuardedTool">
/// Whether the stored lesson names the guarded tool. It is read back out of the store, and it is what
/// decides whether the <c>unauthorized_tool_executions</c> gate term could have failed in this arm at
/// all: nothing else in the harness can make the agent ask for that tool.
/// </param>
public sealed record LearnedRecord(
    string TaskId,
    Guid ExperienceId,
    ExperienceStatus Status,
    double ReuseConfidence,
    int FailedAttempts,
    string? WorkingStrategy,
    bool LessonNamesGuardedTool);

/// <summary>Everything one run of the harness produced.</summary>
/// <param name="Arm">The arm that ran.</param>
/// <param name="Preregistration">The design, as it stood when the trials started, with the digest of the exact bytes.</param>
/// <param name="Source">Where the pre-registration was read from, so the report can re-read it.</param>
/// <param name="Learned">The records the learning phase produced.</param>
/// <param name="Trials">Every trial, in plan order. None is ever dropped.</param>
/// <param name="Gate">The one gate evaluation.</param>
/// <param name="LedgerRows">
/// Every row the reuse-feedback ledger holds at the end of the run, read back out of the ledger and
/// ordered by feedback identity. The report's feedback counts are derived from these rather than from
/// what the harness believes it submitted.
/// </param>
/// <param name="CheckDisagreements">Trials where the IEvaluator task check and the verification aggregator disagreed. Reported rather than reconciled.</param>
/// <param name="MaxAttemptsPerTrial">
/// How many attempts one trial was permitted. The report needs it to say whether the verified-success
/// guardrail could have failed in this arm at all.
/// </param>
public sealed record ExperimentResult(
    ExperimentArm Arm,
    PreregistrationSnapshot Preregistration,
    PreregistrationSource Source,
    IReadOnlyList<LearnedRecord> Learned,
    IReadOnlyList<TrialRecord> Trials,
    GateResult Gate,
    IReadOnlyList<RecordedExperienceReuseFeedback> LedgerRows,
    IReadOnlyList<int> CheckDisagreements,
    int MaxAttemptsPerTrial)
{
    /// <summary>How many trials the ledger holds a row for. Counted from the ledger, not from the harness's own tally.</summary>
    public int FeedbackSubmissions => LedgerRows.Count;

    /// <summary>
    /// How many rows the ledger recorded a comparative machine attribution for. Zero, by design:
    /// frozen rule 11 forbids constructing one from a scripted run.
    /// </summary>
    /// <remarks>
    /// Counted from the stored rows rather than compared against a literal, so a future change that
    /// started submitting comparative results would move this number and the report with it. The one
    /// case it cannot see is a comparative result the ledger <em>degraded</em> to no attribution,
    /// which leaves no evaluator identity behind; that the harness constructs none at all is asserted
    /// separately, against the submission path.
    /// </remarks>
    public int ComparativeResultsSubmitted => LedgerRows.Count(row =>
        row.AttributionSource == ReuseAttributionSource.ComparativeEvaluation || row.EvaluatorId is not null);

    /// <summary>How many rows the ledger recorded a human assessment for. Zero, by design, and counted the same way.</summary>
    public int HumanAssessmentsSubmitted => LedgerRows.Count(row =>
        row.AttributionSource == ReuseAttributionSource.HumanAssessment || row.AssessmentId is not null);
}

/// <summary>
/// The measurement harness: a learning phase that produces Experience Records, then a balanced,
/// pre-registered set of trials over held-out evaluation tasks, then one evaluation of one gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it measures.</b> The harness, not a model. There is no model credential in this
/// repository and every <see cref="IChatClient"/> in it is a fake, so the magnitude of any
/// difference between the conditions is a property of <see cref="PolicyChatClient"/> and the task
/// set. What is genuinely measured is mechanical: whether an injected Historical Reference reaches
/// the agent's context and changes the action it takes, whether the authorization boundary still
/// holds when it does, and whether the gate says no when it should.
/// </para>
/// <para>
/// <b>What it takes from the 4.2 sample.</b> The composition-root shape -- a fresh
/// <see cref="ServiceCollection"/> and provider per trial, so N trials in one process is already a
/// supported thing -- the three in-memory port doubles, the attempt-level tool recorder, and the
/// discipline of reading every reported fact back out of the loop's own state rather than
/// restating what was asked for.
/// </para>
/// <para>
/// <b>What it deliberately does not take from it.</b> <c>SteppingTimeProvider</c> advances on every
/// clock <em>read</em>, so a <c>TimeProvider</c>-derived duration is a function of read count rather
/// than of time; elapsed time here is a <see cref="Stopwatch"/> and the fixture clock is frozen.
/// <c>DeterministicIds</c> restarts per container, and the harness builds one per trial, so its
/// identifiers would collide across trials; <see cref="TrialIdentities"/> derives from the trial
/// index instead. And <c>SampleRun</c> throws on any deviation, which is the opposite of what this
/// story needs: a trial that errors or times out is <em>recorded</em>, because a harness that
/// discards its awkward trials is not measuring anything.
/// </para>
/// </remarks>
public static class ReuseBaselineExperiment
{
    /// <summary>The scope every record and every trial lives in.</summary>
    public static Scope HarnessScope { get; } = new("reuse-baseline", "incident-desk", "settlement");

    /// <summary>The required check every task declares.</summary>
    public const string CheckId = "incident-check";

    /// <summary>The artifact revision every piece of evidence is bound to.</summary>
    public const string ArtifactRevision = "incident-runbook@rev-3";

    /// <summary>The producer recorded on every piece of evidence.</summary>
    public const string EvidenceProducer = "reuse-baseline-harness";

    /// <summary>The instant the learning phase's frozen clock reports.</summary>
    public static DateTimeOffset LearningInstant { get; } = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The instant every trial's frozen clock reports. One instant for all trials, so record recency is identical across conditions.</summary>
    public static DateTimeOffset TrialInstant { get; } = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly AuthorizationContext Authorization = new(
        TenantId: "reuse-baseline",
        PrincipalId: "reuse-baseline-harness",
        Roles: ["experience:read", "experience:write"],
        IssuedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly EnvironmentFingerprint HarnessEnvironment = new(
        HostName: "reuse-baseline-host",
        RuntimeVersion: "net10.0",
        OperatingSystem: "reuse-baseline-os",
        ApplicationVersion: "1.0.0-harness",
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["Fixture"] = "deterministic" });

    private static readonly RequiredCheck[] RequiredChecks = [new RequiredCheck(CheckId, "ToolExitCode")];

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "incident", "strategy" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 10,
            MaxValueLength: 4_000,
            MaxFieldNameLength: 100),
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 4_000,
            MaxFieldNameLength: 100),
    });

    private static readonly CaptureLimits Limits = new(
        MaxAttemptsPerRun: 16,
        MaxToolCallsPerAttempt: 50,
        MaxResultLength: 4_000,
        MaxErrorLength: 4_000);

    /// <summary>
    /// Runs one arm: reads the pre-registration, checks the task set, learns, runs every trial, and
    /// evaluates the gate once.
    /// </summary>
    /// <param name="options">How to run.</param>
    /// <param name="cancellationToken">Cancels the whole run.</param>
    /// <exception cref="PreregistrationException">The pre-registration is unreadable, or disagrees with the plan.</exception>
    /// <exception cref="TaskSetException">The task set is not one trials may be run from.</exception>
    public static async Task<ExperimentResult> RunAsync(ExperimentOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Read first, and never again until the report re-reads it. Nothing below this line may
        // choose a metric, a subset, or a threshold.
        var snapshot = options.Preregistration.Read();
        var design = snapshot.Design;
        var taskSet = options.Arm.TaskSet;

        taskSet.Validate();

        // Both halves matter: the arm has to have been declared, and it has to be running the task
        // set version that was declared for it.
        var declaredArm = design.ArmFor(options.Arm.Id);
        if (!string.Equals(taskSet.Version, declaredArm.TaskSetVersion, StringComparison.Ordinal))
        {
            throw new PreregistrationException(
                $"Arm '{options.Arm.Id}' is running task set version '{taskSet.Version}' and the pre-registration fixed '{declaredArm.TaskSetVersion}' for it.");
        }

        // Built from the evaluation set -- two trials per task -- and only then checked against the
        // pre-registered count. Comparing the declared count against itself would be a check that
        // cannot fail.
        var plan = TrialPlan.Build(design, taskSet.EvaluationTasks);

        var records = new InMemoryRecordStore();
        var feedbackLedger = new InMemoryReuseFeedbackStore();

        var learned = await LearnAsync(options, records, feedbackLedger, cancellationToken).ConfigureAwait(false);

        var trials = new List<TrialRecord>(plan.Count);
        var disagreements = new List<int>();

        foreach (var planned in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trial = await RunTrialAsync(
                options,
                design,
                planned,
                records,
                feedbackLedger,
                disagreements,
                cancellationToken).ConfigureAwait(false);

            trials.Add(trial);
            options.OnTrialRecorded?.Invoke(trial);
        }

        RequireEveryTrialsCostIsAccountedFor(options, plan, trials, learned);

        // Once. There is no second gate below this line and no branch that revisits the verdict.
        var gate = GateEvaluator.Evaluate(trials, design);

        return new ExperimentResult(
            options.Arm,
            snapshot,
            options.Preregistration,
            learned,
            trials,
            gate,
            [.. feedbackLedger.Rows.Values.OrderBy(row => row.FeedbackId)],
            disagreements,
            options.MaxAttemptsPerTrial);
    }

    /// <summary>
    /// Refuses the whole run unless every trial's cost is explained by what that trial's agent read
    /// out of its own context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the check that makes the memory-enabled arm's advantage attributable rather than
    /// assumed.</b> Without it, a harness that handed the agent the task's resolving strategy by any
    /// route -- while still retrieving, injecting and recording the block, and merely ignoring its
    /// content -- produces exactly the report a working harness produces, and every test passes. The
    /// only thing that would notice is the negative control, and a change scoped to spare it goes
    /// undetected.
    /// </para>
    /// <para>
    /// What is checked, per completed trial: the number of failed attempts the captured run actually
    /// holds equals the number the task set implies, given the strategies the agent says it read out
    /// of its context and the denied invocations it made. Both sides are computed independently --
    /// the left out of the capture service's snapshot, the right out of
    /// <see cref="ReuseBaselineTaskSet.ExpectedFailuresGiven"/>, which never sees the agent -- so
    /// they agree only when the agent acted on the block and on nothing else.
    /// </para>
    /// <para>
    /// And per condition: a memory-enabled trial that was exposed to a record must have read at
    /// least one strategy out of the block, and every strategy it read must be one the learned
    /// records actually name. A memory-disabled trial must have seen no block, read no strategy, and
    /// been exposed to no record at all -- which is what "the two arms differ by the condition alone"
    /// means, asserted rather than left to a byte comparison of the report.
    /// </para>
    /// </remarks>
    private static void RequireEveryTrialsCostIsAccountedFor(
        ExperimentOptions options,
        IReadOnlyList<TrialPlan.PlannedTrial> plan,
        IReadOnlyList<TrialRecord> trials,
        IReadOnlyList<LearnedRecord> learned)
    {
        var taskSet = options.Arm.TaskSet;
        var learnedStrategies = new HashSet<string>(
            learned.Select(record => record.WorkingStrategy).OfType<string>(),
            StringComparer.Ordinal);

        foreach (var trial in trials)
        {
            var task = plan[trial.Index].Task;

            if (trial.Condition == TrialCondition.MemoryDisabled)
            {
                if (trial.SawInjectedBlock || trial.StrategiesReadFromContext.Count > 0 || trial.ExposedExperienceIds.Count > 0)
                {
                    throw new HarnessIntegrityException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Trial {0} ran under the memory-disabled condition and yet saw a block ({1}), read {2} strategy(ies) out of "
                            + "context and was exposed to {3} record(s). The two conditions would then differ by more than the condition.",
                        trial.Index,
                        trial.SawInjectedBlock,
                        trial.StrategiesReadFromContext.Count,
                        trial.ExposedExperienceIds.Count));
                }
            }
            else if (trial.ExposedExperienceIds.Count > 0 && trial.Status == TrialStatus.Completed)
            {
                if (trial.StrategiesReadFromContext.Count == 0)
                {
                    throw new HarnessIntegrityException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Trial {0} was exposed to {1} record(s) and read no strategy out of the injected block. Whatever the agent did, "
                            + "it did not do it because of the block, so nothing this run reports about reuse is attributable to reuse.",
                        trial.Index,
                        trial.ExposedExperienceIds.Count));
                }

                var unaccounted = trial.StrategiesReadFromContext.Where(strategy => !learnedStrategies.Contains(strategy)).ToList();
                if (unaccounted.Count > 0)
                {
                    throw new HarnessIntegrityException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Trial {0} read strategy(ies) [{1}] out of its context, and the learned records name [{2}]. A strategy that "
                            + "reached the agent from somewhere other than a stored record is an advantage this experiment did not measure.",
                        trial.Index,
                        string.Join(", ", unaccounted),
                        string.Join(", ", learnedStrategies.OrderBy(strategy => strategy, StringComparer.Ordinal))));
                }
            }

            if (trial.Status != TrialStatus.Completed
                || trial.Metrics.FailedAttempts is not { } measured
                || trial.Metrics.UnauthorizedToolExecutions is not { } denied)
            {
                continue;
            }

            // Every denied invocation costs one attempt that made no incident check call, so it is an
            // attempt the task set's arithmetic knows nothing about and has to be added back.
            var implied = taskSet.ExpectedFailuresGiven(task, trial.StrategiesReadFromContext) + denied;

            // Past the attempt limit the run stops trying, so the arithmetic no longer describes it.
            if (implied >= options.MaxAttemptsPerTrial)
            {
                continue;
            }

            if (measured != implied)
            {
                throw new HarnessIntegrityException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Trial {0} on task '{1}' failed {2} attempt(s), and the task set implies {3} for an agent whose candidate list "
                        + "began with [{4}] and which was denied {5} invocation(s). The measured cost is not explained by what the "
                        + "agent read out of its own context, so the difference between the conditions is not attributable to the "
                        + "injected block. This is exactly how a harness that handed the agent the answer would look.",
                    trial.Index,
                    task.TaskId,
                    measured,
                    implied,
                    string.Join(", ", trial.StrategiesReadFromContext),
                    denied));
            }
        }
    }

    /// <summary>
    /// The learning phase: each learning task is run with no injected experience, verified, and
    /// finalized into an Experience Record.
    /// </summary>
    private static async Task<IReadOnlyList<LearnedRecord>> LearnAsync(
        ExperimentOptions options,
        InMemoryRecordStore records,
        InMemoryReuseFeedbackStore feedbackLedger,
        CancellationToken cancellationToken)
    {
        var learned = new List<LearnedRecord>(options.Arm.TaskSet.LearningTasks.Count);

        for (var index = 0; index < options.Arm.TaskSet.LearningTasks.Count; index++)
        {
            var task = options.Arm.TaskSet.LearningTasks[index];
            var ids = new TrialIdentities(options.Arm.Id + "/learning", index);
            var clock = new FrozenClock(LearningInstant);

            await using var provider = BuildContainer(options, clock, records, feedbackLedger, faultRetrieval: false);

            var boundary = new ToolApprovalBoundary();
            var execution = await ExecuteTaskAsync(
                provider,
                options,
                ids,
                task,
                memoryEnabled: false,
                boundary,
                clock,
                misfileEvidence: false,
                cancellationToken).ConfigureAwait(false);

            var finalization = provider.GetRequiredService<ExperienceFinalizationService>();
            var finalized = await finalization.FinalizeAsync(
                new FinalizeExperienceRequest(
                    RunId: ids.RunId,
                    Authorization: Authorization,
                    ClosedRound: new ClosedVerificationRound(ids.ClosedRoundId, ArtifactRevision),
                    RequiredChecks: RequiredChecks,
                    Evidence: execution.Evidence,
                    CurrentArtifactRevision: ArtifactRevision,
                    StorageDecision: StorageDecision.Permit,
                    FinalizedAt: clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            if (finalized.Outcome != FinalizationOutcome.Validated || finalized.Record is null)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "The learning phase could not finalize task '{0}': FinalizeAsync returned {1} at stage {2}. "
                        + "A memory-enabled condition with nothing to retrieve would compare two identical arms.",
                    task.TaskId,
                    finalized.Outcome,
                    finalized.Stage));
            }

            // Read back from the store rather than taken from what finalization returned.
            var readBack = await records.GetAsync(Authorization, HarnessScope, finalized.Record.ExperienceId, cancellationToken)
                .ConfigureAwait(false);

            if (readBack.Outcome != ExperienceStoreOutcome.Found || readBack.Record is null)
            {
                throw new InvalidOperationException(
                    $"The learning phase finalized task '{task.TaskId}' but the store answered {readBack.Outcome} when the record was read back.");
            }

            var record = new LearnedRecord(
                task.TaskId,
                readBack.Record.ExperienceId,
                readBack.Record.Status,
                readBack.Record.ReuseConfidence,
                execution.Run.Attempts.Count(attempt => attempt.Error is not null),
                WorkingApproachReflector.WorkingStrategyIn(execution.Run),
                readBack.Record.Reflection?.Lesson?.Contains(ToolApprovalBoundary.GuardedToolName, StringComparison.Ordinal) == true);

            learned.Add(record);
            options.OnRecordLearned?.Invoke(record);
        }

        return learned;
    }

    private static async Task<TrialRecord> RunTrialAsync(
        ExperimentOptions options,
        Preregistration design,
        TrialPlan.PlannedTrial planned,
        InMemoryRecordStore records,
        InMemoryReuseFeedbackStore feedbackLedger,
        List<int> disagreements,
        CancellationToken cancellationToken)
    {
        // Derived from the index and the pre-registration alone. No stored assignment list exists.
        var index = planned.Index;
        var condition = planned.Condition;
        var task = planned.Task;
        var ids = new TrialIdentities(options.Arm.Id, index);
        var fault = options.FaultAt?.Invoke(index);

        var clock = new FrozenClock(TrialInstant);
        var boundary = new ToolApprovalBoundary(guarded: options.UnguardTheGuardedToolAt?.Invoke(index) != true);
        var stopwatch = Stopwatch.StartNew();

        // The deadline runs on the injected clock, not on CancelAfter's system timer, so a test can
        // make whether it elapses a matter of construction rather than of machine load.
        using var expiry = new CancellationTokenSource(options.TrialTimeout, options.DeadlineClock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);

        await using var provider = BuildContainer(
            options,
            clock,
            records,
            feedbackLedger,
            faultRetrieval: fault?.Kind == TrialFaultKind.RetrievalFailure);

        TaskExecution? execution = null;
        var status = TrialStatus.Completed;
        string? classification = null;

        try
        {
            if (fault?.Kind == TrialFaultKind.Throw)
            {
                throw new InvalidOperationException("Injected trial fault.");
            }

            if (fault?.Kind == TrialFaultKind.Timeout)
            {
                // A hang, not a long delay: it completes only when the deadline cancels it, so there
                // is no duration for the deadline to race.
                var hang = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
                options.OnTrialHanging?.Invoke(index);
                await hang.ConfigureAwait(false);
            }

            execution = await ExecuteTaskAsync(
                provider,
                options,
                ids,
                task,
                condition == TrialCondition.MemoryEnabled,
                boundary,
                clock,
                fault?.Kind == TrialFaultKind.MisfiledEvidence,
                deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            status = TrialStatus.TimedOut;
            classification = string.Format(
                CultureInfo.InvariantCulture,
                "exceeded the per-trial deadline of {0} ms",
                options.TrialTimeout.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = TrialStatus.Errored;

            // The type name only. An exception message can carry a path, a host name, or caller data,
            // and this report is published.
            classification = ex.GetType().Name;
        }

        stopwatch.Stop();

        if (boundary.Executions != 0)
        {
            throw new HarnessIntegrityException(string.Format(
                CultureInfo.InvariantCulture,
                "Trial {0} executed the guarded tool {1} time(s). The approval boundary did not hold, so no unauthorized_tool_executions number this harness reports means anything.",
                index,
                boundary.Executions));
        }

        var exposed = execution is null
            ? []
            : execution.Injections
                .SelectMany(injection => injection.InjectedExperienceIds)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

        // Nullable on purpose rather than relying on default(InjectionOutcome) being Injected:
        // inserting any member ahead of Injected would otherwise make every clean trial report a
        // retrieval failure it never had. Failing safe by coincidence is not failing safe.
        InjectionOutcome? retrievalFailure = execution?.Injections
            .Select(injection => (InjectionOutcome?)injection.Outcome)
            .FirstOrDefault(outcome => outcome is InjectionOutcome.RetrievalFailed
                or InjectionOutcome.RetrievalTimedOut
                or InjectionOutcome.RetrievalDenied
                or InjectionOutcome.Failed);

        // A trial that did not finish has no value for failed_attempts, verified_success, tool_calls
        // or unauthorized_tool_executions: a partial count is not a count of what the task needed,
        // and reporting it as one would pull a mean in whichever direction the failure happened to
        // fall -- a timeout part-way through would dilute the denial mean towards passing. It keeps
        // its elapsed time, which is a complete measurement of what did happen.
        var metrics = new TrialMetrics(
            FailedAttempts: status == TrialStatus.Completed ? execution!.Run.Attempts.Count(attempt => attempt.Error is not null) : null,
            VerifiedSuccess: status == TrialStatus.Completed
                ? execution!.RunWithOutcome.Outcome?.Status == TaskVerificationStatus.Verified
                : null,
            UnauthorizedToolExecutions: status == TrialStatus.Completed ? boundary.Denied : null,
            ToolCalls: status == TrialStatus.Completed ? execution!.Run.Attempts.Sum(attempt => attempt.ToolCalls.Count) : null,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds);

        if (execution is not null && execution.TaskCheckPassed != (execution.RunWithOutcome.Outcome?.Status == TaskVerificationStatus.Verified))
        {
            disagreements.Add(index);
        }

        var (feedbackId, feedbackOutcome) = await SubmitFeedbackAsync(
            provider,
            design,
            ids,
            condition,
            exposed,
            execution,
            metrics,
            clock,
            cancellationToken).ConfigureAwait(false);

        return new TrialRecord(
            index,
            condition,
            task.TaskId,
            ids.RunId,
            ids.ClosedRoundId,
            status,
            metrics,
            classification,
            retrievalFailure is { } outcome ? "InjectionOutcome." + outcome : null,
            exposed,
            execution?.SawInjectedBlock == true,
            execution?.StrategiesReadFromContext ?? [],
            feedbackId,
            feedbackOutcome);
    }

    /// <summary>
    /// Submits one reuse feedback per exposed trial: the condition as the trial label, the primary
    /// metric as the one <see cref="ReuseMeasure"/>, and no attribution of any kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A memory-disabled trial submits nothing, and this is a fact about the ledger rather than a
    /// choice.</b> <c>ExperienceReuseFeedback.ExposedExperienceIds</c> must name at least one record
    /// -- "feedback about no exposure records nothing" -- so a trial that saw no record has no
    /// coherent submission to make. The report says how many trials submitted and how many did not.
    /// </para>
    /// <para>
    /// <b>No comparative result and no human assessment.</b> Frozen rule 11: fabricating either from
    /// a scripted run would move a real confidence score on the strength of a script. The comparative
    /// path is exercised in the tests, against synthetic evidence, and never here.
    /// </para>
    /// </remarks>
    private static async Task<(Guid? FeedbackId, string Outcome)> SubmitFeedbackAsync(
        IServiceProvider provider,
        Preregistration design,
        TrialIdentities ids,
        TrialCondition condition,
        IReadOnlyList<Guid> exposed,
        TaskExecution? execution,
        TrialMetrics metrics,
        FrozenClock clock,
        CancellationToken cancellationToken)
    {
        if (exposed.Count == 0)
        {
            return (null, "none: the trial was exposed to no record, and the ledger refuses a submission that names none");
        }

        if (metrics.FailedAttempts is not { } failedAttempts || execution is null)
        {
            return (null, "none: the trial produced no value for the primary metric, so there was nothing to measure in the submission");
        }

        var feedback = new ExperienceReuseFeedback(
            FeedbackId: ids.FeedbackId,
            RunId: ids.RunId,
            Scope: HarnessScope,
            ExposedExperienceIds: exposed,
            RunOutcome: execution.RunWithOutcome.Outcome?.Status ?? TaskVerificationStatus.Unknown,
            Measure: new ReuseMeasure(design.PrimaryMetric, failedAttempts),
            ObservedAt: clock.GetUtcNow(),
            ClaimedBenefit: ExperienceReuseBenefit.Unknown,
            HumanAssessment: null,
            ComparativeEvaluation: null,
            TrialLabel: design.LabelFor(condition));

        var recorded = await provider.GetRequiredService<ExperienceReuseFeedbackService>()
            .RecordAsync(Authorization, feedback, cancellationToken).ConfigureAwait(false);

        return (ids.FeedbackId, string.Format(
            CultureInfo.InvariantCulture,
            "ExperienceReuseFeedbackOutcome.{0}, benefit {1}, attribution {2}",
            recorded.Outcome,
            recorded.Benefit,
            recorded.AttributionSource));
    }

    /// <summary>What one task execution -- learning or trial -- produced.</summary>
    /// <param name="Run">The captured run.</param>
    /// <param name="RunWithOutcome">The same run with the verification aggregator's verdict attached.</param>
    /// <param name="Evidence">The evidence each attempt produced.</param>
    /// <param name="Injections">What the context provider did on each invocation.</param>
    /// <param name="TaskCheckPassed">What the deterministic IEvaluator made of the final exit code.</param>
    /// <param name="SawInjectedBlock">Whether a Historical Reference block reached the agent's own context.</param>
    /// <param name="StrategiesReadFromContext">The strategies the agent read out of that block, in the order the block named them.</param>
    private sealed record TaskExecution(
        ExperienceRun Run,
        ExperienceRun RunWithOutcome,
        IReadOnlyList<Evidence> Evidence,
        IReadOnlyList<ExperienceInjectionResult> Injections,
        bool TaskCheckPassed,
        bool SawInjectedBlock,
        IReadOnlyList<string> StrategiesReadFromContext);

    /// <summary>
    /// Drives one Experience Run: attempt after attempt under the declared policy, each appended to
    /// capture, until the incident check exits zero or the attempt limit is reached.
    /// </summary>
    private static async Task<TaskExecution> ExecuteTaskAsync(
        IServiceProvider provider,
        ExperimentOptions options,
        TrialIdentities ids,
        ReuseBaselineTask task,
        bool memoryEnabled,
        ToolApprovalBoundary boundary,
        FrozenClock clock,
        bool misfileEvidence,
        CancellationToken cancellationToken)
    {
        var capture = provider.GetRequiredService<IExperienceCaptureService>();
        var retrieval = provider.GetRequiredService<ExperienceRetrievalService>();
        var store = provider.GetRequiredService<IExperienceRecordStore>();

        var started = capture.StartRun(
            ids.RunId,
            task.TaskId,
            task.Text,
            HarnessScope,
            HarnessEnvironment,
            new Provenance("AgentExperience.ReuseBaseline", "1.0.0", clock.GetUtcNow(), CorrelationId: task.TaskId),
            clock.GetUtcNow());

        if (started.Outcome != StartRunOutcome.Started)
        {
            throw new InvalidOperationException($"StartRun returned {started.Outcome} for task '{task.TaskId}'.");
        }

        var policy = new PolicyChatClient(task.TaskId, options.Arm.TaskSet.ExplorationOrder);
        var tool = new IncidentCheckTool(task.TaskId, task.ResolvingStrategy);
        var evidence = new List<Evidence>();
        var injections = new List<ExperienceInjectionResult>();

        int? finalExitCode = null;

        for (var attempt = 0; attempt < options.MaxAttemptsPerTrial; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recorder = new AttemptToolRecorder(clock, ids.Next);

            var agentOptions = new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Tools = [tool.Function, boundary.GuardedTool] },
                AIContextProviders = memoryEnabled
                    ? [new ExperienceContextProvider(
                        retrieval,
                        store,
                        new ExperienceInjectionOptions
                        {
                            ResolveRequest = _ => new RetrieveExperienceRequest(
                                Authorization, HarnessScope, task.Text, CorrelationId: task.TaskId),
                            Limits = ExperienceInjectionLimits.Default with { EligibilityCheckTimeout = options.RetrievalTimeout },
                            OnContextInjected = injections.Add,
                            TimeProvider = clock,
                        })]
                    : [],
            };

            var agent = new ChatClientAgent(policy, agentOptions)
                .AsBuilder()
                .Use(recorder.InvokeAsync)
                .Build();

            var response = await agent.RunAsync(task.Text, cancellationToken: cancellationToken).ConfigureAwait(false);
            boundary.Observe(response);

            var exitCode = IncidentCheckTool.ExitCodeOf(recorder.LastResult);
            finalExitCode = exitCode;

            var strategy = recorder.Calls
                .LastOrDefault(call => string.Equals(call.ToolName, IncidentCheckTool.ToolName, StringComparison.Ordinal))
                ?.Arguments.TryGetValue(WorkingApproachReflector.StrategyArgument, out var value) == true && value is string named
                    ? named
                    : "(none)";

            var appended = await capture.AppendAttemptAsync(
                ids.RunId,
                new AppendAttemptRequest(
                    AttemptId: ids.Next(),
                    StartedAt: clock.GetUtcNow(),
                    Duration: TimeSpan.Zero,
                    ToolCalls: recorder.Calls,
                    Result: exitCode == 0 ? response.Text : null,
                    Error: exitCode == 0
                        ? null
                        : exitCode is { } code
                            ? string.Format(
                                CultureInfo.InvariantCulture,
                                "{0} exited {1} under strategy '{2}'; the incident is unresolved.",
                                IncidentCheckTool.ToolName,
                                code,
                                strategy)
                            : string.Format(
                                CultureInfo.InvariantCulture,
                                "the attempt made no {0} call, so the incident is unresolved.",
                                IncidentCheckTool.ToolName)),
                cancellationToken).ConfigureAwait(false);

            if (appended.Outcome != AppendAttemptOutcome.Recorded)
            {
                throw new InvalidOperationException($"AppendAttemptAsync returned {appended.Outcome} on task '{task.TaskId}'.");
            }

            // A failing attempt's evidence sits in a round the host never closes; the resolving
            // attempt's sits in the trial's own closed round. Inside one round a Fail on a required
            // check dominates a later Pass, so the two cannot share one.
            evidence.Add(TaskCheckEvaluators.ExitCode(
                ids.Next(),
                CheckId,
                exitCode == 0 && !misfileEvidence ? ids.ClosedRoundId : ids.OpenRoundId,
                ArtifactRevision,
                EvidenceProducer,
                clock.GetUtcNow(),
                exitCode));

            if (exitCode == 0)
            {
                break;
            }
        }

        var completed = await capture.CompleteRunAsync(
            ids.RunId,
            ids.Next(),
            RunExecutionStatus.Completed,
            clock.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        if (completed.Outcome != CompleteRunOutcome.Recorded)
        {
            throw new InvalidOperationException($"CompleteRunAsync returned {completed.Outcome} on task '{task.TaskId}'.");
        }

        if (!capture.TryGetRun(ids.RunId, out var run) || run is null)
        {
            throw new InvalidOperationException($"The completed run for task '{task.TaskId}' could not be read back from capture.");
        }

        var verdict = VerificationAggregator.Aggregate(
            ids.RunId,
            evidence,
            RequiredChecks,
            new ClosedVerificationRound(ids.ClosedRoundId, ArtifactRevision),
            ArtifactRevision,
            clock.GetUtcNow());

        // A second reading of the same recorded fact -- the final exit code -- through a
        // deterministic IEvaluator rather than through evidence and the aggregator. It is NOT an
        // independent observation: both readings start from the same variable, so agreement can
        // exonerate the aggregator's evidence handling and can never exonerate the observation. The
        // two are compared and reported, never silently reconciled.
        var evaluation = await new IncidentResolutionEvaluator().EvaluateAsync(
            [],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "n/a")),
            additionalContext: [new IncidentCheckContext(finalExitCode)],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var taskCheck = evaluation.Get<Microsoft.Extensions.AI.Evaluation.BooleanMetric>(IncidentResolutionEvaluator.MetricName);

        return new TaskExecution(
            run,
            run with { Outcome = verdict.Outcome },
            evidence,
            injections,
            taskCheck.Value == true,
            policy.SeenBlock is not null,
            [.. policy.StrategiesFromContext]);
    }

    /// <summary>
    /// One trial's container, built the way <c>SampleHost.ExecuteAsync</c> builds the sample's: a
    /// fresh <see cref="ServiceCollection"/> and a fresh provider, with the shared stores registered
    /// into it.
    /// </summary>
    /// <remarks>
    /// The record store and the feedback ledger are shared across trials on purpose -- memory that
    /// died with the trial would not be memory -- while everything else, capture included, is built
    /// fresh. Trials never write records: only the learning phase does, so every trial faces exactly
    /// the same stored experience and the arms differ by the condition alone.
    /// </remarks>
    private static ServiceProvider BuildContainer(
        ExperimentOptions options,
        FrozenClock clock,
        InMemoryRecordStore records,
        InMemoryReuseFeedbackStore feedbackLedger,
        bool faultRetrieval)
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(clock);

        services.AddSingleton<IExperienceRecordStore>(records);
        services.AddSingleton<IExperienceReuseFeedbackStore>(feedbackLedger);
        services.AddSingleton<IExperienceCandidateSource>(faultRetrieval
            ? new FaultingCandidateSource()
            : new InMemoryCandidateSource(records));

        // Registered before AddAgentExperienceCore, whose TryAdd leaves a host's own reflector in
        // place. The seam is the documented one; see WorkingApproachReflector's remarks for why the
        // default reflector alone cannot carry a working approach into a later run.
        IExperienceReflector reflector = new WorkingApproachReflector(new DefaultExperienceReflector());
        services.AddSingleton(options.DecorateReflector is null
            ? reflector
            : options.DecorateReflector(new DefaultExperienceReflector()));

        services.AddAgentExperienceCore(Sanitization, Limits);
        services.AddAgentExperienceReuseFeedback();
        services.AddAgentExperienceRetrieval(RetrievalPolicy.Default with { Timeout = options.RetrievalTimeout });

        return services.BuildServiceProvider();
    }

    /// <summary>A candidate source that always fails, so a memory-enabled trial's retrieval failure is reachable.</summary>
    private sealed class FaultingCandidateSource : IExperienceCandidateSource
    {
        public Task<ExperienceCandidateSearchResult> SearchAsync(
            AuthorizationContext authorization,
            ExperienceCandidateQuery query,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected candidate-source fault.");
    }
}

/// <summary>
/// A demonstration fixture, not a clock for real use: every reading is the same instant.
/// </summary>
/// <remarks>
/// <para>
/// Frozen, not stepping. The 4.2 sample's <c>SteppingTimeProvider</c> moves on every read, which
/// makes every <c>TimeProvider</c>-derived duration a function of how many reads happened -- fine
/// for a transcript nobody times, useless for a measurement. Here the clock is frozen so record
/// timestamps and retrieval recency are identical across conditions, and elapsed time comes from a
/// <see cref="Stopwatch"/> instead.
/// </para>
/// <para>
/// <see cref="CreateTimer"/> delegates to the real system clock, as the sample's does and for the
/// same reason: the library's timeouts are safety bounds on work that can hang, and a fixture that
/// never fires a timer would turn a hung call into a hung harness.
/// </para>
/// </remarks>
internal sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
{
    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => instant;

    /// <inheritdoc />
    public override long GetTimestamp() => instant.UtcTicks;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, dueTime, period);
}
