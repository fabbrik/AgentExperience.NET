using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.InMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The receiving agent, as <c>options.json</c> of an inject-options case declares it: .NET's
/// <see cref="ReceivingAgentCapabilities"/>, with <see cref="AvailableTools"/> <see langword="null"/> for no tool check.
/// </summary>
internal sealed record ReceivingAgentInput(
    IReadOnlyList<string>? AvailableTools,
    ToolRiskClass? MaxRiskClass,
    IReadOnlyDictionary<string, ToolRiskClass> ToolRiskClasses,
    bool UseRunTools,
    int GatedRetrievalWindowMultiplier);

/// <summary>What the host's <see cref="ExperienceInjectionOptions.DecideInjection"/> answers for one record.</summary>
internal sealed record HostDecisionInput(bool Permitted, string? Reason);

/// <summary>
/// <c>options.json</c> of an inject-options case: the injection options whose effect is deterministic, the resolved
/// request's own limit (<see langword="null"/> for none), the receiving agent (<see langword="null"/> for none), the names
/// of the tools the invocation offers (<see cref="RunTools"/>, <see langword="null"/> for none), the host's decision per
/// record (<see cref="HostDecisions"/>: a record not listed is permitted; <see langword="null"/>: no decision callback)
/// and the benefit policy (<see cref="PreferMeasuredHelpful"/>; the policy is on, over <c>estimate.json</c>, exactly when
/// the case has that file).
/// </summary>
internal sealed record InjectOptionsInput(
    string Description,
    HistoricalReferenceRendering Rendering,
    AttemptFailureDetail FailureDetail,
    HistoricalReferenceMessageRole MessageRole,
    int MaxRecords,
    int MaxBytes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ApproachArguments,
    ModelAuthoredLessonPolicy ModelAuthoredLessons,
    int? RequestLimit,
    ReceivingAgentInput? ReceivingAgent,
    IReadOnlyList<string>? RunTools,
    IReadOnlyDictionary<Guid, HostDecisionInput>? HostDecisions,
    bool PreferMeasuredHelpful)
{
    /// <summary>The library's defaults, no request limit, no receiving agent and no host decision.</summary>
    public static InjectOptionsInput Default { get; } = new(
        string.Empty,
        HistoricalReferenceRendering.Compact,
        AttemptFailureDetail.ErrorClass,
        HistoricalReferenceMessageRole.User,
        ExperienceInjectionLimits.DefaultMaxRecords,
        ExperienceInjectionLimits.DefaultMaxBytes,
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        ModelAuthoredLessonPolicy.Include,
        RequestLimit: null,
        ReceivingAgent: null,
        RunTools: null,
        HostDecisions: null,
        PreferMeasuredHelpful: false);
}

/// <summary>
/// An inject-options case: what the candidate source answers (each record with its relevance, best first), the options
/// and, for a case with the benefit policy on, the estimate the provider's cache holds.
/// </summary>
internal sealed record InjectOptionsCase(IReadOnlyList<ExperienceRecord> Candidates, InjectOptionsInput Options, ExperienceBenefitEstimate? Estimate = null);

/// <summary>What the .NET agent did with an inject-options case.</summary>
internal sealed record InjectOptionsRun(
    IReadOnlyList<(ExperienceRecord Record, RankingInput Ranking)> Ranked,
    RetrieveExperienceRequest Request,
    bool QueryExcludedModelAuthored,
    ExperienceInjectionResult Result,
    ChatMessage? Block,
    List<(Guid ExperienceId, bool ModelAuthored)> Decisions);

/// <summary>
/// Contract 1.13's <c>inject-options</c> family: the injection options whose effect is deterministic. Each case runs a
/// real <see cref="ExperienceContextProvider"/> on a <see cref="ChatClientAgent"/>: retrieval over a candidate source that
/// answers with the case's records, the final re-read through the in-memory record store, the host's decision from the
/// options, and the writer. <c>records.json</c> is what retrieval ranked (the input a port selects from); the rest is what
/// the provider reported (<see cref="ExperienceInjectionOptions.OnContextInjected"/>) and the message the model was sent.
/// Needs no database.
/// </summary>
internal static class InjectOptionsContract
{
    public const string Family = "inject-options";

