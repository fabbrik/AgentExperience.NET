using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.MicrosoftAgentFramework;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// <c>query.json</c> of a lessons-view case: what the view is asked (a port's list call), and the clock it reads the page
/// with (<see cref="Now"/>, which the page reports as <c>generatedAt</c>). <see cref="Statuses"/> of <see langword="null"/>
/// lists every status.
/// </summary>
internal sealed record LessonsViewQuery(
    string Description,
    Scope Scope,
    IReadOnlyList<ExperienceStatus>? Statuses,
    int Limit,
    bool IncludeIneligible,
    DateTimeOffset Now);

/// <summary>A lessons-view case: the records seeded (as stored), the injection rows seeded, and the query.</summary>
internal sealed record LessonsViewCase(
    IReadOnlyList<ExperienceRecord> Seed,
    IReadOnlyList<SeededInjection> Injections,
    LessonsViewQuery Query);

/// <summary>
/// Contract 1.7's <c>lessons-view</c> family: what the lessons view lists for the stored records and injection rows of a
/// scope. Running it needs PostgreSQL, so the PostgreSQL contract test runs it. Each case writes <c>seed.json</c>
/// (<c>records</c>, stored records as a search seed holds them, and <c>injections</c>, rows of
/// <c>agent_experience.experience_injections</c> in PostgreSQL's text form), <c>query.json</c> and <c>expected.json</c>
/// (the page, see <see cref="PageJson"/>).
/// </summary>
internal static class LessonsViewContract
{
    public const string Family = "lessons-view";

    public const string File = "sql/list-experience-lessons.sql";

