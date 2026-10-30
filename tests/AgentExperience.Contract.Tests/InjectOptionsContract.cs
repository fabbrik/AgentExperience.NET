using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.InMemory;
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

/// <summary>
/// <c>options.json</c> of an inject-options case: the injection options whose effect is deterministic, the resolved
/// request's own limit (<see langword="null"/> for none), the receiving agent (<see langword="null"/> for none) and the
/// names of the tools the invocation offers (<see cref="RunTools"/>, read only with
/// <see cref="ReceivingAgentInput.UseRunTools"/>).
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
    IReadOnlyList<string>? RunTools)
{
    /// <summary>The library's defaults, no request limit and no receiving agent.</summary>
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
        RunTools: null);
}

/// <summary>An inject-options case: the ranked records (best first) and the options.</summary>
internal sealed record InjectOptionsCase(IReadOnlyList<(ExperienceRecord Record, RankingInput Ranking)> Records, InjectOptionsInput Options);

/// <summary>What the .NET context provider does with an inject-options case.</summary>
internal sealed record InjectOptionsRun(
    RetrieveExperienceRequest Request,
    List<Guid> Selected,
    HistoricalReferencePayload Payload,
    HistoricalReferenceMessageRole Role,
    List<OmittedExperience> Omitted)
{
    /// <summary><see cref="InjectionOutcome.Injected"/> when the block holds a record, otherwise <see cref="InjectionOutcome.NothingToInject"/>.</summary>
    public InjectionOutcome Outcome => Payload.IsEmpty ? InjectionOutcome.NothingToInject : InjectionOutcome.Injected;
}

/// <summary>
/// Contract 1.13's <c>inject-options</c> family: the injection options whose effect is deterministic. The ranked records,
/// the options and the receiving agent go in; the request retrieval is asked for, what the context provider selects (the
/// model-authored policy, the capability gate, the record limit), the block its writer renders (layout, failure detail,
/// argument allowlist, byte budget), the role it is sent in, every omission with its reason and detail, and the outcome
/// come out. Generated through <c>ExperienceContextProvider.SelectAndWrite</c>, the selection and rendering an
/// invocation runs. Needs no database.
/// </summary>
internal static class InjectOptionsContract
{
    public const string Family = "inject-options";

    private const int F = 0xe1;

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

    /// <summary>Every inject-options case, by directory name.</summary>
    public static IReadOnlyList<(string Name, InjectOptionsCase Case)> Cases { get; } = Build();

    private static ReceivingAgentInput Agent(ToolRiskClass? max = null, IReadOnlyList<string>? available = null, bool useRunTools = false, int multiplier = 4) =>
        new(available, max, RiskClasses, useRunTools, multiplier);

