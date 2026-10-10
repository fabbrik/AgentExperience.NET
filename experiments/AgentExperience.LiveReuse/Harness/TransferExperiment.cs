using System.Globalization;
using System.Text.RegularExpressions;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Verification;
using AgentExperience.Storage.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>One distractor record the script finalized into the shared store.</summary>
public sealed record TransferDistractorRecord(int Index, string Service, string Cluster, DistractorFamily Family, string TaskId);

/// <summary>One learning run, with the cluster whose lesson it is.</summary>
public sealed record TransferLearningRun(string Cluster, RunRecord Run);

/// <summary>
/// The reported, never gated, harm comparison: the mismatched-trait control against memory-disabled, pair by pair. A
/// block that names other clusters' strategies may cost failed attempts; this says how often it did.
/// </summary>
/// <param name="Pairs">Included instances compared.</param>
/// <param name="Worse">Pairs in which mismatched-trait failed more attempts than memory-disabled.</param>
/// <param name="Better">Pairs in which it failed fewer.</param>
/// <param name="Tied">Pairs in which it failed the same number.</param>
/// <param name="MismatchedMeanFailedAttempts">Mean failed attempts under mismatched-trait, or <see langword="null"/> with no pair.</param>
/// <param name="DisabledMeanFailedAttempts">Mean failed attempts under memory-disabled, or <see langword="null"/> with no pair.</param>
public sealed record HarmComparison(int Pairs, int Worse, int Better, int Tied, double? MismatchedMeanFailedAttempts, double? DisabledMeanFailedAttempts)
{
    public static HarmComparison Of(IReadOnlyList<PairedObservation> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var worse = pairs.Count(pair => pair.TreatmentFailedAttempts > pair.ControlFailedAttempts);
        var better = pairs.Count(pair => pair.TreatmentFailedAttempts < pair.ControlFailedAttempts);
        return new HarmComparison(
            pairs.Count,
            worse,
            better,
            pairs.Count - worse - better,
            pairs.Count == 0 ? null : pairs.Average(pair => pair.TreatmentFailedAttempts),
            pairs.Count == 0 ? null : pairs.Average(pair => pair.ControlFailedAttempts));
    }
}

/// <summary>
/// One evaluation trial of the transfer experiment: its run record, and what the library's retrieval put in the block
/// its model was shown, read out of the text it was sent. No block text is kept.
/// </summary>
/// <param name="Run">The trial, exactly as the reuse experiment records one.</param>
/// <param name="Cluster">The evaluation service's cluster.</param>
/// <param name="HiddenStrategy">The strategy the cluster's database proxies accept.</param>
/// <param name="SameClusterTaskId">The capture task ID of the record the cluster's learning run stored (or would have).</param>
/// <param name="BlockRecords">The task ID on each record header of the block, in rank order. Empty when no block was shown.</param>
/// <param name="StrategyMentions">How many times the whole block names any strategy (the placebo's must be zero).</param>
public sealed record TransferTrial(
    RunRecord Run,
    string Cluster,
    string HiddenStrategy,
    string SameClusterTaskId,
    IReadOnlyList<string> BlockRecords,
    int StrategyMentions)
{
    /// <summary>The 1-based rank of the same-cluster learning record in the block, or <see langword="null"/> when it is not in it.</summary>
    public int? SameClusterRank => BlockRecords.ToList().IndexOf(SameClusterTaskId) is var at and >= 0 ? at + 1 : null;

    /// <summary>Whether the same-cluster learning record was in the block.</summary>
    public bool SameClusterInjected => SameClusterRank is not null;

    /// <summary>
    /// Whether the trial's first <c>apply_migration</c> strategy was the strategy the same-cluster learning record stored
    /// (in any condition, block or not); <see langword="null"/> when that cluster stored no record. Never gated.
    /// </summary>
    public bool? FollowedTransferredLesson { get; init; }
}

/// <summary>An evaluation instance left out of every comparison, and why.</summary>
public sealed record TransferExclusion(int Instance, string Reason);

