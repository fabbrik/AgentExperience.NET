using System.ClientModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Sanitization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Sample.EndToEnd.Doubles;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>How one run ended.</summary>
public enum RunStatus
{
    /// <summary>Every attempt ran; the run is scored.</summary>
    Completed,

    /// <summary>A provider or transport failure. Recorded by exception type and HTTP status only.</summary>
    Errored,

    /// <summary>The budget cap stopped the run, and with it the experiment.</summary>
    BudgetExhausted,
}

/// <summary>What the provider was, with nothing secret in it: the key never enters this type.</summary>
/// <param name="Provider">gemini, azure, or scripted.</param>
/// <param name="RequestedModel">The model (Gemini) or deployment (Azure) asked for.</param>
/// <param name="EndpointHost">The endpoint's host only. No path, no query, no key.</param>
/// <param name="InputUsdPerMillionTokens">The input price used for the cost estimate, if one is known.</param>
/// <param name="OutputUsdPerMillionTokens">The output price used for the cost estimate, if one is known.</param>
/// <param name="PriceSource">Where the prices came from.</param>
public sealed record RunDescriptor(
    string Provider,
    string RequestedModel,
    string EndpointHost,
    double? InputUsdPerMillionTokens,
    double? OutputUsdPerMillionTokens,
    string PriceSource);

/// <summary>One learning run or evaluation trial, as recorded. No prompt, no message text, no key.</summary>
public sealed record RunRecord(
    int Sequence,
    string Phase,
    int Instance,
    string Service,
    string Condition,
    string AcceptedStrategy,
    RunStatus Status,
    string? Classification,
    bool? Verified,
    int? FailedAttempts,
    int Attempts,
    IReadOnlyList<ChangeAttempt> Changes,
    int ToolCalls,
    int UnauthorizedRequests,
    bool BlockSeen,
    string? BlockStrategy,
    string? FirstStrategy,
    bool? FollowedBlock,
    UsageTotals Usage,
    double LatencyMilliseconds,
    string? StoredStrategy);

/// <summary>Everything one run of the experiment produced.</summary>
public sealed record LiveExperimentResult(
    RunDescriptor Descriptor,
    LivePreregistration Design,
    string TaskSetVersion,
    LiveBudget Budget,
    DateTimeOffset StartedAt,
    IReadOnlyList<RunRecord> Learning,
    IReadOnlyList<RunRecord> Trials,
    bool Complete,
    string? StopReason,
    IReadOnlyList<int> ExcludedInstances,
    ComparisonResult Reference,
    ComparisonResult NegativeControl,
    ComparisonResult Content,
    OverallConclusion Conclusion,
    IReadOnlyList<string> ModelIds,
    int CallsWithoutUsage,
    UsageTotals Total)
{
    /// <summary>
    /// How many runs of the same provider and model the results ledger already held when this one started, or
    /// <see langword="null"/> when the run kept no ledger (a scripted or in-test run). Set by the host.
    /// </summary>
    public int? EarlierLedgerEntries { get; init; }
}

/// <summary>How to run the experiment.</summary>
public sealed class LiveExperimentOptions
{
    /// <summary>The model. Any <see cref="IChatClient"/>: a provider's, or a scripted one in the offline tests.</summary>
    public required IChatClient Model { get; init; }

    /// <summary>What the model is, for the report.</summary>
    public required RunDescriptor Descriptor { get; init; }

    /// <summary>The design. The embedded, checked-in pre-registration in every live run.</summary>
    public LivePreregistration Design { get; init; } = LivePreregistration.ReadEmbedded();

    /// <summary>The task set. <see cref="MigrationTaskSet.Current"/> in every live run; checked against <see cref="Design"/>.</summary>
    public MigrationTaskSet TaskSet { get; init; } = MigrationTaskSet.Current;

    /// <summary>The caps. <see langword="null"/> takes the pre-registered defaults.</summary>
    public LiveBudget? Budget { get; init; }

    /// <summary>Times latency and the report date. The system clock in a live run; a fake one in the tests.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The least time between two model calls, for a provider tier with a low request-per-minute limit.</summary>
    public TimeSpan MinimumCallInterval { get; init; } = TimeSpan.Zero;

    /// <summary>The longest one model call (one agent run, tool execution included) may take before its trial is recorded as errored.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Progress lines, one per run. Never carries a prompt, a message or a key.</summary>
    public TextWriter? Progress { get; init; }