    private const int F = 0xe1;

    private const string TaskText = "Refund ticket 46 is stuck";

    /// <summary>The host's risk classes for the tools the records call; <c>drop_ledger</c> is undeclared, so Critical.</summary>
    internal static readonly IReadOnlyDictionary<string, ToolRiskClass> RiskClasses = new Dictionary<string, ToolRiskClass>(StringComparer.Ordinal)
    {
        ["read_ledger"] = ToolRiskClass.Low,
        ["wait_for_lock"] = ToolRiskClass.Low,
        ["retry_refund"] = ToolRiskClass.Medium,
        ["issue_refund"] = ToolRiskClass.High,
    };

    // Each record's verified approach (its final attempt) calls the tools named.
    private static readonly ExperienceRecord Read = Lesson(1, "read-ledger", "Read the ledger before touching a refund.", [["read_ledger"]]);
    private static readonly ExperienceRecord Retry = Retried(2);
    private static readonly ExperienceRecord Issue = Lesson(3, "issue-refund", "Issue the refund once the ledger agrees.", [["read_ledger", "issue_refund"]]);
    private static readonly ExperienceRecord Drop = Lesson(4, "drop-ledger", "Drop the stale ledger and rebuild it.", [["drop_ledger"]]);
    private static readonly ExperienceRecord Think = Lesson(5, "explain-refund", "Explain the refund policy; no tool is needed.", [[]]);
    private static readonly ExperienceRecord Earlier = Lesson(6, "rebuild-ledger", "Rebuilding the ledger failed; reading it was enough.", [["drop_ledger"], ["read_ledger"]], failFirst: true);
    private static readonly ExperienceRecord Model = Lesson(7, "model-refund", "A model wrote this lesson about the ledger.", [["read_ledger"]], modelAuthored: true);
    private static readonly ExperienceRecord ModelDrop = Lesson(8, "model-drop", "A model wrote this lesson about dropping the ledger.", [["drop_ledger"]], modelAuthored: true);

    // Records with no approach to gate: not verified, a final attempt that failed, and attempt numbers that repeat.
    private static readonly ExperienceRecord Unverified = Lesson(9, "unverified-drop", "Dropping the ledger did not verify.", [["drop_ledger"]]) is var u
        ? u with { Outcome = u.Outcome with { Status = TaskVerificationStatus.Failed } }
        : null!;
    private static readonly ExperienceRecord FailedLast = Lesson(10, "failed-last", "The last attempt to drop the ledger failed.", [["read_ledger"], ["drop_ledger"]], failLast: true);
    private static readonly ExperienceRecord Repeated = Lesson(11, "repeated-attempts", "Two attempts share a number.", [["read_ledger"], ["drop_ledger"]]) is var r
        ? r with { Attempts = [.. r.Attempts.Select(attempt => attempt with { SequenceNumber = 0 })] }
        : null!;

    // Twenty-one calls: the approach is cut to the first twenty, so the twenty-first tool is never checked.
    private static readonly ExperienceRecord Long = Lesson(12, "long-approach", "Read the ledger page by page.", [[.. Enumerable.Repeat("read_ledger", 20), "drop_ledger"]]);

    // A recorded call whose tool name is blank.
    private static readonly ExperienceRecord Blank = Lesson(13, "blank-tool", "A call whose tool name was not recorded.", [["read_ledger", " "]]);

    /// <summary>Every inject-options case, by directory name.</summary>
    public static IReadOnlyList<(string Name, InjectOptionsCase Case)> Cases { get; } = Build();

    private static ReceivingAgentInput Agent(ToolRiskClass? max = null, IReadOnlyList<string>? available = null, bool useRunTools = false, int multiplier = 4) =>
        new(available, max, RiskClasses, useRunTools, multiplier);

