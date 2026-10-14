using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// <c>query.json</c> of a search case. The first block is the text search's arguments (<see cref="Scope"/> gives the
/// six scope arguments; the bounds declared before the call are the same scope's fields). <see cref="Now"/> and
/// <see cref="PreferredEnvironment"/> are the ranking's inputs.
/// </summary>
internal sealed record SearchQuery(
    string Description,
    Scope Scope,
    string TaskText,
    IReadOnlyList<ExperienceStatus> Statuses,
    double MinConfidence,
    int Limit,
    bool ExcludeModelAuthored,
    bool WithGrants,
    int MinimumMatchedTerms,
    DateTimeOffset Now,
    IReadOnlyDictionary<string, string> PreferredEnvironment);

/// <summary>One row the text search returned, in its order.</summary>
internal sealed record SearchRow(Guid ExperienceId, float Relevance);

/// <summary>One record as ranking ordered it, with its score and components.</summary>
internal sealed record RankedRow(Guid ExperienceId, double Score, IReadOnlyList<RankingComponentInput> Components);

/// <summary><c>expected.json</c> of a search case.</summary>
internal sealed record SearchExpected(IReadOnlyList<SearchRow> Rows, IReadOnlyList<RankedRow> Ranked);

/// <summary>A search case: the records seeded (as stored) and the query.</summary>
internal sealed record SearchCase(IReadOnlyList<ExperienceRecord> Seed, SearchQuery Query);

/// <summary>The search family's cases. Running them needs PostgreSQL, so the PostgreSQL contract test runs them.</summary>
internal static class SearchContract
{
    public const string Family = "search";

    private static readonly DateTimeOffset Created = Fixed.T0.AddDays(-10);

    private static readonly DateTimeOffset Now = Fixed.T0;

    /// <summary>Every search case, by directory name.</summary>
    public static IReadOnlyList<(string Name, SearchCase Case)> Cases { get; } =
    [
        ("coverage-filters-and-ranking", CoverageFiltersAndRanking()),
        ("model-authored-excluded", ModelAuthoredExcluded()),
        ("recency-status-and-environment", RecencyStatusAndEnvironment()),
        ("query-syntax-is-plain-words", QuerySyntaxIsPlainWords()),
    ];

    private static SearchCase CoverageFiltersAndRanking()
    {
        const int f = 0x5a;
        return new SearchCase(
        [
            Seed(f, 1, "refund-stuck-pending", "Refund stuck in pending", "The refund lock timeout is the cause."),
            Seed(f, 2, "refund-stuck-pending", "Refund stuck in pending", "Verified after 1 attempt."),
            Seed(f, 3, "refund-lock", "Refund lock timeout", "Verified after 1 attempt."),
            Seed(f, 4, "refund-pending", "Refund pending", "Verified after 1 attempt."),
            Seed(f, 5, "refund-stuck-pending", "Refund stuck in pending", "Lock timeout.", status: ExperienceStatus.Quarantined, confidence: 0),
            Seed(f, 6, "refund-stuck-pending", "Refund stuck in pending", "Lock timeout.", confidence: 0.4),
            Seed(f, 7, "refund-stuck-pending", "Refund stuck in pending", "Lock timeout.", scope: Fixed.Scope with { AgentId = "other-agent" }),
            Seed(f, 8, "refund-stuck", "Refund stuck behind a lock timeout", "Verified after 2 attempts.", status: ExperienceStatus.Reinforced, confidence: 0.9),
        ],
        Query(
            "Five query terms (refund, stuck, pend, lock, timeout), at least three required. Below three matched terms, a " +
            "Quarantined status, a confidence under the floor and another scope are all left out. Rows come back by coverage, " +
            "then ts_rank_cd, then ID; ranking then reorders them by the weighted score.",
            "refund stuck in pending after a lock timeout"));
    }

    private static SearchCase ModelAuthoredExcluded()
    {
        const int f = 0x5b;
        return new SearchCase(
        [
            Seed(f, 1, "deploy-service", "Deploy service X to prod", "Verified after 1 attempt."),
            Seed(f, 2, "deploy-service", "Deploy service X to prod", "Use a blue-green deploy.", modelAuthored: true),
        ],
        Query(
            "With the model-authored exclusion asked for, a record whose reflection a model wrote is left out before the limit.",
            "deploy service X to prod") with { ExcludeModelAuthored = true });
    }