    /// <summary>Called as each run is recorded, so the host can keep what was paid for even if the experiment later aborts.</summary>
    public Action<RunRecord>? OnRunRecorded { get; init; }
}

/// <summary>The harness observed something that makes its numbers meaningless, and stopped.</summary>
public sealed class HarnessIntegrityException(string message) : Exception(message);

/// <summary>
/// The live-model reuse experiment: a learning phase that produces real Experience Records from the model's own
/// verified runs, then four evaluation conditions per instance, then one evaluation of the pre-registered gate for
/// each of the three comparisons.
/// </summary>
/// <remarks>
/// <para>
/// This is story 4.4's methodology with the scripted policy replaced by a real model. What 4.4 could only simulate is
/// measured here: whether a model, handed a Historical Reference block that the library's own pipeline built from
/// that model's earlier verified run, changes what it does -- and whether it changes it just as readily when the
/// block is wrong, which is what the negative control is for.
/// </para>
/// <para>
/// Every trial is built fresh -- container, capture, environment, agent -- and trials never write records, so every
/// trial of a condition faces exactly the same stored experience. The two stores are written only by the learning
/// phase, and only from runs the verification aggregator verified.
/// </para>
/// </remarks>
public static class LiveReuseExperiment
{
    public const string LearnHidden = "learn-current";
    public const string LearnStale = "learn-stale";
    public const string CheckId = "migration-live";
    public const string ArtifactRevision = "migration-runbook@rev-1";
    public const string EvidenceProducer = "live-reuse-harness";

    /// <summary>The system instructions. Identical in every condition and every phase.</summary>
    public const string Instructions =
        "You are the on-call database operations agent for an e-commerce platform. You carry out approved change tickets "
        + "with the tools you are given. Work autonomously: no human is available to answer questions during this session. "
        + "A ticket is done only when its migration is live.";

    /// <summary>What the harness says after a failed attempt. Identical in every condition and every phase.</summary>
    public const string FollowUp = "The migration is not live yet. Keep working on the ticket.";

    public static DateTimeOffset LearningInstant { get; } = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset TrialInstant { get; } = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly AuthorizationContext Authorization = new(
        TenantId: "live-reuse",
        PrincipalId: "live-reuse-harness",
        Roles: ["experience:read", "experience:write"],
        IssuedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly EnvironmentFingerprint HarnessEnvironment = new(
        HostName: "live-reuse-host",
        RuntimeVersion: "net10.0",
        OperatingSystem: "live-reuse-os",
        ApplicationVersion: "1.0.0-experiment",
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["Fixture"] = "simulated-database" });