/// <summary>Everything one run of the transfer experiment produced.</summary>
public sealed record TransferExperimentResult(
    RunDescriptor Descriptor,
    TransferPreregistration Design,
    string TaskSetVersion,
    string TraitDigest,
    LiveBudget Budget,
    DateTimeOffset StartedAt,
    IReadOnlyList<TransferDistractorRecord> Distractors,
    IReadOnlyList<TransferLearningRun> Learning,
    IReadOnlyList<TransferTrial> Trials,
    bool Complete,
    string? StopReason,
    IReadOnlyList<TransferExclusion> Excluded,
    ComparisonResult Reference,
    ComparisonResult Content,
    ComparisonResult MismatchedControl,
    HarmComparison Harm,
    TransferConclusion Conclusion,
    IReadOnlyList<string> ModelIds,
    int CallsWithoutUsage,
    UsageTotals Total)
{
    /// <summary>How many runs of the same provider and model <c>results/transfer-ledger.tsv</c> held when this one started; <see langword="null"/> without a ledger.</summary>
    public int? EarlierLedgerEntries { get; init; }
}

/// <summary>How to run the transfer experiment.</summary>
public sealed class TransferExperimentOptions
{
    /// <summary>The model. Any <see cref="IChatClient"/>: a provider's, or a scripted one offline.</summary>
    public required IChatClient Model { get; init; }

    /// <summary>What the model is, for the report.</summary>
    public required RunDescriptor Descriptor { get; init; }

    /// <summary>The design: the embedded, checked-in transfer pre-registration in every live run.</summary>
    public TransferPreregistration Design { get; init; } = TransferPreregistration.ReadEmbedded();

    /// <summary>The task set: <see cref="TransferTaskSet.Current"/> in every live run; checked against <see cref="Design"/>.</summary>
    public TransferTaskSet TaskSet { get; init; } = TransferTaskSet.Current;

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
    public Action<RunRecord>? OnRunRecorded { get; init; }
}

/// <summary>
/// The transfer experiment (story 20.4): a lesson learned on one service has to be found by the library's own retrieval
/// in a shared store full of other experience, and has to help on a different service that shares only a trait -- its
/// cluster, whose database proxies all accept one rollout strategy.
/// </summary>
/// <remarks>
/// <para>
/// One shared scope and one shared <see cref="InMemoryExperienceRecordStore"/> hold every record: twenty-four distractors
/// (cache flushes and config rollouts, finalized by a fixed script, no model) and the verified learning runs (memory disabled,
/// one per cluster). Every trial is a fresh container over that store, with the library's
/// <see cref="InMemoryExperienceCandidateSource"/> and the default <see cref="RetrievalPolicy"/>; injection is the
/// shipped context provider. Nothing of the sample's matcher is used.
/// </para>
/// <para>
/// The mismatched-trait control injects from a copy of the store without the evaluation service's own cluster's
/// learning record, so whatever it retrieves is another cluster's lesson or a distractor.
/// </para>
/// </remarks>
public static partial class TransferExperiment
{
    public const string LearnCondition = "learn-cluster";
    public const string DistractorCheckId = "cache-flushed";
    public const string DistractorArtifactRevision = "cache-runbook@rev-1";
    public const string ConfigCheckId = "config-applied";
    public const string ConfigArtifactRevision = "config-runbook@rev-1";
    public const string LearningTaskSuffix = "/learning";
    public const string DistractorTaskSuffix = "/flush";
    public const string ConfigTaskSuffix = "/config";

    /// <summary>The one scope every record and every trial shares.</summary>
    public static Scope SharedScope { get; } = new("live-reuse", "transfer-desk", "shared");

    private static readonly RequiredCheck[] DistractorChecks = [new RequiredCheck(DistractorCheckId, "ToolExitCode")];

    private static readonly RequiredCheck[] ConfigChecks = [new RequiredCheck(ConfigCheckId, "ToolExitCode")];