    private static List<(string Name, InjectOptionsCase Case)> Build()
    {
        ExperienceRecord[] all = [Read, Retry, Issue, Drop, Think];
        var o = InjectOptionsInput.Default;

        // The size of the block holding Read and Think alone, as the provider writes it.
        var two = Run(new InjectOptionsCase([Read, Think], o)).Result.PayloadBytes;
        var hurts = new BenefitSelectContract.RunBuilder(F + 1);
        hurts.Add(3, HoldoutArm.Treatment, verified: false, null, null, Read.ExperienceId);
        hurts.Add(3, HoldoutArm.Holdout, verified: true, null, null, Read.ExperienceId);
        hurts.Add(3, HoldoutArm.Treatment, verified: true, null, null, Think.ExperienceId);
        hurts.Add(3, HoldoutArm.Holdout, verified: false, null, null, Think.ExperienceId);
        return
        [
            ("risk-low", new(all, o with
            {
                Description = "MaxRiskClass Low: an approach calling a Medium, a High or an undeclared (Critical) tool is omitted as RiskClassExceeded, with no detail, and takes no slot.",
                ReceivingAgent = Agent(ToolRiskClass.Low),
            })),
            ("risk-medium", new(all, o with
            {
                Description = "MaxRiskClass Medium: Low and Medium approaches pass; High and undeclared ones are omitted.",
                ReceivingAgent = Agent(ToolRiskClass.Medium),
            })),
            ("risk-high", new(all, o with
            {
                Description = "MaxRiskClass High: only the approach calling an undeclared tool, which counts as Critical, is omitted.",
                ReceivingAgent = Agent(ToolRiskClass.High),
            })),
            ("risk-critical", new(all, o with
            {
                Description = "MaxRiskClass Critical disables the risk check: with no tool check either the gate can reject nothing, so nothing is omitted and retrieval's window is not widened.",
                ReceivingAgent = Agent(ToolRiskClass.Critical),
                RequestLimit = 3,
            })),
            ("tool-unavailable", new([Read, Retry, Issue, Earlier, Think], o with
            {
                Description = "AvailableTools without retry_refund and issue_refund: approaches calling either are omitted as ToolUnavailable. Only the verified final attempt's tools are checked, so a failed earlier attempt's tool (drop_ledger) does not gate, and an approach calling no tool always passes.",
                ReceivingAgent = Agent(available: ["read_ledger", "wait_for_lock"]),
            })),
            ("tool-check-before-risk", new([Read, Retry, Issue, Drop], o with
            {
                Description = "Both checks: the tool check comes first, so an approach whose tool is missing is ToolUnavailable even when its risk would also fail, and an available tool above MaxRiskClass is RiskClassExceeded.",
                ReceivingAgent = Agent(ToolRiskClass.Low, available: ["read_ledger", "wait_for_lock", "retry_refund"]),
            })),
            ("no-tools-available", new(all, o with
            {
                Description = "An empty AvailableTools admits only records whose approach calls no tool.",
                ReceivingAgent = Agent(available: []),
            })),
            ("run-tools", new(all, o with
            {
                Description = "UseRunTools: the tool check uses the names of the tools the invocation offers (a blank name ignored), intersected with AvailableTools, so issue_refund, offered but not declared, is unavailable, and so is wait_for_lock, declared but not offered.",
                ReceivingAgent = Agent(available: ["read_ledger", "wait_for_lock", "retry_refund"], useRunTools: true),
                RunTools = ["read_ledger", "retry_refund", "issue_refund", " "],
            })),
            ("run-tools-none-offered", new(all, o with
            {
                Description = "UseRunTools with no AvailableTools, on an invocation that offers no tool: only records whose approach calls no tool pass.",
                ReceivingAgent = Agent(useRunTools: true),
                RunTools = [],
            })),
            ("blank-recorded-tool", new([Blank, Read], o with
            {
                Description = "A recorded call whose tool name is blank is always unavailable to the tool check (a declared tool name can never be blank).",
                ReceivingAgent = Agent(available: ["read_ledger"]),
            })),
            ("blank-recorded-tool-risk", new([Blank, Read], o with
            {
                Description = "A recorded call whose tool name is blank is Critical to the risk check.",
                ReceivingAgent = Agent(ToolRiskClass.High),
            })),
            ("no-approach-passes", new([Unverified, FailedLast, Repeated, Drop], o with
            {
                Description = "A record with no approach has nothing to gate and passes: one that did not verify, one whose final attempt failed, and one whose attempt numbers repeat. A verified approach calling an unavailable tool is still omitted.",
                ReceivingAgent = Agent(available: ["read_ledger"]),
            })),
            ("approach-cut-at-twenty", new([Long, Drop], o with
            {
                Description = "The approach checked is the one the Tried: line shows, cut to twenty calls, so a twenty-first call's unavailable tool does not gate.",
                ReceivingAgent = Agent(available: ["read_ledger"]),
            })),
            ("gate-takes-no-slot", new([Issue, Read, Drop, Retry, Think, Earlier], o with
            {
                Description = "The gate runs before the record limit: with MaxRecords 2, the gated records take no slot, the next eligible records fill them, and the record after that is OverRecordLimit, its detail naming its rank.",
                MaxRecords = 2,
                ReceivingAgent = Agent(ToolRiskClass.Low),
            })),
            ("gate-and-byte-budget", new([Issue, Read, Think, Earlier], o with
            {
                Description = "The gate's omissions come first, then the writer's byte budget: a budget that fits two records drops the third passing record whole as OverByteBudget.",
                MaxBytes = two + 1,
                ReceivingAgent = Agent(ToolRiskClass.Low),
            })),
            ("request-limit-gated-window", new([Issue, Read, Retry, Think, Earlier], o with
            {
                Description = "A request limit of 2 with a gate that can reject: retrieval is asked for MaxRecords x GatedRetrievalWindowMultiplier (3 x 2 = 6) records, capped by the candidate limit. A gated record inside the host's window is reported; one past it was fetched only as backfill, so it is not, and neither is a backfill record no slot was left for.",
                MaxRecords = 3,
                RequestLimit = 2,
                ReceivingAgent = Agent(ToolRiskClass.Low, multiplier: 2),
            })),
            ("gate-and-measured-harmful", new([Read, Issue, Think, Retry], o with
            {
                Description = "The benefit policy with a gate: a lesson measured Hurts is MeasuredHarmful before the gate is asked, then the gate omits, and the lesson measured Helps moves first with PreferMeasuredHelpful.",
                PreferMeasuredHelpful = true,
                ReceivingAgent = Agent(ToolRiskClass.Low),
            }, hurts.Estimate(splitByModel: false))),
            ("everything-gated", new([Retry, Issue, Drop], o with
            {
                Description = "Every record is gated: nothing is injected, the outcome is NothingToInject and every omission is reported.",
                ReceivingAgent = Agent(ToolRiskClass.Low),
            })),
            ("model-authored-include", new([Model, Read], o with
            {
                Description = "ModelAuthoredLessons Include (the default): the model-authored record is injected, its model-written fields between the two fixed authored lines.",
            })),
            ("model-authored-exclude", new([Model, Read, ModelDrop, Think], o with
            {
                Description = "ModelAuthoredLessons Exclude: retrieval is asked to exclude model-authored records and leaves out any the source still returns, so they are not among the ranked records and take no slot.",
                MaxRecords = 2,
                ModelAuthoredLessons = ModelAuthoredLessonPolicy.Exclude,
            })),
            ("host-decision", new([Read, Model, Issue, Retry, Think, Earlier], o with
            {
                Description = "The host's decision, asked once per selected record in order, after the gate: a denial with a reason omits it as HostDenied with that reason, one with none with the default detail, and a denied record keeps its slot. The decision is told whether the record is model-authored. The byte budget comes after: a budget that fits two records drops the next one whole.",
                MaxRecords = 5,
                MaxBytes = two + 1,
                HostDecisions = new Dictionary<Guid, HostDecisionInput>
                {
                    [Retry.ExperienceId] = new(false, "risk policy: no retries"),
                    [Model.ExperienceId] = new(false, null),
                },
                ReceivingAgent = Agent(ToolRiskClass.Medium),
            })),
            ("approach-arguments", new([Retry], o with
            {
                Description = "ApproachArguments allowlists retry_refund's delay: the Tried: lines show its stored values, and nothing else.",
                ApproachArguments = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["retry_refund"] = ["delay"] },
            })),
            ("failure-detail-error-class", new([Retry], o with
            {
                Description = "FailureDetail ErrorClass (the default): a failed attempt shows the error's class, and the failed call its first token.",
            })),
            ("failure-detail-excerpt", new([Retry], o with
            {
                Description = "FailureDetail Excerpt: the failed attempt adds the error's first line, quoted; the failed call's marker is still the class's first token.",
                FailureDetail = AttemptFailureDetail.Excerpt,
            })),
            ("failure-detail-none", new([Retry], o with
            {
                Description = "FailureDetail None: a failed attempt says only failed, and a failed call [failed].",
                FailureDetail = AttemptFailureDetail.None,
            })),
            ("message-role-system-verbose", new([Read, Drop, Retry], o with
            {
                Description = "MessageRole System with the verbose layout and a gate: the same block text, sent as a system message.",
                Rendering = HistoricalReferenceRendering.Verbose,
                MessageRole = HistoricalReferenceMessageRole.System,
                ReceivingAgent = Agent(ToolRiskClass.Medium),
            })),
        ];
    }

    /// <summary>A verified lesson whose attempts call <paramref name="attempts"/>' tools in order, the last the approach.</summary>
    internal static ExperienceRecord Lesson(
        int n,
        string taskId,
        string lesson,
        string[][] attempts,
        bool modelAuthored = false,
        bool failFirst = false,
        int family = F,
        Scope? scope = null,
        bool failLast = false)
    {
        var record = SearchContract.Seed(family, n, taskId, "Refund ticket handling", lesson, modelAuthored: modelAuthored, scope: scope);
        var at = record.CreatedAt.AddMinutes(-10);
        var list = new List<Attempt>();
        for (var a = 0; a < attempts.Length; a++)
        {
            var error = (failFirst && a == 0) || (failLast && a == attempts.Length - 1)
                ? "System.InvalidOperationException: the ledger is locked"
                : null;
            var calls = attempts[a]
                .Select((tool, c) => Call(family, n, (a * 32) + c, c, tool, at.AddMinutes(a).AddSeconds(c), new(), error))
                .ToList();
            list.Add(new Attempt(Fixed.Id(family, 0x300 + (n * 16) + a), a, at.AddMinutes(a), TimeSpan.FromSeconds(3), calls, error is null ? "ok" : null, error));
        }

        return record with { Attempts = list };
    }

    /// <summary>The retry lesson: a first attempt that timed out retrying at once, and a second that waited for the lock.</summary>
    internal static ExperienceRecord Retried(int n, int family = F, Scope? scope = null)
    {
        var record = SearchContract.Seed(family, n, "retry-refund", "Refund ticket handling", "Wait for the refund lock before retrying.", scope: scope);
        var at = record.CreatedAt.AddMinutes(-10);
        const string Timeout = "System.TimeoutException: the refund lock was held by worker 7 (exit 2)\n   at Refunds.Retry()";
        Attempt[] attempts =
        [
            new(Fixed.Id(family, 0x300 + (n * 16)), 0, at, TimeSpan.FromSeconds(4),
            [
                Call(family, n, 0, 0, "read_ledger", at, new(), error: null),
                Call(family, n, 1, 1, "retry_refund", at.AddSeconds(1), new() { ["delay"] = 0, ["ticket"] = "T-46" }, Timeout),
            ],
            Result: null,
            Error: Timeout),
            new(Fixed.Id(family, 0x300 + (n * 16) + 1), 1, at.AddMinutes(1), TimeSpan.FromSeconds(4),
            [
                Call(family, n, 32, 0, "wait_for_lock", at.AddMinutes(1), new(), error: null),
                Call(family, n, 33, 1, "retry_refund", at.AddMinutes(1).AddSeconds(1), new() { ["delay"] = 30, ["ticket"] = "T-46" }, error: null),
            ],
            Result: "Refund issued.",
            Error: null),
        ];

        return record with { Attempts = attempts };
    }

    private static ToolCallRecord Call(int family, int n, int id, int sequence, string name, DateTimeOffset at, Dictionary<string, object?> arguments, string? error) =>
        new(Fixed.Id(family, 0x1000 + (n * 64) + id), sequence, name, arguments, at, TimeSpan.FromMilliseconds(500), error is null ? "ok" : null, error);

    /// <summary>
    /// Runs a case through a real <see cref="ExperienceContextProvider"/> on a <see cref="ChatClientAgent"/>: the records
    /// stored in the in-memory record store (for the final re-read), retrieval over a source that answers with them at
    /// relevance 1.00, 0.95, 0.90, ..., the host's resolver returning a request with the case's limit, the case's options,
    /// and, with an estimate, the benefit policy on over a cache that holds it.
    /// </summary>
    public static InjectOptionsRun Run(InjectOptionsCase definition)
    {
        var options = definition.Options;
        var clock = new FixedTimeProvider(Fixed.T0);
        var store = new InMemoryExperienceRecordStore(clock);
        foreach (var record in definition.Candidates)
        {
            var created = store.CreateAsync(Fixed.Authorization, record, CancellationToken.None).GetAwaiter().GetResult();
            if (created.Outcome != ExperienceStoreOutcome.Created)
            {
                throw new InvalidOperationException($"{record.TaskId} was not stored: {created.Outcome}.");
            }
        }

        var source = new FixtureSource([.. definition.Candidates.Select((record, index) => (record, 1.0 - (0.05 * index)))]);
        var retrieval = new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, clock);
        var request = new RetrieveExperienceRequest(Fixed.Authorization, Fixed.Scope, TaskText, Limit: options.RequestLimit);
        var decisions = new List<(Guid, bool)>();
        ExperienceInjectionResult? result = null;
        var injection = Options(options, _ => request, clock);
        injection.OnContextInjected = injected => result = injected;
        if (options.HostDecisions is { } host)
        {
            injection.DecideInjection = decision =>
            {
                decisions.Add((decision.Current.ExperienceId, decision.ModelAuthored));
                return host.TryGetValue(decision.Current.ExperienceId, out var answer)
                    ? new InjectionDecision(answer.Permitted, answer.Reason)
                    : InjectionDecision.Permit;
            };
        }

        if (definition.Estimate is { } estimate)
        {
            injection.Benefit = new ExperienceBenefitPolicy
            {
                Enabled = true,
                PreferMeasuredHelpful = options.PreferMeasuredHelpful,
                MinimumRunsPerArm = estimate.MinimumRunsPerArm,
            };
        }

        var provider = new ExperienceContextProvider(retrieval, store, injection);
        if (definition.Estimate is { } held)
        {
            provider.BenefitCache!.Prime(Fixed.Authorization, Fixed.Scope, held);
        }

        var runTools = options.RunTools?.Select(name => (AITool)new NamedTool(name)).ToList();
        var client = new RecordingChatClient();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            ChatOptions = runTools is null ? null : new ChatOptions { Tools = runTools },
            AIContextProviders = [provider],
        });
        agent.RunAsync(TaskText).GetAwaiter().GetResult();
        if (result is null)
        {
            throw new InvalidOperationException("The provider reported no result.");
        }

        // The ranked records retrieval answered the provider with: the same source and request, asked again.
        var asked = provider.RetrievalRequest(request, runTools);
        var ranked = retrieval.RetrieveAsync(asked, CancellationToken.None).GetAwaiter().GetResult().Records
            .Select(entry => (entry.Record, new RankingInput(
                entry.Score,
                [.. entry.Components.Select(component => new RankingComponentInput(component.Kind, component.Value, component.Weight))])))
            .ToList();
        var block = client.Seen.SingleOrDefault(message => message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true);
        return new InjectOptionsRun(ranked, asked, source.Query?.ExcludeModelAuthored ?? false, result, block, decisions);
    }

    /// <summary>The .NET injection options a case declares, with <paramref name="resolve"/> as the host's resolver.</summary>
    internal static ExperienceInjectionOptions Options(InjectOptionsInput options, Func<ExperienceInjectionContext, RetrieveExperienceRequest?> resolve, TimeProvider clock) => new()
    {
        ResolveRequest = resolve,
        Limits = new ExperienceInjectionLimits(options.MaxRecords, options.MaxBytes),
        SessionLimits = null,
        ApproachArguments = new Dictionary<string, IReadOnlyList<string>>(options.ApproachArguments, StringComparer.Ordinal),
        FailureDetail = options.FailureDetail,
        Rendering = options.Rendering,
        MessageRole = options.MessageRole,
        ModelAuthoredLessons = options.ModelAuthoredLessons,
        ReceivingAgent = options.ReceivingAgent is { } agent
            ? new ReceivingAgentCapabilities
            {
                AvailableTools = agent.AvailableTools?.ToHashSet(StringComparer.Ordinal),
                MaxRiskClass = agent.MaxRiskClass,
                ToolRiskClasses = new Dictionary<string, ToolRiskClass>(agent.ToolRiskClasses, StringComparer.Ordinal),
                UseRunTools = agent.UseRunTools,
                GatedRetrievalWindowMultiplier = agent.GatedRetrievalWindowMultiplier,
            }
            : null,
        TimeProvider = clock,
    };

    /// <summary>
    /// <c>expected.json</c>: the <c>outcome</c>; the <c>messageRole</c> the block was sent in (<c>null</c> when nothing was
    /// injected); the <c>retrievalRequest</c> (the <c>limit</c> retrieval was asked for, <c>null</c> for none, and whether it
    /// excluded model-authored records); the host's <c>decisions</c>, in the order it was asked (each <c>experienceId</c>
    /// and whether the provider counts it <c>modelAuthored</c>); the records <c>injected</c>, in order; the block's
    /// <c>byteCount</c>; and every omission in the order the provider reports it (<c>experienceId</c>, <c>reason</c>,
    /// <c>detail</c>, <c>null</c> when it carries none). The block itself is <c>expected-block.txt</c>, empty when nothing
    /// was injected.
    /// </summary>
    public static JsonObject ExpectedJson(InjectOptionsRun run) => new()
    {
        ["outcome"] = run.Result.Outcome.ToString(),
        ["messageRole"] = run.Block is null ? null : (run.Block.Role == ChatRole.System ? "System" : "User"),
        ["retrievalRequest"] = new JsonObject
        {
            ["limit"] = run.Request.Limit,
            ["excludeModelAuthored"] = run.QueryExcludedModelAuthored,
        },
        ["decisions"] = new JsonArray([.. run.Decisions.Select(entry => (JsonNode)new JsonObject
        {
            ["experienceId"] = Id(entry.ExperienceId),
            ["modelAuthored"] = entry.ModelAuthored,
        })]),
        ["injected"] = new JsonArray([.. run.Result.InjectedExperienceIds.Select(id => (JsonNode)Id(id))]),
        ["byteCount"] = run.Result.PayloadBytes,
        ["omitted"] = new JsonArray([.. run.Result.Omitted.Select(entry => (JsonNode)new JsonObject
        {
            ["experienceId"] = Id(entry.ExperienceId),
            ["reason"] = entry.Reason.ToString(),
            ["detail"] = entry.Detail,
        })]),
    };

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>A tool that is only a name, as the invocation hands it to the provider.</summary>
    private sealed class NamedTool(string name) : AITool
    {
        public override string Name => name;
    }

    /// <summary>A candidate source that answers with the given records, in order, at most the limit it is asked for.</summary>
    private sealed class FixtureSource(IReadOnlyList<(ExperienceRecord Record, double Relevance)> candidates) : IExperienceCandidateSource
    {
        public ExperienceCandidateQuery? Query { get; private set; }

        public Task<ExperienceCandidateSearchResult> SearchAsync(AuthorizationContext authorization, ExperienceCandidateQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            IReadOnlyList<ExperienceCandidate> answer = [.. candidates.Take(query.Limit).Select(entry => new ExperienceCandidate(entry.Record, entry.Relevance))];
            return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, answer, []));
        }
    }

    /// <summary>A model that answers at once, and keeps every message it was sent.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