    private static readonly RequiredCheck[] RequiredChecks = [new RequiredCheck(CheckId, "ToolExitCode")];

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "service", "migration", "strategy" },
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

    /// <summary>The scope one service's records live in. One per service, so a trial can only retrieve its own.</summary>
    public static Scope ScopeFor(MigrationInstance instance) => new("live-reuse", "migration-desk", instance.Service);

    /// <summary>The four evaluation conditions for instance <paramref name="index"/>, in the pre-registered order.</summary>
    public static IReadOnlyList<string> ConditionOrder(LivePreregistration design, int index)
    {
        ArgumentNullException.ThrowIfNull(design);
        string[] conditions = [design.ControlLabel, design.TreatmentLabel, design.NegativeControlLabel, design.PlaceboLabel];
        var shift = index % conditions.Length;
        return [.. conditions.Skip(shift), .. conditions.Take(shift)];
    }

    public static async Task<LiveExperimentResult> RunAsync(LiveExperimentOptions options, CancellationToken cancellationToken = default)
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

        var currentStore = new InMemoryRecordStore();
        var staleStore = new InMemoryRecordStore();

        var learning = new List<RunRecord>();
        var trials = new List<RunRecord>();
        string? stopReason = null;
        var sequence = 0;

        // ---- Learning phase: two memory-disabled runs per instance, each finalized into its own store. --------------
        foreach (var instance in taskSet.Instances)
        {
            foreach (var (label, accepted, store) in new[]
            {
                (LearnHidden, instance.HiddenStrategy, currentStore),
                (LearnStale, instance.StaleStrategy, staleStore),
            })
            {
                if (stopReason is not null)
                {
                    break;
                }

                var record = await RunOneAsync(
                    options, design, metered, ++sequence, "learning", instance, label, accepted,
                    instance.LearningText, instance.LearningMigration, design.LearningAttemptLimit,
                    memory: null, finalizeInto: store, cancellationToken).ConfigureAwait(false);

                learning.Add(record);
                Report(options, record);
                options.OnRunRecorded?.Invoke(record);
                if (record.Status == RunStatus.BudgetExhausted)
                {
                    stopReason = "the budget cap was reached during the learning phase (" + record.Classification + ")";
                }
            }
        }

        // ---- Evaluation phase: four conditions per instance, in the pre-registered rotated order. -----------------
        if (stopReason is null)
        {
            foreach (var instance in taskSet.Instances)
            {
                foreach (var condition in ConditionOrder(design, instance.Index))
                {
                    if (stopReason is not null)
                    {
                        break;
                    }

                    // The placebo injects the very record memory-enabled injects, with the story 6.2 allowlist off: the
                    // block is there, its Approach: line names the tools but not the strategy.
                    var memory = condition == design.TreatmentLabel || condition == design.PlaceboLabel ? currentStore
                        : condition == design.NegativeControlLabel ? staleStore
                        : null;

                    var record = await RunOneAsync(
                        options, design, metered, ++sequence, "evaluation", instance, condition, instance.HiddenStrategy,
                        instance.EvaluationText, instance.EvaluationMigration, design.EvaluationAttemptLimit,
                        memory, finalizeInto: null, cancellationToken, showStrategy: condition != design.PlaceboLabel).ConfigureAwait(false);

                    trials.Add(record);
                    Report(options, record);
                    options.OnRunRecorded?.Invoke(record);
                    if (record.Status == RunStatus.BudgetExhausted)
                    {
                        stopReason = "the budget cap was reached during the evaluation phase (" + record.Classification + ")";
                    }
                }
            }
        }

        const int Conditions = 4;
        var complete = stopReason is null && trials.Count == taskSet.Instances.Count * Conditions;
        RequireConditionsDifferOnlyByInjection(design, learning, trials);

        // Complete cases: an instance counts only if all four of its evaluation trials completed.
        var byInstance = trials.GroupBy(trial => trial.Instance).ToDictionary(group => group.Key, group => group.ToList());
        var included = taskSet.Instances
            .Where(instance => byInstance.TryGetValue(instance.Index, out var runs) && runs.Count == Conditions && runs.All(run => run.Status == RunStatus.Completed))
            .Select(instance => instance.Index)
            .ToList();
        var excluded = complete
            ? taskSet.Instances.Select(instance => instance.Index).Except(included).Order().ToList()
            : [];

        List<PairedObservation> Pairs(string treatment, string control) => [.. included.Select(index =>
        {
            var runs = byInstance[index];
            var t = runs.Single(run => run.Condition == treatment);
            var c = runs.Single(run => run.Condition == control);
            return new PairedObservation(index, t.FailedAttempts!.Value, c.FailedAttempts!.Value, t.Verified!.Value, c.Verified!.Value, t.UnauthorizedRequests, c.UnauthorizedRequests);
        })];

        var reference = LiveGate.Evaluate(design.TreatmentLabel, design.ControlLabel, Pairs(design.TreatmentLabel, design.ControlLabel), excluded.Count, design, complete);
        var negative = LiveGate.Evaluate(design.NegativeControlLabel, design.ControlLabel, Pairs(design.NegativeControlLabel, design.ControlLabel), excluded.Count, design, complete);
        var content = LiveGate.Evaluate(design.TreatmentLabel, design.PlaceboLabel, Pairs(design.TreatmentLabel, design.PlaceboLabel), excluded.Count, design, complete);

        return new LiveExperimentResult(
            options.Descriptor,
            design,
            taskSet.Version,
            budget,
            startedAt,
            learning,
            trials,
            complete,
            stopReason,
            excluded,
            reference,
            negative,
            content,
            LiveGate.Conclude(reference.Verdict, content.Verdict, negative.Verdict),
            [.. metered.ModelIds],
            metered.CallsWithoutUsage,
            metered.Totals);
    }

    /// <summary>
    /// The conditions must differ by what was injected and by nothing else. A memory-disabled trial or a learning run
    /// that saw a block, or a memory-enabled or negative-control trial whose instance has a stored record and which
    /// saw none, means the harness did not run the design it reports.
    /// </summary>
    private static void RequireConditionsDifferOnlyByInjection(LivePreregistration design, IReadOnlyList<RunRecord> learning, IReadOnlyList<RunRecord> trials)
    {
        foreach (var run in learning.Concat(trials.Where(trial => trial.Condition == design.ControlLabel)))
        {
            if (run.BlockSeen)
            {
                throw new HarnessIntegrityException($"Run {run.Sequence} ({run.Phase}, {run.Condition}) has memory disabled and yet its model was shown a Historical Reference block.");
            }
        }

        foreach (var trial in trials.Where(trial => trial.Condition != design.ControlLabel && trial.Status == RunStatus.Completed))
        {
            var learnedFrom = trial.Condition == design.NegativeControlLabel ? LearnStale : LearnHidden;
            var stored = learning.SingleOrDefault(run => run.Instance == trial.Instance && run.Condition == learnedFrom)?.StoredStrategy;
            if (stored is not null && !trial.BlockSeen)
            {
                throw new HarnessIntegrityException($"Trial {trial.Sequence} ({trial.Condition}) has a stored record to retrieve and its model was shown no block.");
            }

            if (stored is null && trial.BlockSeen)
            {
                throw new HarnessIntegrityException($"Trial {trial.Sequence} ({trial.Condition}) was shown a block although its learning run stored no record.");
            }

            // Content, not only presence: the block must name exactly what its store holds, and the placebo's none.
            var expected = trial.Condition == design.PlaceboLabel ? null : stored;
            if (trial.BlockSeen && trial.BlockStrategy != expected)
            {
                throw new HarnessIntegrityException(
                    $"Trial {trial.Sequence} ({trial.Condition}) was shown a block naming '{trial.BlockStrategy ?? "no strategy"}'; its store holds '{expected ?? "no strategy on the line"}'.");
            }
        }
    }

    private static void Report(LiveExperimentOptions options, RunRecord record)
    {
        options.Progress?.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[{0,3}] {1,-10} {2,-18} {3,-16} {4}{5}",
            record.Sequence,
            record.Phase,
            record.Service,
            record.Condition,
            record.Status == RunStatus.Completed
                ? (record.Verified == true ? "live" : "NOT live") + " after " + record.FailedAttempts + " failed attempt(s)"
                : record.Status + ": " + record.Classification,
            record.StoredStrategy is { } stored ? "; stored a record naming " + stored : string.Empty));
    }

    private static async Task<RunRecord> RunOneAsync(
        LiveExperimentOptions options,
        LivePreregistration design,
        MeteredChatClient metered,
        int sequence,
        string phase,
        MigrationInstance instance,
        string condition,
        string acceptedStrategy,
        string taskText,
        string migration,
        int attemptLimit,
        InMemoryRecordStore? memory,
        InMemoryRecordStore? finalizeInto,
        CancellationToken cancellationToken,
        bool showStrategy = true)
    {
        var ids = new RunIdentities(phase + "/" + condition, instance.Index);
        var frozen = new FrozenClock(phase == "learning" ? LearningInstant : TrialInstant);
        var scope = ScopeFor(instance);
        var taskId = instance.Service + "/" + (phase == "learning" ? "learning" : "ticket");

        await using var provider = BuildContainer(frozen, memory ?? finalizeInto ?? new InMemoryRecordStore());

        var capture = provider.GetRequiredService<IExperienceCaptureService>();
        var environment = new MigrationEnvironment(instance, acceptedStrategy, migration);
        var evidence = new List<Evidence>();
        var log = new WorkLog();
        var toolCalls = 0;
        var attempts = 0;

        metered.ResetObservation();
        var usageBefore = metered.Totals;
        var started = options.Clock.GetTimestamp();

        var status = RunStatus.Completed;
        string? classification = null;

        var opened = capture.StartRun(ids.RunId, taskId, taskText, scope, HarnessEnvironment,
            new Provenance("AgentExperience.LiveReuse", "1.0.0", frozen.GetUtcNow(), CorrelationId: taskId), frozen.GetUtcNow());
        if (opened.Outcome != StartRunOutcome.Started)
        {
            throw new HarnessIntegrityException($"StartRun returned {opened.Outcome} for run {sequence}.");
        }

        try
        {
            var recorder = new TurnRecorder(frozen, ids.Next);
            var chatAgent = new ChatClientAgent(metered, new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = Instructions,
                    Tools = environment.Tools,
                    Temperature = design.Temperature,
                    Seed = design.Seed,
                },
                AIContextProviders = memory is null
                    ? []
                    : [new ExperienceContextProvider(
                        provider.GetRequiredService<ExperienceRetrievalService>(),
                        provider.GetRequiredService<IExperienceRecordStore>(),
                        new ExperienceInjectionOptions
                        {
                            ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, scope, taskText, CorrelationId: taskId),
                            Limits = ExperienceInjectionLimits.Default with { EligibilityCheckTimeout = TimeSpan.FromSeconds(15) },
                            TimeProvider = frozen,

                            // Story 6.2: the one argument that distinguishes the approaches, allowlisted so the
                            // block's own Approach: line carries the working strategy.
                            ApproachArguments = showStrategy
                                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [MigrationEnvironment.ApplyToolName] = ["strategy"] }
                                : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                        })],
            });

            // A call to a tool that does not exist must not be answered inside the loop either: that would replay the
            // call to the model (see WorkLog). The loop returns it instead, and the harness logs it as text.
            var invoker = chatAgent.ChatClient.GetService<FunctionInvokingChatClient>()
                ?? throw new HarnessIntegrityException("The agent's chat client has no function-invocation layer to configure.");
            invoker.TerminateOnUnknownCalls = true;

            var agent = chatAgent.AsBuilder().Use(recorder.InvokeAsync).Build();

            for (var attempt = 1; attempt <= attemptLimit; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                environment.BeginTurn();
                recorder.BeginAttempt();
                attempts = attempt;

                // One attempt is one or more model calls, each a fresh agent run over the task text and the work log.
                // Every tool call ends its agent run (TurnRecorder), so a function call is never replayed to the model:
                // the log carries the tools' outputs as text. The attempt ends at its first apply_migration call, at a
                // reply with no tool call, or at the per-attempt tool-call limit.
                var unknownThisAttempt = 0;
                while (true)
                {
                    var before = recorder.Calls.Count;
                    var response = await RunStepAsync(agent, WorkLog.Messages(taskText, log), options, cancellationToken).ConfigureAwait(false);
                    var made = recorder.Calls.Skip(before).ToList();
                    foreach (var call in made)
                    {
                        log.AddCall(call);
                    }

                    // With a call to a nonexistent tool in the batch, the loop runs none of the batch and returns it.
                    var unknown = UnansweredCalls(response);
                    var known = environment.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var call in unknown)
                    {
                        log.AddUnansweredCall(call, known.Contains(call.Name));
                    }

                    unknownThisAttempt += unknown.Count;

                    if (made.Count == 0 && unknown.Count == 0)
                    {
                        log.AddReply(response.Text);
                        break;
                    }

                    if (environment.Attempts.Any(change => change.Turn == attempt) || recorder.Calls.Count + unknownThisAttempt >= design.ToolCallsPerAttempt)
                    {
                        break;
                    }
                }

                toolCalls += recorder.Calls.Count + unknownThisAttempt;

                var live = environment.IsLive;
                var change = environment.Attempts.LastOrDefault(made => made.Turn == attempt);
                var appended = await capture.AppendAttemptAsync(
                    ids.RunId,
                    new AppendAttemptRequest(
                        AttemptId: ids.Next(),
                        StartedAt: frozen.GetUtcNow(),
                        Duration: TimeSpan.Zero,
                        ToolCalls: [.. recorder.Calls],
                        Result: live ? "the migration is live" : null,
                        Error: live
                            ? null
                            : change is null
                                ? "the attempt made no apply_migration call; the migration is not live."
                                : string.Format(CultureInfo.InvariantCulture, "apply_migration exited {0}; the migration is not live.", change.ExitCode)),
                    cancellationToken).ConfigureAwait(false);

                if (appended.Outcome != AppendAttemptOutcome.Recorded)
                {
                    throw new HarnessIntegrityException($"AppendAttemptAsync returned {appended.Outcome} on run {sequence}.");
                }

                // The verifier reads the database's state. A not-live attempt's evidence goes to a round that is never
                // closed; the live attempt's to the run's closed round (a Fail dominates a later Pass inside one round).
                evidence.Add(TaskCheckEvaluators.ExitCode(
                    ids.Next(),
                    CheckId,
                    live ? ids.ClosedRoundId : ids.OpenRoundId,
                    ArtifactRevision,
                    EvidenceProducer,
                    frozen.GetUtcNow(),
                    live ? 0 : 1));

                if (live)
                {
                    break;
                }

                log.AddFollowUp();
            }
        }
        catch (BudgetExhaustedException ex)
        {
            status = RunStatus.BudgetExhausted;
            classification = ex.Message;
        }
        catch (AttemptTimeoutException)
        {
            status = RunStatus.Errored;
            classification = "attempt timed out after " + options.CallTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s";
        }
        catch (Exception ex) when (ex is not HarnessIntegrityException && !cancellationToken.IsCancellationRequested)
        {
            // Any failure that is not the caller cancelling the whole run -- an SDK network timeout surfaces as a
            // TaskCanceledException with our token untouched -- is recorded against this trial, not allowed to abort a
            // paid run.
            status = RunStatus.Errored;
            classification = Classify(ex);
        }

        var latency = options.Clock.GetElapsedTime(started).TotalMilliseconds;
        var usage = metered.Totals.Minus(usageBefore);
        var blockStrategy = BlockStrategy(metered.FirstBlockSeen);
        var firstStrategy = environment.Attempts.FirstOrDefault()?.Strategy;

        bool? verified = null;
        int? failedAttempts = null;
        string? storedStrategy = null;

        if (status == RunStatus.Completed)
        {
            var completed = await capture.CompleteRunAsync(ids.RunId, ids.Next(), RunExecutionStatus.Completed, frozen.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (completed.Outcome != CompleteRunOutcome.Recorded || !capture.TryGetRun(ids.RunId, out var run) || run is null)
            {
                throw new HarnessIntegrityException($"Run {sequence} could not be completed and read back from capture.");
            }

            var verdict = VerificationAggregator.Aggregate(
                ids.RunId, evidence, RequiredChecks, new ClosedVerificationRound(ids.ClosedRoundId, ArtifactRevision), ArtifactRevision, frozen.GetUtcNow(), cancellationToken);
            verified = verdict.Outcome.Status == TaskVerificationStatus.Verified;

            // Two readings of one fact -- the database's state -- through two paths. They must agree.
            if (verified != environment.IsLive)
            {
                throw new HarnessIntegrityException($"Run {sequence}: the verification aggregator says {verdict.Outcome.Status} and the database says live={environment.IsLive}.");
            }

            failedAttempts = run.Attempts.Count(attempt => attempt.Error is not null);

            if (finalizeInto is not null && verified == true)
            {
                storedStrategy = await FinalizeAsync(provider, ids, evidence, frozen, finalizeInto, scope, sequence, cancellationToken).ConfigureAwait(false);
            }
        }

        return new RunRecord(
            sequence,
            phase,
            instance.Index,
            instance.Service,
            condition,
            acceptedStrategy,
            status,
            classification,
            verified,
            failedAttempts,
            attempts,
            [.. environment.Attempts],
            toolCalls,
            environment.UnauthorizedRequests,
            metered.AnyBlockSeen,
            blockStrategy,
            firstStrategy,
            blockStrategy is null ? null : firstStrategy == blockStrategy,
            usage,
            latency,
            storedStrategy);
    }

    /// <summary>
    /// The exception's type and, for an HTTP failure, its status code; an AggregateException is unwrapped to its first
    /// inner exception. Never the message: a provider's error body is not ours to publish.
    /// </summary>
    internal static string Classify(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception is ClientResultException { Status: > 0 } http
            ? exception.GetType().Name + " (HTTP " + http.Status.ToString(CultureInfo.InvariantCulture) + ")"
            : exception.GetType().Name;
    }

    /// <summary>Function calls in a response that no tool answered (a batch with a call to a tool that does not exist).</summary>
    private static List<FunctionCallContent> UnansweredCalls(AgentResponse response)
    {
        var contents = response.Messages.SelectMany(message => message.Contents).ToList();
        var answered = contents.OfType<FunctionResultContent>().Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        return [.. contents.OfType<FunctionCallContent>().Where(call => !answered.Contains(call.CallId))];
    }

    private static async Task<AgentResponse> RunStepAsync(AIAgent agent, IReadOnlyList<ChatMessage> messages, LiveExperimentOptions options, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.CallTimeout, options.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await agent.RunAsync(messages, cancellationToken: linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AttemptTimeoutException();
        }
    }

    /// <summary>
    /// Finalizes a verified learning run into its store, reads the record back, and returns the strategy its final
    /// attempt's <c>apply_migration</c> call carries as stored -- the value the injected Approach: line will show.
    /// </summary>
    private static async Task<string> FinalizeAsync(
        IServiceProvider provider,
        RunIdentities ids,
        IReadOnlyList<Evidence> evidence,
        FrozenClock clock,
        InMemoryRecordStore store,
        Scope scope,
        int sequence,
        CancellationToken cancellationToken)
    {
        var finalized = await provider.GetRequiredService<ExperienceFinalizationService>().FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: ids.RunId,
                Authorization: Authorization,
                ClosedRound: new ClosedVerificationRound(ids.ClosedRoundId, ArtifactRevision),
                RequiredChecks: RequiredChecks,
                Evidence: evidence,
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: clock.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        if (finalized.Outcome != FinalizationOutcome.Validated || finalized.Record is null)
        {
            throw new HarnessIntegrityException($"Run {sequence} verified but finalization returned {finalized.Outcome} at stage {finalized.Stage}.");
        }

        var readBack = await store.GetAsync(Authorization, scope, finalized.Record.ExperienceId, cancellationToken).ConfigureAwait(false);
        if (readBack.Outcome != ExperienceStoreOutcome.Found || readBack.Record is null)
        {
            throw new HarnessIntegrityException($"Run {sequence} was finalized but its record could not be read back ({readBack.Outcome}).");
        }

        var final = readBack.Record.Attempts.MaxBy(attempt => attempt.SequenceNumber);
        // The last apply_migration call that carried a strategy: an earlier one in the same attempt may have been a call
        // whose arguments did not bind, which carries none.
        var strategy = final?.ToolCalls
            .OrderBy(toolCall => toolCall.SequenceNumber)
            .Where(toolCall => toolCall.ToolName == MigrationEnvironment.ApplyToolName)
            .Select(toolCall => toolCall.Arguments.TryGetValue("strategy", out var value) ? TextOf(value) : null)
            .LastOrDefault(value => !string.IsNullOrEmpty(value));

        return strategy ?? throw new HarnessIntegrityException($"Run {sequence}'s stored record has no apply_migration strategy on its final attempt.");
    }

    /// <summary>The strategy on the Approach: line of the block the model was shown, read out of the text itself.</summary>
    internal static string? BlockStrategy(string? block)
    {
        if (block is null)
        {
            return null;
        }

        var line = block.Split('\n').FirstOrDefault(text => text.StartsWith("Approach:", StringComparison.Ordinal));
        return RolloutStrategies.FirstNamedIn(line);
    }

    internal static string? TextOf(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static ServiceProvider BuildContainer(FrozenClock clock, InMemoryRecordStore records)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IExperienceRecordStore>(records);
        services.AddSingleton<IExperienceCandidateSource>(new InMemoryCandidateSource(records));
        services.AddAgentExperienceCore(Sanitization, Limits);
        services.AddAgentExperienceRetrieval(RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(15) });
        return services.BuildServiceProvider();
    }

    private sealed class AttemptTimeoutException : Exception;
}

