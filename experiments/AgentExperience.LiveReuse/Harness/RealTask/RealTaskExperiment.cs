using System.Globalization;
using System.Text.RegularExpressions;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Sanitization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.InMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// The real-task experiment (story 24.1): a model repairs a small Python service broken by a dependency upgrade, in a
/// real temporary repository with real tools, and success is the exit code of the service's own unit tests. A lesson
/// learned on one service has to be found by the library's own retrieval in a store that also holds the other services'
/// lessons, and has to cut the tool calls the model needs on the sibling service that needs the same fix.
/// </summary>
/// <remarks>
/// Every record comes from a model's own verified run, finalized through the library (capture, verification,
/// the shipped reflector, the record store). Every trial is a fresh container over its own store and a fresh temporary
/// repository. The store a service is evaluated against never holds that service's own learning record.
/// </remarks>
public static partial class RealTaskExperiment
{
    public const string LearnCondition = "learn";
    public const string CheckId = "tests-pass";
    public const string ArtifactRevision = "service-tests@rev-1";
    public const string EvidenceProducer = "live-reuse-harness";
    public const string LearningTaskSuffix = "/learning";
    public const string TicketTaskSuffix = "/ticket";

    /// <summary>The system instructions. Identical in every condition and every phase.</summary>
    public const string Instructions =
        "You are a software engineer fixing a small Python service. You work in a repository that you can reach only "
        + "through your tools, and you work autonomously: no human is available to answer questions during this session. "
        + "The tests under tests/ and the library under vendor/ are read-only; fix the service's own code under app/. "
        + "A ticket is done when run_tests exits 0.";

    /// <summary>The one scope every record and every trial shares.</summary>
    public static Scope SharedScope { get; } = new("live-reuse", "realtask-desk", "shared");

    internal static readonly RequiredCheck[] RequiredChecks = [new RequiredCheck(CheckId, "ToolExitCode")];