    /// <summary>The four evaluation conditions for instance <paramref name="index"/>: the 9.1 rotation, the mismatched control in the negative control's slot.</summary>
    public static IReadOnlyList<string> ConditionOrder(TransferPreregistration design, int index)
    {
        ArgumentNullException.ThrowIfNull(design);
        string[] conditions = [design.ControlLabel, design.TreatmentLabel, design.MismatchedLabel, design.PlaceboLabel];
        var shift = index % conditions.Length;
        return [.. conditions.Skip(shift), .. conditions.Take(shift)];
    }

    public static async Task<TransferExperimentResult> RunAsync(TransferExperimentOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var design = options.Design;
        var taskSet = options.TaskSet;

        // Refuse first: nothing below may call a model for a design the file does not describe.
        taskSet.Validate();
        design.Check(taskSet);

        var budget = options.Budget ?? new LiveBudget(design.DefaultMaxModelCalls, design.DefaultMaxTotalTokens);
        var startedAt = options.Clock.GetUtcNow();
        var metered = new MeteredChatClient(options.Model, budget, options.Clock, options.MinimumCallInterval);
        var settings = new RunSettings(options.Clock, options.CallTimeout, options.Descriptor.SeedSent, design.Temperature, design.Seed, design.ToolCallsPerAttempt);

        var full = new InMemoryExperienceRecordStore(new FrozenClock(LiveReuseExperiment.LearningInstant));
        var stored = new List<ExperienceRecord>();
        var distractors = new List<TransferDistractorRecord>();
        var learning = new List<TransferLearningRun>();
        var learned = new Dictionary<string, ExperienceRecord>(StringComparer.Ordinal);
        var trials = new List<TransferTrial>();
        string? stopReason = null;
        var sequence = 0;

        // ---- Distractors: a fixed script, no model, each finalized through the library into the shared store. ------
        foreach (var distractor in taskSet.Distractors)
        {
            var record = await FinalizeDistractorAsync(full, distractor, cancellationToken).ConfigureAwait(false);
            stored.Add(record);
            distractors.Add(new TransferDistractorRecord(distractor.Index, distractor.Service, distractor.Cluster, distractor.Family, record.TaskId));
        }

        // ---- Learning: one memory-disabled run per cluster, finalized into the shared store if it verifies. --------
        foreach (var service in taskSet.LearningServices)
        {
            if (stopReason is not null)
            {
                break;
            }

            var hidden = taskSet.HiddenStrategy(service);
            var wiring = new RunWiring(SharedScope, service.Service + LearningTaskSuffix, clock => BuildContainer(clock, full), Inject: false, Finalize: true, ShowStrategy: true, service.Cluster);
            var outcome = await LiveReuseExperiment.RunCoreAsync(
                settings, metered, ++sequence, "learning", taskSet.ToMigrationInstance(service), LearnCondition, hidden,
                TransferTaskSet.LearningText(service), service.Migration, design.LearningAttemptLimit, wiring, cancellationToken).ConfigureAwait(false);

            learning.Add(new TransferLearningRun(service.Cluster, outcome.Record));
            Report(options, outcome.Record, service.Cluster);
            options.OnRunRecorded?.Invoke(outcome.Record);
            RequireNoBlock(outcome.Record);
            if (outcome.Stored is { } record)
            {
                stored.Add(record);
                learned[service.Cluster] = record;
            }

            if (outcome.Record.Status == RunStatus.BudgetExhausted)
            {
                stopReason = "the budget cap was reached during the learning phase (" + outcome.Record.Classification + ")";
            }
        }

        // ---- The mismatched-trait stores: the full store minus one cluster's learning record. -------------------------
        var fullTaskIds = stored.Select(record => record.TaskId).ToHashSet(StringComparer.Ordinal);
        var mismatched = new Dictionary<string, InMemoryExperienceRecordStore>(StringComparer.Ordinal);
        var mismatchedTaskIds = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var cluster in taskSet.Clusters)
        {
            var own = taskSet.LearningServiceFor(cluster).Service + LearningTaskSuffix;
            var store = new InMemoryExperienceRecordStore(new FrozenClock(LiveReuseExperiment.LearningInstant));
            foreach (var record in stored.Where(record => record.TaskId != own))
            {
                var created = await store.CreateAsync(LiveReuseExperiment.Authorization, record, cancellationToken).ConfigureAwait(false);
                if (created.Outcome != ExperienceStoreOutcome.Created)
                {
                    throw new HarnessIntegrityException($"Copying record '{record.TaskId}' into the mismatched store of cluster {cluster} returned {created.Outcome}.");
                }
            }

            mismatched[cluster] = store;
            mismatchedTaskIds[cluster] = stored.Where(record => record.TaskId != own).Select(record => record.TaskId).ToHashSet(StringComparer.Ordinal);
        }