    private static List<(string Name, InjectOptionsCase Case)> Build()
    {
        var all = Ranked(Read, Retry, Issue, Drop, Think);
        var o = InjectOptionsInput.Default;
        var budget = InjectContract.BudgetFor(Ranked(Read, Think), InjectOptions.Default) + 1;
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
            ("tool-unavailable", new(Ranked(Read, Retry, Issue, Earlier, Think), o with
            {
                Description = "AvailableTools without retry_refund and issue_refund: approaches calling either are omitted as ToolUnavailable. Only the verified final attempt's tools are checked, so a failed earlier attempt's tool (drop_ledger) does not gate, and an approach calling no tool always passes.",
                ReceivingAgent = Agent(available: ["read_ledger", "wait_for_lock"]),
            })),
            ("tool-check-before-risk", new(Ranked(Read, Retry, Issue, Drop), o with
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
            ("gate-takes-no-slot", new(Ranked(Issue, Read, Drop, Retry, Think, Earlier), o with
            {
                Description = "The gate runs before the record limit: with MaxRecords 2, the gated records take no slot, the next eligible records fill them, and the record after that is OverRecordLimit, its detail naming its rank.",
                MaxRecords = 2,
                ReceivingAgent = Agent(ToolRiskClass.Low),
            })),
            ("gate-and-byte-budget", new(Ranked(Issue, Read, Think, Earlier), o with
            {
                Description = "The gate's omissions come first, then the writer's byte budget: a budget that fits two records drops the third passing record whole as OverByteBudget.",
                MaxBytes = budget,
                ReceivingAgent = Agent(ToolRiskClass.Low),
            })),
            ("request-limit-gated-window", new(Ranked(Issue, Read, Retry, Think, Earlier), o with
            {
                Description = "A request limit of 2 with a gate that can reject: retrieval is asked for MaxRecords x GatedRetrievalWindowMultiplier (3 x 2 = 6) records, capped by the candidate limit. A gated record inside the host's window is reported; one past it was fetched only as backfill, so it is not, and neither is a backfill record no slot was left for.",
                MaxRecords = 3,
                RequestLimit = 2,
                ReceivingAgent = Agent(ToolRiskClass.Low, multiplier: 2),
            })),
            ("everything-gated", new(Ranked(Retry, Issue, Drop), o with
            {
                Description = "Every record is gated: nothing is injected, the outcome is NothingToInject and every omission is reported.",
                ReceivingAgent = Agent(ToolRiskClass.Low),
            })),
            ("model-authored-include", new(Ranked(Model, Read), o with
            {
                Description = "ModelAuthoredLessons Include (the default): the model-authored record is injected, its model-written fields between the two fixed authored lines.",
            })),
            ("model-authored-exclude", new(Ranked(Model, Read, ModelDrop, Think), o with
            {
                Description = "ModelAuthoredLessons Exclude: retrieval is asked to exclude model-authored records, and any that arrives is omitted as ModelAuthored, with no detail, before the record limit, so it takes no slot.",
                MaxRecords = 2,
                ModelAuthoredLessons = ModelAuthoredLessonPolicy.Exclude,
            })),
            ("model-authored-before-the-gate", new(Ranked(ModelDrop, Drop, Read), o with
            {
                Description = "Exclude with a gate: the model-authored exclusion is decided before the gate, so a model-authored record whose approach would also be gated is ModelAuthored.",
                ModelAuthoredLessons = ModelAuthoredLessonPolicy.Exclude,
                ReceivingAgent = Agent(ToolRiskClass.Low),
            })),
            ("approach-arguments", new(Ranked(Retry), o with
            {
                Description = "ApproachArguments allowlists retry_refund's delay: the Tried: lines show its stored values, and nothing else.",
                ApproachArguments = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["retry_refund"] = ["delay"] },
            })),
            ("failure-detail-error-class", new(Ranked(Retry), o with
            {
                Description = "FailureDetail ErrorClass (the default): a failed attempt shows the error's class, and the failed call its first token.",
            })),
            ("failure-detail-excerpt", new(Ranked(Retry), o with
            {
                Description = "FailureDetail Excerpt: the failed attempt adds the error's first line, quoted; the failed call's marker is still the class's first token.",
                FailureDetail = AttemptFailureDetail.Excerpt,
            })),
            ("failure-detail-none", new(Ranked(Retry), o with
            {
                Description = "FailureDetail None: a failed attempt says only failed, and a failed call [failed].",
                FailureDetail = AttemptFailureDetail.None,
            })),
            ("message-role-system-verbose", new(Ranked(Read, Drop, Retry), o with
            {
                Description = "MessageRole System with the verbose layout and a gate: the same block text, sent as a system message.",
                Rendering = HistoricalReferenceRendering.Verbose,
                MessageRole = HistoricalReferenceMessageRole.System,
                ReceivingAgent = Agent(ToolRiskClass.Medium),
            })),
        ];
    }

    private static List<(ExperienceRecord Record, RankingInput Ranking)> Ranked(params ExperienceRecord[] records) =>
        [.. records.Select((record, index) => (record, InjectContract.Ranking(1.0 - (0.05 * index), 0.9)))];

    /// <summary>A verified lesson whose attempts call <paramref name="attempts"/>' tools in order, the last the approach.</summary>
    private static ExperienceRecord Lesson(int n, string taskId, string lesson, string[][] attempts, bool modelAuthored = false, bool failFirst = false)
    {
        var record = SearchContract.Seed(F, n, taskId, "Refund ticket handling", lesson, modelAuthored: modelAuthored);
        var at = record.CreatedAt.AddMinutes(-10);
        var list = new List<Attempt>();
        for (var a = 0; a < attempts.Length; a++)
        {
            var error = failFirst && a == 0 ? "System.InvalidOperationException: the ledger is locked" : null;
            var calls = attempts[a]
                .Select((tool, c) => Call(n, (a * 16) + c, c, tool, at.AddMinutes(a).AddSeconds(c), new(), error))
                .ToList();
            list.Add(new Attempt(Fixed.Id(F, 0x300 + (n * 16) + a), a, at.AddMinutes(a), TimeSpan.FromSeconds(3), calls, error is null ? "ok" : null, error));
        }

        return record with { Attempts = list };
    }