    private static SearchCase RecencyStatusAndEnvironment()
    {
        const int f = 0x5c;
        var usEast = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "us-east" };
        return new SearchCase(
        [
            Seed(f, 1, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", updated: Now.AddDays(-90)),
            Seed(f, 2, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", updated: Now.AddDays(-1)),
            Seed(f, 3, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", status: ExperienceStatus.Reinforced, updated: Now.AddDays(-30)),
            Seed(f, 4, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", updated: Now.AddDays(-1), metadata: usEast),
        ],
        Query(
            "Four records the search cannot tell apart (equal coverage and text rank, so ID order). Ranking separates them by " +
            "recency (half-life 30 days, from updated_at), status (Reinforced 1.0, Validated 0.5) and environment fit against " +
            "the preferred attributes (the share that match).",
            "rotate the signing keys") with
        {
            PreferredEnvironment = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "eu-west" },
        });
    }

    private static SearchCase QuerySyntaxIsPlainWords()
    {
        const int f = 0x5d;
        return new SearchCase(
        [
            Seed(f, 1, "multi-stage-build", "Fix the multi-stage build cache", "Verified after 1 attempt."),
            Seed(f, 2, "build-cache", "Build cache misses on stage two", "Verified after 1 attempt."),
            Seed(f, 3, "docker-or-podman", "Choose docker or podman", "Verified after 1 attempt."),
        ],
        Query(
            "Quotes, \"or\", a leading \"-\" and a backslash are plain words to the parser, never operators, and a hyphenated " +
            "compound counts once, as the whole compound. With two required terms the second record's \"stage\" still matches " +
            "through its own lexemes.",
            "\"multi-stage\" build or -cache \\ podman") with { MinimumMatchedTerms = 2 });
    }

    private static SearchQuery Query(string description, string taskText) => new(
        description,
        Fixed.Scope,
        taskText,
        ExperienceRetrievalService.EligibleStatuses,
        RetrievalPolicy.DefaultMinimumConfidence,
        RetrievalPolicy.DefaultCandidateLimit + 1,
        ExcludeModelAuthored: false,
        WithGrants: false,
        ExperienceCandidateQuery.DefaultMinimumMatchedTerms,
        Now,
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static ExperienceRecord Seed(
        int family,
        int n,
        string taskId,
        string summary,
        string lesson,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 2d / 3d,
        Scope? scope = null,
        DateTimeOffset? updated = null,
        bool modelAuthored = false,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var id = Fixed.Id(family, n);
        var run = Fixed.Id(family, 0x100 + n);
        var evidence = new Evidence(Fixed.Id(family, 0xe00 + n), Fixed.Id(family, 0xd00), "rev-1", "check", "integration-test", CheckResult.Pass, "contract-ci", null, Created);
        var reflection = status == ExperienceStatus.Quarantined
            ? null
            : new Reflection(
                Fixed.Id(family, 0x200 + n),
                run,
                lesson,
                [],
                [],
                ["Runtime version: runtime-1"],
                [],
                "Reuse only where the listed preconditions match.",
                [evidence.EvidenceId],
                TaskVerificationStatus.Verified,
                1.0,
                "1.0.0",
                modelAuthored ? "host-model-reflector/1.0" : "host-reflector/1.0",
                Created)
            {
                Authorship = modelAuthored ? ReflectionAuthorship.Model : ReflectionAuthorship.Deterministic,
            };

        return new ExperienceRecord(
            id,
            run,
            scope ?? Fixed.Scope,
            taskId,
            summary,
            [],
            new Outcome(status == ExperienceStatus.Quarantined ? TaskVerificationStatus.Failed : TaskVerificationStatus.Verified, [evidence], null, Created),
            status == ExperienceStatus.Quarantined ? 0 : 1.0,
            reflection,
            metadata is null ? Fixed.Environment : Fixed.Environment with { Metadata = metadata },
            Fixed.Provenance(Created),
            status,
            confidence,
            status == ExperienceStatus.Quarantined ? 0 : 1,
            0,
            1,
            Created,
            updated ?? Created);
    }

    /// <summary>The stored form of a case's seed, as <c>seed.json</c> holds it.</summary>
    public static JsonArray ToSeedJson(IReadOnlyList<ExperienceRecord> seed) => [.. seed.Select(record => (JsonNode)ContractJson.ToStored(record))];
}