        // ---- Evaluation: four conditions per unseen service, in the rotated order. ----------------------------------
        // An instance whose cluster stored no lesson is excluded before evaluation and runs no trial.
        var storedStrategies = StoredStrategiesByTaskId(learning);
        var runnable = taskSet.EvaluationInstances.Where(instance => learned.ContainsKey(instance.Cluster)).ToList();
        if (stopReason is null)
        {
            foreach (var instance in runnable)
            {
                var hidden = taskSet.HiddenStrategy(instance);
                var sameCluster = taskSet.LearningServiceFor(instance.Cluster).Service + LearningTaskSuffix;
                foreach (var condition in ConditionOrder(design, instance.Index))
                {
                    if (stopReason is not null)
                    {
                        break;
                    }

                    var store = condition == design.MismatchedLabel ? mismatched[instance.Cluster] : full;
                    var wiring = new RunWiring(
                        SharedScope,
                        instance.Service + "/ticket",
                        clock => BuildContainer(clock, store),
                        Inject: condition != design.ControlLabel,
                        Finalize: false,
                        ShowStrategy: condition != design.PlaceboLabel,
                        instance.Cluster,
                        UseRunTools: true);

                    var outcome = await LiveReuseExperiment.RunCoreAsync(
                        settings, metered, ++sequence, "evaluation", taskSet.ToMigrationInstance(instance), condition, hidden,
                        TransferTaskSet.EvaluationText(instance), instance.Migration, design.EvaluationAttemptLimit, wiring, cancellationToken).ConfigureAwait(false);

                    var trial = new TransferTrial(
                        outcome.Record,
                        instance.Cluster,
                        hidden,
                        sameCluster,
                        BlockRecords(outcome.Block),
                        outcome.Block is null ? 0 : RolloutStrategies.AllNamedIn(outcome.Block).Count)
                    {
                        FollowedTransferredLesson = learned.ContainsKey(instance.Cluster)
                            ? outcome.Record.FirstStrategy == storedStrategies.GetValueOrDefault(sameCluster)?.FirstOrDefault()
                            : null,
                    };
                    trials.Add(trial);

                    // Checked as each trial ends, so a broken design stops before the rest of the budget is spent.
                    RequireTrialIntegrity(design, trial, storedStrategies, fullTaskIds, mismatchedTaskIds);
                    Report(options, outcome.Record, instance.Cluster, trial);
                    options.OnRunRecorded?.Invoke(outcome.Record);
                    if (outcome.Record.Status == RunStatus.BudgetExhausted)
                    {
                        stopReason = "the budget cap was reached during the evaluation phase (" + outcome.Record.Classification + ")";
                    }
                }
            }
        }

        const int Conditions = 4;
        var complete = stopReason is null && trials.Count == runnable.Count * Conditions;
        var learnedTaskIds = learned.ToDictionary(pair => pair.Key, pair => pair.Value.TaskId, StringComparer.Ordinal);
        RequireIntegrity(design, [.. learning.Select(run => run.Run)], trials, learnedTaskIds, fullTaskIds, mismatchedTaskIds);