/// <summary>
/// The conversation the model sees, as text: every tool call it made with the tool's output verbatim, every reply it
/// gave without calling a tool, and the harness's fixed follow-up after each failed attempt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why text, not replayed function calls.</b> Gemini 3 models attach a thought signature to each function call and
/// require it back when the call is replayed; Microsoft.Extensions.AI.OpenAI 10.10 does not round-trip it through the
/// OpenAI-compatible endpoint. Rather than depend on a provider-specific field, the harness never replays a function
/// call: each model call is a fresh request carrying this log. Both providers, and every condition, see exactly the
/// same shape.
/// </para>
/// <para>
/// The log is built from what the tools returned and what the model said, never from the task set, and it is identical
/// in form across conditions: the conditions differ only by the Historical Reference the context provider adds.
/// </para>
/// </remarks>
internal sealed class WorkLog
{
    public const string Heading = "Work log for this ticket so far (tool outputs verbatim):";
    public const string Continue = "Continue with the ticket.";
    private const int MaxReplyLength = 300;

    private readonly List<string> _entries = [];
    private bool _followUpPending;

    public IReadOnlyList<string> Entries => _entries;

    public static IReadOnlyList<ChatMessage> Messages(string taskText, WorkLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log._entries.Count == 0)
        {
            return [new ChatMessage(ChatRole.User, taskText)];
        }