    /// <summary>The retry lesson: a first attempt that timed out retrying at once, and a second that waited for the lock.</summary>
    private static ExperienceRecord Retried(int n)
    {
        var record = SearchContract.Seed(F, n, "retry-refund", "Refund ticket handling", "Wait for the refund lock before retrying.");
        var at = record.CreatedAt.AddMinutes(-10);
        const string Timeout = "System.TimeoutException: the refund lock was held by worker 7 (exit 2)\n   at Refunds.Retry()";
        Attempt[] attempts =
        [
            new(Fixed.Id(F, 0x300 + (n * 16)), 0, at, TimeSpan.FromSeconds(4),
            [
                Call(n, 0, 0, "read_ledger", at, new(), error: null),
                Call(n, 1, 1, "retry_refund", at.AddSeconds(1), new() { ["delay"] = 0, ["ticket"] = "T-46" }, Timeout),
            ],
            Result: null,
            Error: Timeout),
            new(Fixed.Id(F, 0x300 + (n * 16) + 1), 1, at.AddMinutes(1), TimeSpan.FromSeconds(4),
            [
                Call(n, 16, 0, "wait_for_lock", at.AddMinutes(1), new(), error: null),
                Call(n, 17, 1, "retry_refund", at.AddMinutes(1).AddSeconds(1), new() { ["delay"] = 30, ["ticket"] = "T-46" }, error: null),
            ],
            Result: "Refund issued.",
            Error: null),
        ];

        return record with { Attempts = attempts };
    }

    private static ToolCallRecord Call(int n, int id, int sequence, string name, DateTimeOffset at, Dictionary<string, object?> arguments, string? error) =>
        new(Fixed.Id(F, 0x1000 + (n * 64) + id), sequence, name, arguments, at, TimeSpan.FromMilliseconds(500), error is null ? "ok" : null, error);

    /// <summary>
    /// What the .NET context provider does with a case: the provider is built with the case's options (a host resolver
    /// whose request carries the case's limit), retrieval is asked for <c>RetrievalRequest</c>'s request, and the
    /// selection and the block are <c>SelectAndWrite</c>'s, for an invocation offering the case's run tools.
    /// </summary>
    public static InjectOptionsRun Run(IReadOnlyList<(ExperienceRecord Record, RankingInput Ranking)> records, InjectOptionsInput options)
    {
        var clock = new FixedTimeProvider(Fixed.T0);
        var store = new InMemoryExperienceRecordStore(clock);
        var request = new RetrieveExperienceRequest(Fixed.Authorization, Fixed.Scope, "Refund ticket 46 is stuck", Limit: options.RequestLimit);
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(new InMemoryExperienceCandidateSource(store), RetrievalPolicy.Default, RankingWeights.Default, clock),
            store,
            Options(options, _ => request, clock));

        var runTools = options.RunTools?.Select(name => (AITool)new NamedTool(name)).ToList();
        var ranked = records
            .Select(entry => new RankedExperience(
                entry.Record,
                entry.Ranking.Score,
                [.. entry.Ranking.Components.Select(component => new RankingComponent(component.Kind, component.Value, component.Weight))]))
            .ToList();
        var omitted = new List<OmittedExperience>();
        var asked = provider.RetrievalRequest(request, runTools);
        // As an invocation selects: under the resolved request's own limit, never the widened one retrieval is asked for.
        var (selected, payload, role) = provider.SelectAndWrite(ranked, omitted, request.Limit, runTools);
        return new InjectOptionsRun(asked, [.. selected.Select(entry => entry.Record.ExperienceId)], payload, role, omitted);
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
    /// <c>expected.json</c>: the <c>outcome</c>; the <c>messageRole</c> the block is sent in (<c>null</c> when nothing is
    /// injected); the <c>retrievalRequest</c> (its <c>limit</c>, <c>null</c> for none, and whether it excludes
    /// model-authored records); the records <c>selected</c> before the byte budget and <c>injected</c>, in order; the
    /// block's <c>byteCount</c>; and every omission in the order the provider reports it (<c>experienceId</c>,
    /// <c>reason</c>, <c>detail</c>, <c>null</c> when it carries none). The block itself is <c>expected-block.txt</c>,
    /// empty when nothing is injected.
    /// </summary>
    public static JsonObject ExpectedJson(InjectOptionsRun run) => new()
    {
        ["outcome"] = run.Outcome.ToString(),
        ["messageRole"] = run.Payload.IsEmpty ? null : run.Role.ToString(),
        ["retrievalRequest"] = new JsonObject
        {
            ["limit"] = run.Request.Limit,
            ["excludeModelAuthored"] = run.Request.ExcludeModelAuthored,
        },
        ["selected"] = new JsonArray([.. run.Selected.Select(id => (JsonNode)Id(id))]),
        ["injected"] = new JsonArray([.. run.Payload.ExperienceIds.Select(id => (JsonNode)Id(id))]),
        ["byteCount"] = run.Payload.IsEmpty ? 0 : run.Payload.ByteCount,
        ["omitted"] = new JsonArray([.. run.Omitted.Select(entry => (JsonNode)new JsonObject
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
}