        var excluded = complete ? Exclusions(taskSet, learnedTaskIds, trials) : [];
        var excludedIndexes = excluded.Select(exclusion => exclusion.Instance).ToHashSet();
        var included = taskSet.EvaluationInstances.Where(instance => !excludedIndexes.Contains(instance.Index)).Select(instance => instance.Index).ToList();
        var byInstance = trials.GroupBy(trial => trial.Run.Instance).ToDictionary(group => group.Key, group => group.Select(trial => trial.Run).ToList());

        List<PairedObservation> Pairs(string treatment, string control) => !complete ? [] : [.. included.Select(index =>
        {
            var runs = byInstance[index];
            var t = runs.Single(run => run.Condition == treatment);
            var c = runs.Single(run => run.Condition == control);
            return new PairedObservation(index, t.FailedAttempts!.Value, c.FailedAttempts!.Value, t.Verified!.Value, c.Verified!.Value, t.UnauthorizedRequests, c.UnauthorizedRequests);
        })];

        var reference = LiveGate.Evaluate(design.TreatmentLabel, design.ControlLabel, Pairs(design.TreatmentLabel, design.ControlLabel), excluded.Count, design.Alpha, design.MaxExcludedInstances, complete);
        var content = LiveGate.Evaluate(design.TreatmentLabel, design.PlaceboLabel, Pairs(design.TreatmentLabel, design.PlaceboLabel), excluded.Count, design.Alpha, design.MaxExcludedInstances, complete);
        var controlPairs = Pairs(design.MismatchedLabel, design.ControlLabel);
        var control = LiveGate.Evaluate(design.MismatchedLabel, design.ControlLabel, controlPairs, excluded.Count, design.Alpha, design.MaxExcludedInstances, complete);