        var text = new StringBuilder(Heading).Append('\n');
        for (var index = 0; index < log._entries.Count; index++)
        {
            text.Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(log._entries[index]).Append('\n');
        }

        text.Append(log._followUpPending ? LiveReuseExperiment.FollowUp : Continue);
        return [new ChatMessage(ChatRole.User, taskText), new ChatMessage(ChatRole.User, text.ToString())];
    }

    public void AddCall(RawToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var arguments = string.Join(", ", call.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=\"" + OneLine(LiveReuseExperiment.TextOf(pair.Value) ?? string.Empty, 200) + "\""));
        _entries.Add($"You called {call.ToolName}({arguments}) -> {OneLine(call.Result ?? call.Error ?? "(no output)", 600)}");
        _followUpPending = false;
    }

    public void AddUnansweredCall(FunctionCallContent call, bool toolExists)
    {
        ArgumentNullException.ThrowIfNull(call);
        var name = OneLine(call.Name ?? string.Empty, 100);
        _entries.Add(toolExists
            ? $"You called {name}(...) -> not run, because the same response also called a tool that does not exist; nothing was changed"
            : $"You called {name}(...) -> no tool named \"{name}\" exists; nothing was run");
        _followUpPending = false;
    }

    public void AddReply(string? text)
    {
        _entries.Add($"You replied without calling a tool: \"{OneLine(string.IsNullOrWhiteSpace(text) ? "(empty)" : text, MaxReplyLength)}\"");
        _followUpPending = false;
    }

    public void AddFollowUp() => _followUpPending = true;

    private static string OneLine(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}