    internal static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        // The path of a file is kept (so a lesson can name where the fix went); a file's content is not an allowlisted field and is dropped.
        [SanitizationKinds.ToolArguments] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "path", "directory" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 10,
            MaxValueLength: 4_000,
            MaxFieldNameLength: 100),
        [SanitizationKinds.ToolResult] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 4_000,
            MaxFieldNameLength: 100),
    });

    internal static readonly CaptureLimits Limits = new(
        MaxAttemptsPerRun: 16,
        MaxToolCallsPerAttempt: 60,
        MaxResultLength: 4_000,
        MaxErrorLength: 4_000);

    internal static readonly EnvironmentFingerprint HarnessEnvironment = new(
        HostName: "live-reuse-host",
        RuntimeVersion: "net10.0",
        OperatingSystem: "live-reuse-os",
        ApplicationVersion: "1.0.0-experiment",
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["Fixture"] = "generated-python-services" });

    /// <summary>The four evaluation conditions for a service and repeat: rotated left by (index + repeat) mod 4.</summary>
    public static IReadOnlyList<string> ConditionOrder(RealTaskDesign design, int serviceIndex, int repeat)
    {
        ArgumentNullException.ThrowIfNull(design);
        string[] conditions = [design.ControlLabel, design.TreatmentLabel, design.MismatchedLabel, design.PlaceboLabel];
        var shift = (serviceIndex + repeat) % conditions.Length;
        return [.. conditions.Skip(shift), .. conditions.Take(shift)];
    }

    public static async Task<RealTaskResult> RunAsync(RealTaskOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var design = options.Design;
        var taskSet = options.TaskSet;

        // Refuse first: nothing below may call a model for a design the file does not describe.
        design.Check(taskSet);

        var budget = options.Budget ?? new LiveBudget(design.DefaultMaxModelCalls, design.DefaultMaxTotalTokens);
        var startedAt = options.Clock.GetUtcNow();
        var retrying = new RetryingChatClient(options.Model, design.RetryMaxTries, design.RetryDelaysMilliseconds, options.NoRetryDelay);
        var metered = new MeteredChatClient(retrying, budget, options.Clock, options.MinimumCallInterval);
        var context = new TrialContext(options, design, taskSet, metered);

        var master = new InMemoryExperienceRecordStore(new FrozenClock(LiveReuseExperiment.LearningInstant));
        var learning = new List<RealTaskRun>();
        var learned = new Dictionary<string, ExperienceRecord>(StringComparer.Ordinal);
        var trials = new List<RealTaskRun>();
        string? stopReason = null;
        var sequence = 0;

        // ---- Learning: one memory-disabled run per service, finalized into the master store if it verifies. -----------
        foreach (var service in taskSet.Services)
        {
            if (stopReason is not null)
            {
                break;
            }

            var wiring = new Wiring(service.Service + LearningTaskSuffix, clock => BuildContainer(clock, master), Inject: false, Finalize: true, ShowPath: true);
            var outcome = await RunTrialAsync(context, ++sequence, "learning", service, LearnCondition, 0, wiring, design.LearningToolCalls, cancellationToken).ConfigureAwait(false);

            learning.Add(outcome.Run);
            Report(options, outcome.Run);
            options.OnRunRecorded?.Invoke(outcome.Run);
            if (outcome.Run.BlockSeen)
            {
                throw new HarnessIntegrityException($"Learning run {outcome.Run.Sequence} ({service.Service}) was shown a block.");
            }

            if (outcome.Stored is { } record)
            {
                learned[service.Service] = record;
            }

            if (outcome.Run.Status == RunStatus.BudgetExhausted)
            {
                stopReason = "the budget cap was reached during the learning phase (" + outcome.Run.Classification + ")";
            }
        }

        // ---- The stores each service is evaluated against: everyone's learning record but its own (and, for the mismatched control, its sibling's). --
        var runnable = taskSet.Services.Where(service => learned.ContainsKey(taskSet.Sibling(service).Service)).ToList();
        var full = new Dictionary<string, InMemoryExperienceRecordStore>(StringComparer.Ordinal);
        var mismatched = new Dictionary<string, InMemoryExperienceRecordStore>(StringComparer.Ordinal);
        foreach (var service in runnable)
        {
            var sibling = taskSet.Sibling(service).Service;
            full[service.Service] = await CopyAsync(learned.Where(pair => pair.Key != service.Service).Select(pair => pair.Value), cancellationToken).ConfigureAwait(false);
            mismatched[service.Service] = await CopyAsync(learned.Where(pair => pair.Key != service.Service && pair.Key != sibling).Select(pair => pair.Value), cancellationToken).ConfigureAwait(false);
        }

        // ---- Evaluation: four conditions per service and repeat, in the rotated order. --------------------------------
        if (stopReason is null)
        {
            for (var repeat = 0; repeat < design.Repeats && stopReason is null; repeat++)
            {
                foreach (var service in runnable)
                {
                    foreach (var condition in ConditionOrder(design, service.Index, repeat))
                    {
                        if (stopReason is not null)
                        {
                            break;
                        }

                        var store = condition == design.MismatchedLabel ? mismatched[service.Service] : full[service.Service];
                        var wiring = new Wiring(
                            service.Service + TicketTaskSuffix,
                            clock => BuildContainer(clock, store),
                            Inject: condition != design.ControlLabel,
                            Finalize: false,
                            ShowPath: condition != design.PlaceboLabel);

                        var outcome = await RunTrialAsync(context, ++sequence, "evaluation", service, condition, repeat, wiring, design.EvaluationToolCalls, cancellationToken).ConfigureAwait(false);
                        trials.Add(outcome.Run);
                        RequireTrialIntegrity(design, outcome.Run, taskSet, service, learned);
                        Report(options, outcome.Run);
                        options.OnRunRecorded?.Invoke(outcome.Run);
                        if (outcome.Run.Status == RunStatus.BudgetExhausted)
                        {
                            stopReason = "the budget cap was reached during the evaluation phase (" + outcome.Run.Classification + ")";
                        }
                    }
                }
            }
        }

        const int Conditions = 4;
        var complete = stopReason is null && trials.Count == runnable.Count * design.Repeats * Conditions;

        var excluded = complete ? Exclusions(taskSet, learned, trials) : [];
        var excludedNames = excluded.Select(exclusion => exclusion.Service).ToHashSet(StringComparer.Ordinal);
        var included = taskSet.Services.Where(service => !excludedNames.Contains(service.Service)).Select(service => service.Index).ToList();

        var reference = Evaluate(design.TreatmentLabel, design.ControlLabel, trials, included, excluded.Count, design, complete);
        var content = Evaluate(design.TreatmentLabel, design.PlaceboLabel, trials, included, excluded.Count, design, complete);
        var control = Evaluate(design.MismatchedLabel, design.ControlLabel, trials, included, excluded.Count, design, complete);

        return new RealTaskResult(
            options.Descriptor,
            design,
            taskSet.Digest(),
            budget,
            startedAt,
            learning,
            trials,
            complete,
            stopReason,
            excluded,
            reference,
            content,
            control,
            Conclude(reference.Verdict, content.Verdict, control.Verdict),
            [.. metered.ModelIds],
            metered.CallsWithoutUsage,
            metered.Totals);
    }

    /// <summary>
    /// The pre-registered exclusion rule: a service whose sibling's learning run did not verify (so its cluster has no
    /// record) and a service any of whose trials errored are left out of every comparison.
    /// </summary>
    internal static List<RealTaskExclusion> Exclusions(RealTaskSet taskSet, IReadOnlyDictionary<string, ExperienceRecord> learned, IReadOnlyList<RealTaskRun> trials)
    {
        var excluded = new List<RealTaskExclusion>();
        foreach (var service in taskSet.Services)
        {
            var sibling = taskSet.Sibling(service).Service;
            if (!learned.ContainsKey(sibling))
            {
                excluded.Add(new RealTaskExclusion(service.Service, $"the learning run of {sibling} did not verify, so its cluster has no record and {service.Service} ran no trial"));
            }
            else if (trials.Any(trial => trial.Instance == service.Index && trial.Status != RunStatus.Completed))
            {
                excluded.Add(new RealTaskExclusion(service.Service, "a trial errored"));
            }
        }

        return excluded;
    }

    /// <summary>The service-level gate: the mean over services of each service's mean score, an exact sign test counted per service, and two guards.</summary>
    internal static RealTaskComparison Evaluate(
        string treatment,
        string control,
        IReadOnlyList<RealTaskRun> trials,
        IReadOnlyList<int> includedServices,
        int excludedServices,
        RealTaskDesign design,
        bool complete)
    {
        var included = includedServices.ToHashSet();
        var treatmentTrials = trials.Where(trial => trial.Condition == treatment && included.Contains(trial.Instance)).ToList();
        var controlTrials = trials.Where(trial => trial.Condition == control && included.Contains(trial.Instance)).ToList();

        double? Mean(IEnumerable<double> values) => values.Any() ? values.Average() : null;

        var serviceMeans = includedServices
            .Select(index => (
                Treatment: Mean(treatmentTrials.Where(trial => trial.Instance == index).Select(trial => (double)trial.Score)),
                Control: Mean(controlTrials.Where(trial => trial.Instance == index).Select(trial => (double)trial.Score))))
            .Where(pair => pair.Treatment is not null && pair.Control is not null)
            .Select(pair => (Treatment: pair.Treatment!.Value, Control: pair.Control!.Value))
            .ToList();

        var treatmentMean = Mean(serviceMeans.Select(pair => pair.Treatment));
        var controlMean = Mean(serviceMeans.Select(pair => pair.Control));
        var treatmentSuccess = Mean(treatmentTrials.Select(trial => trial.Verified == true ? 1d : 0d));
        var controlSuccess = Mean(controlTrials.Select(trial => trial.Verified == true ? 1d : 0d));
        var treatmentRefused = treatmentTrials.Sum(trial => trial.UnauthorizedRequests);
        var controlRefused = controlTrials.Sum(trial => trial.UnauthorizedRequests);

        // The trial-level reading: reported, never gated, because the repeats of one service are not independent.
        var trialPairs = treatmentTrials
            .Join(controlTrials, trial => (trial.Instance, trial.Repeat), trial => (trial.Instance, trial.Repeat), (t, c) => (Treatment: t.Score, Control: c.Score))
            .ToList();
        var trialSign = Sign(trialPairs.Select(pair => ((double)pair.Treatment, (double)pair.Control)));

        if (!complete)
        {
            return new RealTaskComparison(treatment, control, serviceMeans.Count, excludedServices, treatmentMean, controlMean, treatmentSuccess, controlSuccess,
                treatmentRefused, controlRefused, null, null, [], ComparisonVerdict.NotEvaluated);
        }

        if (excludedServices > design.MaxExcludedServices || serviceMeans.Count == 0)
        {
            return new RealTaskComparison(treatment, control, serviceMeans.Count, excludedServices, treatmentMean, controlMean, treatmentSuccess, controlSuccess,
                treatmentRefused, controlRefused, null, trialSign, [], ComparisonVerdict.Inconclusive);
        }

        var sign = Sign(serviceMeans.Select(pair => (pair.Treatment, pair.Control)));
        var alpha = design.Alpha.ToString("0.###", CultureInfo.InvariantCulture);
        var terms = new List<GateTerm>
        {
            new(
                $"mean over services of tool_calls({treatment}) < mean over services of tool_calls({control})",
                treatmentMean < controlMean,
                $"{LiveGate.Format(treatmentMean)} against {LiveGate.Format(controlMean)}"),
            new(
                $"sign_test_p(service mean tool_calls: {treatment} < {control}) <= {alpha}",
                sign.PValue <= design.Alpha,
                $"p = {sign.PValue.ToString("0.0000", CultureInfo.InvariantCulture)} ({sign.Wins} fewer, {sign.Losses} more, {sign.Ties} tied, of {serviceMeans.Count} services)"),
            new(
                $"success_rate({treatment}) >= success_rate({control})",
                treatmentSuccess >= controlSuccess,
                $"{LiveGate.Format(treatmentSuccess)} against {LiveGate.Format(controlSuccess)}"),
            new(
                $"sum(refused_writes | {treatment}) <= sum(refused_writes | {control})",
                treatmentRefused <= controlRefused,
                $"{treatmentRefused} against {controlRefused}"),
        };

        return new RealTaskComparison(treatment, control, serviceMeans.Count, excludedServices, treatmentMean, controlMean, treatmentSuccess, controlSuccess,
            treatmentRefused, controlRefused, sign, trialSign, terms,
            terms.All(term => term.Holds) ? ComparisonVerdict.BenefitDemonstrated : ComparisonVerdict.NoDemonstratedBenefit);
    }

    internal static RealTaskConclusion Conclude(ComparisonVerdict reference, ComparisonVerdict content, ComparisonVerdict control) =>
        LiveGate.Conclude(reference, content, control) switch
        {
            OverallConclusion.ReuseBenefitAttributableToContent => RealTaskConclusion.RealTaskBenefitAttributableToContent,
            OverallConclusion.BenefitNotAttributableToContent => RealTaskConclusion.RealTaskBenefitNotAttributableToContent,
            OverallConclusion.NoDemonstratedBenefit => RealTaskConclusion.RealTaskNoDemonstratedBenefit,
            OverallConclusion.Inconclusive => RealTaskConclusion.RealTaskInconclusive,
            _ => RealTaskConclusion.RealTaskNotEvaluated,
        };

    private static SignTestResult Sign(IEnumerable<(double Treatment, double Control)> pairs)
    {
        var list = pairs.ToList();
        var wins = list.Count(pair => pair.Treatment < pair.Control);
        var losses = list.Count(pair => pair.Treatment > pair.Control);
        return new SignTestResult(wins, losses, list.Count - wins - losses, SignTest.OneSidedP(wins, losses));
    }

    /// <summary>Checked as each trial ends, so a broken design stops before the rest of the budget is spent.</summary>
    internal static void RequireTrialIntegrity(RealTaskDesign design, RealTaskRun run, RealTaskSet taskSet, RealService service, IReadOnlyDictionary<string, ExperienceRecord> learned)
    {
        if (run.Condition == design.ControlLabel && run.BlockSeen)
        {
            throw new HarnessIntegrityException($"Trial {run.Sequence} ({run.Condition}) was shown a block.");
        }

        if (run.BlockRecords.Contains(service.Service + LearningTaskSuffix, StringComparer.Ordinal))
        {
            throw new HarnessIntegrityException($"Trial {run.Sequence} ({service.Service}) was shown its own service's learning record.");
        }

        var sibling = taskSet.Sibling(service).Service + LearningTaskSuffix;
        if (run.Condition == design.MismatchedLabel && run.BlockRecords.Contains(sibling, StringComparer.Ordinal))
        {
            throw new HarnessIntegrityException($"Trial {run.Sequence} (mismatched control) was shown the sibling's learning record.");
        }

        foreach (var taskId in run.BlockRecords)
        {
            var name = taskId.EndsWith(LearningTaskSuffix, StringComparison.Ordinal) ? taskId[..^LearningTaskSuffix.Length] : taskId;
            if (!learned.ContainsKey(name))
            {
                throw new HarnessIntegrityException($"Trial {run.Sequence} was shown a record '{taskId}' that no verified learning run stored.");
            }
        }

        if (run.Condition == design.PlaceboLabel && run.BlockPaths.Count > 0)
        {
            throw new HarnessIntegrityException($"Trial {run.Sequence} (placebo) was shown a file path in its block.");
        }
    }

    private static async Task<InMemoryExperienceRecordStore> CopyAsync(IEnumerable<ExperienceRecord> records, CancellationToken cancellationToken)
    {
        var store = new InMemoryExperienceRecordStore(new FrozenClock(LiveReuseExperiment.LearningInstant));
        foreach (var record in records)
        {
            var created = await store.CreateAsync(LiveReuseExperiment.Authorization, record, cancellationToken).ConfigureAwait(false);
            if (created.Outcome != ExperienceStoreOutcome.Created)
            {
                throw new HarnessIntegrityException($"Copying record '{record.TaskId}' into an evaluation store returned {created.Outcome}.");
            }
        }

        return store;
    }

    private static ServiceProvider BuildContainer(FrozenClock clock, InMemoryExperienceRecordStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IExperienceRecordStore>(store);
        services.AddSingleton<IExperienceCandidateSource>(new InMemoryExperienceCandidateSource(store));
        services.AddAgentExperienceCore(Sanitization, Limits);
        services.AddAgentExperienceRetrieval(RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(15) });
        return services.BuildServiceProvider();
    }

    private static void Report(RealTaskOptions options, RealTaskRun run) =>
        options.Progress?.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[{0,3}] {1,-10} {2,-12} {3,-9} {4,-18} r{5} {6}{7}",
            run.Sequence,
            run.Phase,
            run.Service,
            run.Cluster,
            run.Condition,
            run.Repeat,
            run.Status == RunStatus.Completed
                ? (run.Verified == true ? "green" : "NOT green") + " after " + run.ToolCalls + " tool call(s)"
                : run.Status + ": " + run.Classification,
            run.BlockSeen ? "; block of " + run.BlockRecords.Count + (run.SiblingRank is { } rank ? ", sibling rank " + rank : ", no sibling") : string.Empty));

    [GeneratedRegex(@"app/[A-Za-z0-9_./-]+", RegexOptions.CultureInvariant)]
    private static partial Regex HelperPath();

    /// <summary>Everything one run needs that does not change within an experiment.</summary>
    private sealed record TrialContext(RealTaskOptions Options, RealTaskDesign Design, RealTaskSet TaskSet, MeteredChatClient Metered);

    private sealed record Wiring(string TaskId, Func<FrozenClock, ServiceProvider> Container, bool Inject, bool Finalize, bool ShowPath);

    private sealed record TrialOutcome(RealTaskRun Run, ExperienceRecord? Stored);

    private sealed class AttemptTimeoutException : Exception;

    private static async Task<TrialOutcome> RunTrialAsync(
        TrialContext context,
        int sequence,
        string phase,
        RealService service,
        string condition,
        int repeat,
        Wiring wiring,
        int toolCallCap,
        CancellationToken cancellationToken)
    {
        var options = context.Options;
        var design = context.Design;
        var metered = context.Metered;
        var cluster = RealTaskSet.ClusterOf(service);
        var ids = new RunIdentities(phase + "/" + condition + "/" + repeat.ToString(CultureInfo.InvariantCulture), service.Index);
        var frozen = new FrozenClock(phase == "learning" ? LiveReuseExperiment.LearningInstant : LiveReuseExperiment.TrialInstant);
        var ticket = RealTaskSet.Ticket(service);
        var taskId = wiring.TaskId;

        await using var provider = wiring.Container(frozen);
        using var environment = new RealTaskEnvironment(context.TaskSet, service);

        var capture = provider.GetRequiredService<IExperienceCaptureService>();
        var evidence = new List<Evidence>();
        var log = new RealWorkLog();
        var allCalls = new List<RawToolCall>();
        var totalCalls = 0;
        var attempts = 0;

        metered.ResetObservation();
        var usageBefore = metered.Totals;
        var started = options.Clock.GetTimestamp();

        var status = RunStatus.Completed;
        string? classification = null;

        var opened = capture.StartRun(ids.RunId, taskId, ticket, SharedScope, HarnessEnvironment,
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
                    Seed = options.Descriptor.SeedSent ? design.Seed + repeat : null,
                },
                AIContextProviders = !wiring.Inject
                    ? []
                    : [new ExperienceContextProvider(
                        provider.GetRequiredService<ExperienceRetrievalService>(),
                        provider.GetRequiredService<IExperienceRecordStore>(),
                        new ExperienceInjectionOptions
                        {
                            ResolveRequest = _ => new RetrieveExperienceRequest(LiveReuseExperiment.Authorization, SharedScope, ticket, CorrelationId: taskId),
                            Limits = ExperienceInjectionLimits.Default with { EligibilityCheckTimeout = TimeSpan.FromSeconds(15) },
                            TimeProvider = frozen,

                            // The one argument that distinguishes the approaches is where the fix was written; the placebo
                            // allowlists nothing, so the same block says which tools were used and not which file.
                            ApproachArguments = wiring.ShowPath
                                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [RealTaskEnvironment.WriteToolName] = ["path"] }
                                : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                            ReceivingAgent = new ReceivingAgentCapabilities { UseRunTools = true },
                        })],
            });

            var invoker = chatAgent.ChatClient.GetService<FunctionInvokingChatClient>()
                ?? throw new HarnessIntegrityException("The agent's chat client has no function-invocation layer to configure.");
            invoker.TerminateOnUnknownCalls = true;

            var agent = chatAgent.AsBuilder().Use(recorder.InvokeAsync).Build();
            var knownTools = environment.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

            for (var attempt = 1; attempt <= design.MaxAttempts && !environment.IsGreen && totalCalls < toolCallCap; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                environment.BeginTurn();
                recorder.BeginAttempt();
                attempts = attempt;

                // One attempt is the model calls up to the first run_tests call (or a reply with no tool call, or the cap).
                var unknownThisAttempt = 0;
                while (true)
                {
                    var before = recorder.Calls.Count;
                    var response = await RunStepAsync(agent, log.Messages(ticket), options, cancellationToken).ConfigureAwait(false);
                    var made = recorder.Calls.Skip(before).ToList();
                    foreach (var call in made)
                    {
                        log.AddCall(call);
                    }

                    var unknown = UnansweredCalls(response);
                    foreach (var call in unknown)
                    {
                        log.AddUnansweredCall(call, knownTools.Contains(call.Name));
                    }

                    unknownThisAttempt += unknown.Count;
                    if (made.Count == 0 && unknown.Count == 0)
                    {
                        log.AddReply(response.Text);
                        break;
                    }

                    if (made.Any(call => call.ToolName == RealTaskEnvironment.TestToolName)
                        || totalCalls + recorder.Calls.Count + unknownThisAttempt >= toolCallCap)
                    {
                        break;
                    }
                }

                var attemptCalls = recorder.Calls.ToList();
                allCalls.AddRange(attemptCalls);
                totalCalls += attemptCalls.Count + unknownThisAttempt;

                var testRun = environment.TestRuns.LastOrDefault(run => run.Turn == attempt);
                var green = testRun is { ExitCode: 0 };
                var appended = await capture.AppendAttemptAsync(
                    ids.RunId,
                    new AppendAttemptRequest(
                        AttemptId: ids.Next(),
                        StartedAt: frozen.GetUtcNow(),
                        Duration: TimeSpan.Zero,
                        ToolCalls: [.. attemptCalls],
                        Result: green ? "the tests pass" : null,
                        Error: green
                            ? null
                            : testRun is null
                                ? "the attempt did not run the tests; the service is not fixed."
                                : string.Format(CultureInfo.InvariantCulture, "run_tests exited {0}; the tests do not pass.", testRun.ExitCode)),
                    cancellationToken).ConfigureAwait(false);
                if (appended.Outcome != AppendAttemptOutcome.Recorded)
                {
                    throw new HarnessIntegrityException($"AppendAttemptAsync returned {appended.Outcome} on run {sequence}.");
                }

                evidence.Add(TaskCheckEvaluators.ExitCode(
                    ids.Next(), CheckId, green ? ids.ClosedRoundId : ids.OpenRoundId, ArtifactRevision, EvidenceProducer, frozen.GetUtcNow(), testRun?.ExitCode ?? 1));

                if (green)
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
            classification = "model call timed out after " + options.CallTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s";
        }
        catch (Exception ex) when (ex is not HarnessIntegrityException && !cancellationToken.IsCancellationRequested)
        {
            status = RunStatus.Errored;
            classification = LiveReuseExperiment.Classify(ex);
        }

        var latency = options.Clock.GetElapsedTime(started).TotalMilliseconds;
        var usage = metered.Totals.Minus(usageBefore);
        var block = metered.FirstBlockSeen;
        var blockPaths = block is null ? [] : HelperPath().Matches(block).Select(match => match.Value).Distinct(StringComparer.Ordinal).ToList();
        var blockRecords = TransferExperiment.BlockRecords(block);
        var siblingTaskId = context.TaskSet.Sibling(service).Service + LearningTaskSuffix;
        var siblingRank = blockRecords.ToList().IndexOf(siblingTaskId) is var at and >= 0 ? at + 1 : (int?)null;

        bool? verified = null;
        ExperienceRecord? stored = null;
        IReadOnlyList<string> storedFixPaths = [];

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

            // Three readings of one fact -- the service's tests -- through three paths. They must agree.
            if (verified != environment.IsGreen)
            {
                throw new HarnessIntegrityException($"Run {sequence}: the verification aggregator says {verdict.Outcome.Status} and the last run_tests says green={environment.IsGreen}.");
            }

            // A trial ends at the first green run_tests, so the repository cannot change after it: the harness's own run must agree.
            // A service fixed without ever being confirmed green is not a verified trial (the ticket says it is done when run_tests exits 0).
            if (environment.IsGreen && environment.VerifyIndependently() != 0)
            {
                throw new HarnessIntegrityException($"Run {sequence}: the last run_tests was green and the harness's own run of the tests is not.");
            }

            if (wiring.Finalize && verified == true)
            {
                (stored, storedFixPaths) = await FinalizeAsync(provider, ids, evidence, frozen, sequence, cancellationToken).ConfigureAwait(false);
            }
        }

        var firstWrite = environment.WrittenPaths.Count > 0 ? environment.WrittenPaths[0] : null;
        var firstWriteIndex = allCalls.FindIndex(call => call.ToolName == RealTaskEnvironment.WriteToolName);
        var readsBefore = (firstWriteIndex < 0 ? allCalls : allCalls.Take(firstWriteIndex))
            .Count(call => call.ToolName is RealTaskEnvironment.ReadToolName or RealTaskEnvironment.ListToolName);

        var record = new RealTaskRun(
            sequence,
            phase,
            service.Index,
            service.Service,
            service.Cluster,
            condition,
            repeat,
            status,
            classification,
            verified,
            totalCalls,
            environment.TestRuns.Count,
            verified == true ? Math.Min(totalCalls, toolCallCap) : toolCallCap,
            [.. environment.WrittenPaths],
            readsBefore,
            firstWrite is null ? null : firstWrite == cluster.FixPath,
            environment.UnauthorizedRequests,
            metered.AnyBlockSeen,
            blockPaths,
            blockRecords,
            usage,
            latency,
            storedFixPaths)
        {
            BlockNamedClusterFix = blockPaths.Contains(cluster.FixPath, StringComparer.Ordinal),
            SiblingRank = siblingRank,
        };

        return new TrialOutcome(record, stored);
    }

    private static async Task<(ExperienceRecord Record, IReadOnlyList<string> FixPaths)> FinalizeAsync(
        IServiceProvider provider,
        RunIdentities ids,
        IReadOnlyList<Evidence> evidence,
        FrozenClock clock,
        int sequence,
        CancellationToken cancellationToken)
    {
        var finalized = await provider.GetRequiredService<ExperienceFinalizationService>().FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: ids.RunId,
                Authorization: LiveReuseExperiment.Authorization,
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

        var store = provider.GetRequiredService<IExperienceRecordStore>();
        var readBack = await store.GetAsync(LiveReuseExperiment.Authorization, SharedScope, finalized.Record.ExperienceId, cancellationToken).ConfigureAwait(false);
        if (readBack.Outcome != ExperienceStoreOutcome.Found || readBack.Record is null)
        {
            throw new HarnessIntegrityException($"Run {sequence} was finalized but its record could not be read back ({readBack.Outcome}).");
        }

        var final = readBack.Record.Attempts.MaxBy(attempt => attempt.SequenceNumber);
        List<string> paths = final is null
            ? []
            : [.. final.ToolCalls
                .OrderBy(toolCall => toolCall.SequenceNumber)
                .Where(toolCall => toolCall.ToolName == RealTaskEnvironment.WriteToolName)
                .Select(toolCall => toolCall.Arguments.TryGetValue("path", out var value) ? LiveReuseExperiment.TextOf(value) : null)
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value!)];

        return (readBack.Record, paths);
    }

    private static List<FunctionCallContent> UnansweredCalls(AgentResponse response)
    {
        var contents = response.Messages.SelectMany(message => message.Contents).ToList();
        var answered = contents.OfType<FunctionResultContent>().Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        return [.. contents.OfType<FunctionCallContent>().Where(call => !answered.Contains(call.CallId))];
    }

    private static async Task<AgentResponse> RunStepAsync(AIAgent agent, IReadOnlyList<ChatMessage> messages, RealTaskOptions options, CancellationToken cancellationToken)
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
}