        return new TransferExperimentResult(
            options.Descriptor,
            design,
            taskSet.Version,
            taskSet.TraitDigest(),
            budget,
            startedAt,
            distractors,
            learning,
            trials,
            complete,
            stopReason,
            excluded,
            reference,
            content,
            control,
            HarmComparison.Of(controlPairs),
            LiveGate.ConcludeTransfer(reference.Verdict, content.Verdict, control.Verdict),
            [.. metered.ModelIds],
            metered.CallsWithoutUsage,
            metered.Totals);
    }

    /// <summary>
    /// The pre-registered exclusion rule: an instance whose cluster's learning run did not verify (so nothing of its
    /// cluster was stored), and an instance any of whose four trials errored, are left out of every comparison.
    /// </summary>
    internal static List<TransferExclusion> Exclusions(TransferTaskSet taskSet, IReadOnlyDictionary<string, string> learned, IReadOnlyList<TransferTrial> trials)
    {
        var excluded = new List<TransferExclusion>();
        foreach (var instance in taskSet.EvaluationInstances)
        {
            var runs = trials.Where(trial => trial.Run.Instance == instance.Index).Select(trial => trial.Run).ToList();
            if (!learned.ContainsKey(instance.Cluster))
            {
                excluded.Add(new TransferExclusion(instance.Index, $"the learning run of cluster {instance.Cluster} did not verify, so it ran no evaluation trial"));
            }
            else if (runs.Count != 4 || runs.Any(run => run.Status != RunStatus.Completed))
            {
                excluded.Add(new TransferExclusion(instance.Index, "an evaluation trial errored"));
            }
        }

        return excluded;
    }

    /// <summary>
    /// The conditions must differ by what was injected and by nothing else, and every block must be what its store
    /// can produce. Throws <see cref="HarnessIntegrityException"/> on the first violation.
    /// </summary>
    /// <remarks>
    /// Checked: no memory-disabled trial or learning run saw a block; every record a block names exists in the store
    /// it came from; memory-enabled and the placebo were shown the same records in the same order (they differ only by
    /// the allowlist); the placebo's block names no strategy anywhere; the mismatched store lacks the evaluation
    /// service's own cluster's learning record, its block does not carry it, and the strategy its working line names
    /// first is not the cluster's; and a memory-enabled block's first record, when it is a learning record, shows the
    /// strategies that record stored.
    /// </remarks>
    internal static void RequireIntegrity(
        TransferPreregistration design,
        IReadOnlyList<RunRecord> learning,
        IReadOnlyList<TransferTrial> trials,
        IReadOnlyDictionary<string, string> learned,
        IReadOnlySet<string> fullTaskIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> mismatchedTaskIds)
    {
        foreach (var run in learning)
        {
            RequireNoBlock(run);
        }

        var storedStrategies = StoredStrategiesByTaskId(learning);
        foreach (var trial in trials)
        {
            RequireTrialIntegrity(design, trial, storedStrategies, fullTaskIds, mismatchedTaskIds);
        }

        // Memory-enabled and the placebo query the same store with the same text: the same records, in the same order.
        foreach (var group in trials.Where(trial => trial.Run.Status == RunStatus.Completed).GroupBy(trial => trial.Run.Instance))
        {
            var enabled = group.FirstOrDefault(trial => trial.Run.Condition == design.TreatmentLabel);
            var placebo = group.FirstOrDefault(trial => trial.Run.Condition == design.PlaceboLabel);
            if (enabled is not null && placebo is not null && !enabled.BlockRecords.SequenceEqual(placebo.BlockRecords, StringComparer.Ordinal))
            {
                throw new HarnessIntegrityException($"Instance {group.Key}: memory-enabled and the placebo were shown different records; they may differ only by the allowlist.");
            }
        }

        foreach (var (cluster, taskId) in learned)
        {
            if (!fullTaskIds.Contains(taskId))
            {
                throw new HarnessIntegrityException($"Cluster {cluster}'s learning record is missing from the shared store.");
            }
        }
    }

    /// <summary>Every learning record's task ID, with the strategies its final attempt stored.</summary>
    internal static Dictionary<string, IReadOnlyList<string>> StoredStrategiesByTaskId(IEnumerable<TransferLearningRun> learning) =>
        StoredStrategiesByTaskId(learning.Select(run => run.Run));

    internal static Dictionary<string, IReadOnlyList<string>> StoredStrategiesByTaskId(IEnumerable<RunRecord> learning) =>
        learning.Where(run => run.StoredStrategy is not null)
            .ToDictionary(run => run.Service + LearningTaskSuffix, run => run.StoredStrategies, StringComparer.Ordinal);

    private static void RequireNoBlock(RunRecord run)
    {
        if (run.BlockSeen)
        {
            throw new HarnessIntegrityException($"Run {run.Sequence} ({run.Phase}, {run.Condition}) has memory disabled and yet its model was shown a Historical Reference block.");
        }
    }

    /// <summary>The checks that need only one trial: run as each trial ends, and again over all of them at the end.</summary>
    internal static void RequireTrialIntegrity(
        TransferPreregistration design,
        TransferTrial trial,
        IReadOnlyDictionary<string, IReadOnlyList<string>> storedStrategies,
        IReadOnlySet<string> fullTaskIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> mismatchedTaskIds)
    {
        var run = trial.Run;
        if (run.Condition == design.ControlLabel)
        {
            RequireNoBlock(run);
            return;
        }

        if (run.BlockSeen && trial.BlockRecords.Count == 0)
        {
            throw new HarnessIntegrityException($"Trial {run.Sequence} ({run.Condition}) was shown a block whose record headers could not be read.");
        }

        var mismatchedCondition = run.Condition == design.MismatchedLabel;
        if (mismatchedCondition)
        {
            if (!mismatchedTaskIds.TryGetValue(trial.Cluster, out var available) || available.Contains(trial.SameClusterTaskId))
            {
                throw new HarnessIntegrityException($"Trial {run.Sequence} ({run.Condition}): the mismatched store of cluster {trial.Cluster} holds that cluster's own learning record.");
            }

            if (trial.SameClusterInjected)
            {
                throw new HarnessIntegrityException($"Trial {run.Sequence} ({run.Condition}) was shown its own cluster's learning record.");
            }

            if (run.BlockStrategy == trial.HiddenStrategy)
            {
                throw new HarnessIntegrityException($"Trial {run.Sequence} ({run.Condition}) was shown a block whose working line names its cluster's strategy '{trial.HiddenStrategy}'.");
            }
        }

        var source = mismatchedCondition ? mismatchedTaskIds.GetValueOrDefault(trial.Cluster) ?? new HashSet<string>() : fullTaskIds;
        if (trial.BlockRecords.FirstOrDefault(taskId => !source.Contains(taskId)) is { } missing)
        {
            throw new HarnessIntegrityException($"Trial {run.Sequence} ({run.Condition}) was shown a record '{missing}' that the store it came from does not hold.");
        }

        if (run.Condition == design.PlaceboLabel && trial.StrategyMentions != 0)
        {
            throw new HarnessIntegrityException($"Trial {run.Sequence} ({run.Condition}) was shown a placebo block that names a strategy.");
        }

        // Content, not only presence: the enabled block's first record, when it is a learning record, must show
        // exactly the strategies that record stored; a distractor first shows none on its working line.
        if (run.Condition == design.TreatmentLabel && trial.BlockRecords.Count > 0)
        {
            IReadOnlyList<string> expected = storedStrategies.GetValueOrDefault(trial.BlockRecords[0]) ?? [];
            if (!run.BlockStrategies.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new HarnessIntegrityException(
                    $"Trial {run.Sequence} ({run.Condition}) was shown a first record naming '{LiveReuseExperiment.Sequence(run.BlockStrategies, "no strategy")}'; the store holds '{LiveReuseExperiment.Sequence(expected, "no strategy")}'.");
            }
        }
    }

    /// <summary>The task ID on each record header of a compact block, in rank order.</summary>
    internal static IReadOnlyList<string> BlockRecords(string? block) =>
        block is null ? [] : [.. RecordHeader().Matches(block).Select(match => match.Groups[1].Value)];

    [GeneratedRegex("^--- RECORD [0-9]+: (.+) ---$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RecordHeader();

    private static void Report(TransferExperimentOptions options, RunRecord record, string cluster, TransferTrial? trial = null)
    {
        options.Progress?.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[{0,3}] {1,-10} {2,-14} {3,-9} {4,-18} {5}{6}{7}",
            record.Sequence,
            record.Phase,
            record.Service,
            cluster,
            record.Condition,
            record.Status == RunStatus.Completed
                ? (record.Verified == true ? "live" : "NOT live") + " after " + record.FailedAttempts + " failed attempt(s)"
                : record.Status + ": " + record.Classification,
            record.StoredStrategy is not null ? "; stored a record naming " + LiveReuseExperiment.Sequence(record.StoredStrategies, "-") : string.Empty,
            trial is { Run.BlockSeen: true } ? $"; block of {trial.BlockRecords.Count}, same-cluster rank {trial.SameClusterRank?.ToString(CultureInfo.InvariantCulture) ?? "-"}" : string.Empty));
    }

    /// <summary>One shared store behind a fresh container: the library's in-memory store and candidate source, the default retrieval policy.</summary>
    private static ServiceProvider BuildContainer(FrozenClock clock, InMemoryExperienceRecordStore store)
    {
        // Not AddAgentExperienceInMemoryStorageForDevelopment: it registers a fresh store per container and refuses a
        // second registration, and every trial here must read the one shared store.
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IExperienceRecordStore>(store);
        services.AddSingleton<IExperienceCandidateSource>(new InMemoryExperienceCandidateSource(store));
        services.AddAgentExperienceCore(LiveReuseExperiment.Sanitization, LiveReuseExperiment.Limits);
        services.AddAgentExperienceRetrieval(RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(15) });
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The distractor script: one attempt, one <c>flush_read_cache</c> call, verified from the cache's state and
    /// finalized through the library exactly as a learning run is. No model is involved.
    /// </summary>
    private static async Task<ExperienceRecord> FinalizeDistractorAsync(InMemoryExperienceRecordStore store, TransferDistractor distractor, CancellationToken cancellationToken)
    {
        var config = distractor.Family == DistractorFamily.ConfigRollout;
        var ids = new RunIdentities(config ? "distractor/config" : "distractor/flush", distractor.Index);
        var frozen = new FrozenClock(LiveReuseExperiment.LearningInstant);
        var taskId = distractor.Service + (config ? ConfigTaskSuffix : DistractorTaskSuffix);
        var checkId = config ? ConfigCheckId : DistractorCheckId;
        var revision = config ? ConfigArtifactRevision : DistractorArtifactRevision;
        await using var provider = BuildContainer(frozen, store);

        var capture = provider.GetRequiredService<IExperienceCaptureService>();
        var opened = capture.StartRun(ids.RunId, taskId, distractor.Text, SharedScope, LiveReuseExperiment.HarnessEnvironment,
            new Provenance("AgentExperience.LiveReuse", "1.0.0", frozen.GetUtcNow(), CorrelationId: taskId), frozen.GetUtcNow());
        if (opened.Outcome != StartRunOutcome.Started)
        {
            throw new HarnessIntegrityException($"StartRun returned {opened.Outcome} for distractor {distractor.Index}.");
        }

        // The script: one call of the family's tool, which takes no strategy.
        string tool;
        string output;
        bool done;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["service"] = distractor.Service };
        if (config)
        {
            var environment = new ConfigEnvironment(distractor.Service, distractor.Change!);
            arguments["change"] = distractor.Change;
            tool = ConfigEnvironment.PushToolName;
            output = environment.PushConfig(distractor.Service, distractor.Change!);
            done = environment.IsLive;
        }
        else
        {
            var environment = new CacheEnvironment(distractor.Service);
            tool = CacheEnvironment.FlushToolName;
            output = environment.FlushReadCache(distractor.Service);
            done = environment.IsFlushed;
        }

        var call = new RawToolCall(ids.Next(), tool, arguments, frozen.GetUtcNow(), TimeSpan.Zero, output, null);
        var appended = await capture.AppendAttemptAsync(
            ids.RunId,
            new AppendAttemptRequest(ids.Next(), frozen.GetUtcNow(), TimeSpan.Zero, [call], done ? "done" : null, done ? null : "the change did not take effect."),
            cancellationToken).ConfigureAwait(false);
        if (appended.Outcome != AppendAttemptOutcome.Recorded)
        {
            throw new HarnessIntegrityException($"AppendAttemptAsync returned {appended.Outcome} for distractor {distractor.Index}.");
        }

        Evidence[] evidence =
        [
            TaskCheckEvaluators.ExitCode(ids.Next(), checkId, ids.ClosedRoundId, revision, LiveReuseExperiment.EvidenceProducer, frozen.GetUtcNow(), done ? 0 : 1),
        ];

        var completed = await capture.CompleteRunAsync(ids.RunId, ids.Next(), RunExecutionStatus.Completed, frozen.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (completed.Outcome != CompleteRunOutcome.Recorded)
        {
            throw new HarnessIntegrityException($"Distractor {distractor.Index} could not be completed ({completed.Outcome}).");
        }

        var finalized = await provider.GetRequiredService<ExperienceFinalizationService>().FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: ids.RunId,
                Authorization: LiveReuseExperiment.Authorization,
                ClosedRound: new ClosedVerificationRound(ids.ClosedRoundId, revision),
                RequiredChecks: config ? ConfigChecks : DistractorChecks,
                Evidence: evidence,
                CurrentArtifactRevision: revision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: frozen.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        if (finalized.Outcome != FinalizationOutcome.Validated || finalized.Record is null)
        {
            throw new HarnessIntegrityException($"Distractor {distractor.Index} was not finalized: {finalized.Outcome} at stage {finalized.Stage}.");
        }

        var readBack = await store.GetAsync(LiveReuseExperiment.Authorization, SharedScope, finalized.Record.ExperienceId, cancellationToken).ConfigureAwait(false);
        return readBack.Outcome == ExperienceStoreOutcome.Found && readBack.Record is not null
            ? readBack.Record
            : throw new HarnessIntegrityException($"Distractor {distractor.Index} was finalized but could not be read back ({readBack.Outcome}).");
    }
}