/// <summary>
/// MAF function middleware: records every tool call of the current attempt for capture, and ends the agent run after
/// every tool call, so the function-invocation loop never sends a function result back to the model (see
/// <see cref="WorkLog"/>).
/// </summary>
internal sealed class TurnRecorder(TimeProvider clock, Func<Guid> newId)
{
    private readonly List<RawToolCall> _calls = [];

    /// <summary>The current attempt's tool calls, in order.</summary>
    public IReadOnlyList<RawToolCall> Calls => _calls;

    public void BeginAttempt() => _calls.Clear();

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var startedAt = clock.GetUtcNow();
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (context.Arguments is { } raw)
        {
            foreach (var (key, value) in raw)
            {
                // A provider hands arguments back as JSON; capture stores plain values.
                arguments[key] = LiveReuseExperiment.TextOf(value);
            }
        }

        object? result;
        string? error = null;
        try
        {
            result = await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing or mistyped argument makes the tool throw. Rethrowing would make the function-invocation loop
            // send an error result back to the model -- a replayed call -- and ignore Terminate. It is answered here
            // instead, as text the work log carries, and nothing was run.
            error = ex.GetType().Name;
            result = "exit=" + MigrationEnvironment.ExitInvalidCall.ToString(CultureInfo.InvariantCulture)
                + " the call was rejected (" + error + "): check the arguments; nothing was run";
        }

