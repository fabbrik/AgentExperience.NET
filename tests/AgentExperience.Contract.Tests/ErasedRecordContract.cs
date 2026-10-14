using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Contract.Tests;

/// <summary>One step of an erased-record case.</summary>
internal abstract record ErasedStep;

/// <summary>Runs the text search (and ranking) for <paramref name="Query"/>.</summary>
internal sealed record SearchStep(SearchQuery Query) : ErasedStep;

/// <summary>Reads one record by ID in exactly <paramref name="Scope"/>.</summary>
internal sealed record GetStep(Guid ExperienceId, Scope Scope) : ErasedStep;

/// <summary>Records an injected block, as a record-injection step does.</summary>
internal sealed record RecordStep(RecordInjectionStep Record) : ErasedStep;

/// <summary>Erases a record through <c>agent_experience.purge_experience_record</c>, as the schema's owner.</summary>
internal sealed record EraseRecordStep(EraseStep Erase) : ErasedStep;

/// <summary>An erased-record case: the records seeded (as stored), the description and the steps.</summary>
internal sealed record ErasedRecordCase(string Description, IReadOnlyList<ExperienceRecord> Records, IReadOnlyList<ErasedStep> Steps);

/// <summary>
/// Contract 1.5's erased family: a record erased (tombstoned: <c>deleted_at</c> set, no payload) is never read. Running it
/// needs PostgreSQL, so the PostgreSQL contract test runs it. Each case writes <c>seed.json</c> (stored records, exactly as
/// a search seed holds them), <c>steps.json</c> (the description and the steps, in order) and <c>expected.json</c> (one
/// entry per step, <see langword="null"/> for an <c>erase</c> or a <c>record</c>, and every row of
/// <c>agent_experience.experience_injections</c> afterwards, as the record-injection family writes them).
/// </summary>
internal static class ErasedRecordContract
{
    public const string Family = "erased";

    /// <summary>Every erased-record case, by directory name.</summary>
    public static IReadOnlyList<(string Name, ErasedRecordCase Case)> Cases { get; } =
    [
        ("search-skips-an-erased-best-match", SearchSkipsAnErasedBestMatch()),
        ("get-of-an-erased-record", GetOfAnErasedRecord()),
        ("record-for-an-erased-record", RecordForAnErasedRecord()),
    ];

    private static ErasedRecordCase SearchSkipsAnErasedBestMatch()
    {
        const int f = 0x65;
        var best = SearchContract.Seed(f, 1, "refund-stuck-pending", "Refund stuck in pending after a lock timeout", "Verified after 1 attempt.", status: ExperienceStatus.Reinforced, confidence: 0.9);
        var next = SearchContract.Seed(f, 2, "refund-stuck-pending", "Refund stuck in pending", "Verified after 1 attempt.");
        var third = SearchContract.Seed(f, 3, "refund-stuck", "Refund stuck pending", "Verified after 1 attempt.", confidence: 0.5);
        var query = Query("refund stuck in pending after a lock timeout");
        return new(
            "The best match, first in both the statement's rows and the ranked order, is erased: the same search then never " +
            "returns it, and the next match takes its place, its row and score unchanged.",
            [best, next, third],
            [
                new SearchStep(query with { Description = "Before the erasure: the first record is the best match." }),
                new EraseRecordStep(new EraseStep(best.ExperienceId, best.Scope, Fixed.T0.AddHours(1))),
                new SearchStep(query with { Description = "After the erasure: the erased record is never returned; the second takes its place." }),
            ]);
    }

    private static ErasedRecordCase GetOfAnErasedRecord()
    {
        const int f = 0x66;
        var (a, b) = (Record(f, 1), Record(f, 2));
        return new(
            "A read by ID of an erased record finds nothing, as for an ID that never existed; the record beside it is still " +
            "found.",
            [a, b],
            [
                new GetStep(a.ExperienceId, a.Scope),
                new EraseRecordStep(new EraseStep(a.ExperienceId, a.Scope, Fixed.T0.AddHours(1))),
                new GetStep(a.ExperienceId, a.Scope),
                new GetStep(b.ExperienceId, b.Scope),
            ]);
    }

    private static ErasedRecordCase RecordForAnErasedRecord()
    {
        const int f = 0x67;
        var (a, b) = (Record(f, 1), Record(f, 2));
        return new(
            "Recording an injection that names an erased record leaves no row for it; the live record named beside it gets " +
            "its row.",
            [a, b],
            [
                new EraseRecordStep(new EraseStep(a.ExperienceId, a.Scope, Fixed.T0.AddHours(1))),
                new RecordStep(new RecordInjectionStep(Fixed.Scope, [a.ExperienceId, b.ExperienceId], Fixed.T0.AddHours(2))),
            ]);
    }

    private static ExperienceRecord Record(int family, int n) =>
        SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.");

    private static SearchQuery Query(string taskText) => new(
        string.Empty,
        Fixed.Scope,
        taskText,
        ExperienceRetrievalService.EligibleStatuses,
        RetrievalPolicy.DefaultMinimumConfidence,
        RetrievalPolicy.DefaultCandidateLimit + 1,
        ExcludeModelAuthored: false,
        WithGrants: false,
        ExperienceCandidateQuery.DefaultMinimumMatchedTerms,
        Fixed.T0,
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary><c>seed.json</c>: the records as stored.</summary>
    public static JsonArray ToSeedJson(ErasedRecordCase definition) => SearchContract.ToSeedJson(definition.Records);

    /// <summary><c>steps.json</c>: the description and the steps, in order.</summary>
    public static JsonObject ToStepsJson(ErasedRecordCase definition) => new()
    {
        ["description"] = definition.Description,
        ["steps"] = new JsonArray([.. definition.Steps.Select(step => (JsonNode)(step switch
        {
            SearchStep search => new JsonObject { ["search"] = JsonSerializer.SerializeToNode(search.Query, ContractJson.Options) },
            GetStep get => new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["experienceId"] = get.ExperienceId.ToString("D", CultureInfo.InvariantCulture),
                    ["scope"] = JsonSerializer.SerializeToNode(get.Scope, ContractJson.Options),
                },
            },
            RecordStep record => InjectionRecordContract.StepJson(record.Record),
            EraseRecordStep erase => InjectionRecordContract.StepJson(erase.Erase),
            _ => throw new InvalidOperationException($"Unknown step {step}."),
        }))]),
    };

    /// <summary>Reads <c>steps.json</c> back: the steps a runner runs.</summary>
    public static IReadOnlyList<ErasedStep> ReadSteps(JsonNode steps) =>
    [
        .. steps["steps"]!.AsArray().Select(step => step!["search"] is { } search
            ? new SearchStep(ContractJson.Read<SearchQuery>(search.ToJsonString()))
            : step["get"] is { } get
                ? new GetStep(Guid.Parse(get["experienceId"]!.GetValue<string>()), InjectionRecordContract.ScopeOf(get["scope"]!))
                : InjectionRecordContract.ReadStep(step) switch
                {
                    RecordInjectionStep record => (ErasedStep)new RecordStep(record),
                    EraseStep erase => new EraseRecordStep(erase),
                    var other => throw new InvalidOperationException($"Unknown step {other}."),
                }),
    ];
}