    /// <summary>The clock every case reads its page with.</summary>
    public static readonly DateTimeOffset Now = Fixed.T0.AddHours(6);

    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };

    /// <summary>Every lessons-view case, by directory name.</summary>
    public static IReadOnlyList<(string Name, LessonsViewCase Case)> Cases { get; } =
    [
        ("newest-first-with-eligibility", Mixed(0x70, "Every status the case seeds, newest first (created_at descending, then ID): each lesson carries whether retrieval would use it and why not, the checks (no detail), the block alone, and its last injection.", Query())),
        ("eligible-only", Mixed(0x71, "Lessons retrieval would not return are left out; the probe is the store's largest page, so hasMore is false.", Query() with { IncludeIneligible = false })),
        ("limit-reached-has-more", Mixed(0x72, "The limit is 4: the read is limit + 1 records, the fifth proves more exist, and only four are listed.", Query() with { Limit = 4 })),
        ("limit-equals-the-records", Mixed(0x73, "The limit is 9 and the scope holds exactly 9: nothing past the page, so hasMore is false.", Query() with { Limit = 9 })),
        ("eligible-only-with-limit", Mixed(0x74, "Leaving ineligible lessons out, the limit counts the lessons listed: two of the five eligible ones are listed, and hasMore is true.", Query() with { IncludeIneligible = false, Limit = 2 })),
        ("status-filter", Mixed(0x75, "Only the named statuses are read.", Query() with { Statuses = [ExperienceStatus.Quarantined, ExperienceStatus.Stale, ExperienceStatus.Reinforced] })),
        ("never-injected", Mixed(0x76, "No injection row exists: the injections are tracked and every lesson reads never injected, with a count of 0.", Query(), withInjections: false)),
        ("other-scope-only", OtherScopeOnly()),
    ];

    private static LessonsViewQuery Query() => new(
        string.Empty, Fixed.Scope, Statuses: null, Limit: 50, IncludeIneligible: true, Now);

    private static LessonsViewCase Mixed(int f, string description, LessonsViewQuery query, bool withInjections = true)
    {
        var (a, b, c, d, e, g, h, i, j, other) =
            (Record(f, 1, "Verified after 1 attempt.", -1), Record(f, 2, "Verified after 3 attempts.", -2, ExperienceStatus.Reinforced, 0.9, twoChecks: true),
             Record(f, 3, "Lock timeout.", -3, ExperienceStatus.Quarantined, 0), Record(f, 4, "Verified after 1 attempt.", -4, confidence: 0.4),
             Record(f, 5, "Use a blue-green deploy.", -5, modelAuthored: true),
             Record(f, 6, string.Join(' ', Enumerable.Repeat("An overlong lesson that no budget fits.", 600)), -6),
             Record(f, 7, "Verified after 2 attempts.", -7, ExperienceStatus.Stale, 0.8),
             Record(f, 8, "Verified after 1 attempt.", -8), Record(f, 9, "Verified after 1 attempt.", -8),
             Record(f, 10, "Verified after 1 attempt.", 0, scope: OtherAgent));
        var injections = withInjections
            ?
            [
                new SeededInjection(a.ExperienceId, a.Scope, Fixed.T0.AddHours(-1).AddTicks(1_234_560), 3),
                new SeededInjection(b.ExperienceId, b.Scope, Fixed.T0.AddHours(-2), 1),
                new SeededInjection(c.ExperienceId, c.Scope, Fixed.T0.AddHours(-30), 12),
                new SeededInjection(i.ExperienceId, i.Scope, Fixed.T0.AddDays(-3), 2),
                new SeededInjection(other.ExperienceId, other.Scope, Fixed.T0.AddHours(-1), 9),
            ]
            : Array.Empty<SeededInjection>();
        return new LessonsViewCase([a, b, c, d, e, g, h, i, j, other], injections, query with { Description = description });
    }

    private static LessonsViewCase OtherScopeOnly()
    {
        var other = Record(0x77, 1, "Verified after 1 attempt.", -1, scope: OtherAgent);
        return new LessonsViewCase(
            [other],
            [new SeededInjection(other.ExperienceId, other.Scope, Fixed.T0.AddHours(-1), 1)],
            Query() with { Description = "The scope holds no lesson (another agent's is not listed): an empty page that was found, with injections tracked." });
    }

    private static ExperienceRecord Record(
        int family,
        int n,
        string lesson,
        int daysOld,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 2d / 3d,
        bool modelAuthored = false,
        bool twoChecks = false,
        Scope? scope = null)
    {
        var record = SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", lesson, status, confidence, scope, modelAuthored: modelAuthored);
        var created = Fixed.T0.AddDays(daysOld);
        if (twoChecks)
        {
            var first = record.Outcome.Evidence[0];
            var second = first with { EvidenceId = Fixed.Id(family, 0xf00 + n), CheckId = "smoke", Kind = "smoke-test", Result = CheckResult.Fail, Detail = "never shown by the view" };
            record = record with { Outcome = record.Outcome with { Evidence = [first, second] } };
        }

        return record with { CreatedAt = created, UpdatedAt = created };
    }

    /// <summary><c>seed.json</c>: the records as stored, and the seeded injection rows in PostgreSQL's text form.</summary>
    public static JsonObject ToSeedJson(LessonsViewCase definition) => InjectionRecordContract.ToSeedJson(
        new InjectionRecordCase(string.Empty, definition.Seed, definition.Injections, [], []));

    /// <summary>
    /// <c>expected.json</c>: the page. Each lesson holds <c>experienceId</c>, <c>taskId</c>, <c>status</c>,
    /// <c>confidence</c>, <c>createdAt</c>, <c>updatedAt</c>, <c>checks</c> (<c>checkId</c>, <c>evidenceKind</c>,
    /// <c>result</c>), <c>blockText</c>, <c>isEligible</c>, <c>ineligibility</c> and <c>ineligibleReason</c> (<c>null</c>
    /// when eligible), <c>lastInjectedAt</c> (<c>null</c> when never injected) and <c>injectionCount</c>.
    /// </summary>
    public static JsonObject PageJson(ExperienceLessonsPage page) => new()
    {
        ["outcome"] = page.Outcome.ToString(),
        ["generatedAt"] = ContractJson.Timestamp(page.GeneratedAt),
        ["hasMore"] = page.HasMore,
        ["outOfScopeDropped"] = page.OutOfScopeDropped,
        ["injectionsTracked"] = page.InjectionsTracked,
        ["lessons"] = new JsonArray([.. page.Lessons.Select(lesson => (JsonNode)new JsonObject
        {
            ["experienceId"] = lesson.ExperienceId.ToString("D", CultureInfo.InvariantCulture),
            ["taskId"] = lesson.TaskId,
            ["status"] = lesson.Status.ToString(),
            ["confidence"] = lesson.Confidence,
            ["createdAt"] = ContractJson.Timestamp(lesson.CreatedAt),
            ["updatedAt"] = ContractJson.Timestamp(lesson.UpdatedAt),
            ["checks"] = new JsonArray([.. lesson.Checks.Select(check => (JsonNode)new JsonObject
            {
                ["checkId"] = check.CheckId,
                ["evidenceKind"] = check.EvidenceKind,
                ["result"] = check.Result.ToString(),
            })]),
            ["blockText"] = lesson.BlockText,
            ["isEligible"] = lesson.IsEligible,
            ["ineligibility"] = lesson.Ineligibility?.ToString(),
            ["ineligibleReason"] = lesson.IneligibleReason,
            ["lastInjectedAt"] = lesson.LastInjectedAt is { } at ? ContractJson.Timestamp(at) : null,
            ["injectionCount"] = lesson.InjectionCount,
        })]),
    };

    /// <summary>The number of records a port reads for <paramref name="query"/>: the statement's <c>@limit</c>.</summary>
    public static int Probe(LessonsViewQuery query) =>
        query.IncludeIneligible ? Math.Min(query.Limit + 1, ExperienceRecordQuery.MaxLimit) : ExperienceRecordQuery.MaxLimit;

    /// <summary>A fixed clock.</summary>
    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal static JsonSerializerOptions Json => ContractJson.Options;
}