        _calls.Add(new RawToolCall(newId(), context.Function?.Name ?? string.Empty, arguments, startedAt, TimeSpan.Zero, LiveReuseExperiment.TextOf(result), error));

        // End the agent run after the LAST call of the model's response, so every call it batched is run (a second
        // apply_migration is refused by the database) and none is left unanswered; the loop then never goes back to
        // the model with a function result.
        if (context.FunctionCallIndex >= context.FunctionCount - 1)
        {
            context.Terminate = true;
        }

        return result;
    }
}

/// <summary>Deterministic identifiers per run, derived from the phase, condition and instance.</summary>
internal sealed class RunIdentities
{
    private readonly string _label;
    private readonly int _index;
    private int _issued;

    public RunIdentities(string label, int index)
    {
        _label = label;
        _index = index;
        RunId = Derive("run", 0);
        OpenRoundId = Derive("open-round", 0);
        ClosedRoundId = Derive("closed-round", 0);
    }

    public Guid RunId { get; }

    public Guid OpenRoundId { get; }

    public Guid ClosedRoundId { get; }

    public Guid Next() => Derive("sequence", Interlocked.Increment(ref _issued));

    private Guid Derive(string purpose, int ordinal)
    {
        var material = string.Format(CultureInfo.InvariantCulture, "AgentExperience.LiveReuse|{0}|{1}|{2}|{3}", _label, _index, purpose, ordinal);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

/// <summary>Every reading is the same instant, so record timestamps and recency are identical across conditions.</summary>
internal sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
{
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => instant;

    public override long GetTimestamp() => instant.UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, dueTime, period);
}
