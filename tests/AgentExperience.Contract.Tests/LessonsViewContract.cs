using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.MicrosoftAgentFramework;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// <c>query.json</c> of a lessons-view case: what the view is asked (a port's list call), and the clock it reads the page
/// with (<see cref="Now"/>, which the page reports as <c>generatedAt</c>). <see cref="Statuses"/> of <see langword="null"/>
/// lists every status.
/// </summary>
/// <remarks>
/// Contract 1.10 adds four members, written only when they are not their default, so every 1.7 <c>query.json</c> is
/// unchanged: <see cref="IncludeMeasuredBenefit"/> (the view reads one benefit estimate for the scope, over the benefit
/// policy's window, 30 days before <see cref="Now"/>), <see cref="MinimumRunsPerArm"/> (the policy's minimum, the
/// default 20 when absent), <see cref="PerModel"/> (the policy's per-model switch, which splits the estimate by model) and
/// <see cref="HoldoutReadRevoked"/> (before listing, the schema's owner revokes the application role's <c>SELECT</c> on
/// <c>agent_experience.experience_holdout_assignments</c>, so the holdout read fails). Contract 1.12 adds two more, written
/// only when set: the retrieval policy's <see cref="MinimumConfidence"/> and <see cref="MaxAgeTicks"/> (its other options
/// the defaults), which the view's eligibility applies as retrieval does.
/// </remarks>
internal sealed record LessonsViewQuery(
    string Description,
    Scope Scope,
    IReadOnlyList<ExperienceStatus>? Statuses,
    int Limit,
    bool IncludeIneligible,
    DateTimeOffset Now,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IncludeMeasuredBenefit = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MinimumRunsPerArm = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PerModel = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HoldoutReadRevoked = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? MinimumConfidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MaxAgeTicks = null);

/// <summary>
/// A lessons-view case: the records seeded (as stored), the injection rows seeded, the query, and (contract 1.10) the
/// holdout's assignment rows seeded.
/// </summary>
internal sealed record LessonsViewCase(
    IReadOnlyList<ExperienceRecord> Seed,
    IReadOnlyList<SeededInjection> Injections,
    LessonsViewQuery Query)
{
    /// <summary>The holdout's assignment rows (contract 1.10), seeded as the schema's owner; <c>seed.json</c> holds them only when there are any.</summary>
    public IReadOnlyList<SeededAssignment> Assignments { get; init; } = [];
}

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

        // Contract 1.10.
        ("measured-benefit", Measured(0x7a, Query() with
        {
            Description = "IncludeMeasuredBenefit, at a minimum of 2 runs per arm: one estimate for the scope over the window (30 days before the page's clock). The lesson treatment runs failed on and holdout runs verified on is measured Hurts, the other Helps; a lesson no assigned run named, and one named only by an assignment older than the window, carry null, as does every run's own record.",
            IncludeMeasuredBenefit = true,
            MinimumRunsPerArm = 2,
        })),
        ("measured-benefit-default-minimum", Measured(0x7b, Query() with
        {
            Description = "IncludeMeasuredBenefit at the default minimum of 20 runs per arm: the same counts, every verdict NoEvidence.",
            IncludeMeasuredBenefit = true,
        })),
        ("measured-benefit-by-model", Measured(0x7c, Query() with
        {
            Description = "IncludeMeasuredBenefit with the policy's PerModel on: the estimate is split by model, so each lesson's measured benefit carries its model pairs.",
            IncludeMeasuredBenefit = true,
            MinimumRunsPerArm = 2,
            PerModel = true,
        })),
        ("measured-benefit-read-fails", Measured(0x7d, Query() with
        {
            Description = "IncludeMeasuredBenefit, and the application role may not read the holdout's assignments: the holdout read fails, and the list fails with it, as a store failure (error class Infrastructure). No page is returned.",
            IncludeMeasuredBenefit = true,
            MinimumRunsPerArm = 2,
            HoldoutReadRevoked = true,
        })),

        // Contract 1.12.
        ("retrieval-policy", RetrievalPolicyCase()),
    ];

    /// <summary>
    /// Contract 1.12: the view under a retrieval policy with a 0.7 confidence floor and a 30-day MaxAge, applied as
    /// retrieval applies them, in order: status, then the floor, then the age (from the last activity, at the page's clock).
    /// </summary>
    private static LessonsViewCase RetrievalPolicyCase()
    {
        const int f = 0x7e;
        ExperienceRecord Aged(ExperienceRecord record, int updatedDaysAgo) => record with { UpdatedAt = Now.AddDays(-updatedDaysAgo) };
        return new LessonsViewCase(
            [
                Aged(Record(f, 1, "Verified after 1 attempt.", -40, confidence: 0.8), 1),
                Aged(Record(f, 2, "Verified after 1 attempt.", -40, confidence: 0.8), 31),
                Aged(Record(f, 3, "Verified after 1 attempt.", -40, confidence: 0.65), 1),
                Aged(Record(f, 4, "Verified after 1 attempt.", -40, confidence: 0.65), 31),
                Aged(Record(f, 5, "Lock timeout.", -40, ExperienceStatus.Quarantined, 0.9), 31),
                Aged(Record(f, 6, "Verified after 1 attempt.", -40, confidence: 0.7), 30),
            ],
            [],
            Query() with
            {
                Description = "A retrieval policy with a 0.7 confidence floor and a 30-day MaxAge: a lesson below the floor is BelowConfidenceFloor, naming the floor; one whose last activity is more than 30 days before the page's clock is Expired; status comes first, then the floor, then the age; exactly at the floor and exactly 30 days old is eligible.",
                MinimumConfidence = 0.7,
                MaxAgeTicks = TimeSpan.FromDays(30).Ticks,
            });
    }

    /// <summary>The error class of the failure a case whose holdout read fails ends in: .NET throws <c>ExperienceStoreException</c>.</summary>
    public const string ReadFailedErrorClass = "Infrastructure";

    /// <summary>
    /// A scope with four lessons and the runs that measured them: H (treatment runs failed, holdout runs verified), G (the
    /// reverse), N (no assigned run) and O (one assignment, older than the window). Each run's own record carries its
    /// outcome and the model that ran it; the lessons carry the model that learned them.
    /// </summary>
    private static LessonsViewCase Measured(int f, LessonsViewQuery query)
    {
        var h = Record(f, 1, "Rotate every key at once.", -2, metadata: ModelMetadata("model-a"));
        var g = Record(f, 2, "Rotate the keys in the documented order.", -3, metadata: ModelMetadata("model-b"));
        var n = Record(f, 3, "Announce the rotation first.", -4);
        var o = Record(f, 4, "Delete the old key straight away.", -40);
        var records = new List<ExperienceRecord> { h, g, n, o };
        var assignments = new List<SeededAssignment>();
        var next = 0x10;

        void Runs(ExperienceRecord lesson, HoldoutArm arm, bool verified, string model, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var run = Record(f, next++, "Verified after 1 attempt.", -1, metadata: ModelMetadata(model), outcome: verified ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed);
                records.Add(run);
                assignments.Add(new SeededAssignment(run.SourceRunId, lesson.ExperienceId, lesson.Revision, arm, lesson.Scope, Fixed.T0.AddMinutes(next).AddTicks(1_234_560)));
            }
        }

        Runs(h, HoldoutArm.Treatment, verified: false, "model-a", 2);
        Runs(h, HoldoutArm.Holdout, verified: true, "model-b", 2);
        Runs(g, HoldoutArm.Treatment, verified: true, "model-a", 2);
        Runs(g, HoldoutArm.Holdout, verified: false, "model-a", 2);

        // Outside the window: assigned 31 days before the records' clock.
        var old = Record(f, next++, "Verified after 1 attempt.", -1, outcome: TaskVerificationStatus.Verified);
        records.Add(old);
        assignments.Add(new SeededAssignment(old.SourceRunId, o.ExperienceId, o.Revision, HoldoutArm.Treatment, o.Scope, Fixed.T0.AddDays(-31)));

        return new LessonsViewCase(records, [], query) { Assignments = assignments };
    }

    private static Dictionary<string, string> ModelMetadata(string model) =>
        new(StringComparer.Ordinal) { ["region"] = "eu-west", [HoldoutOutcomeRow.ModelMetadataKey] = model };

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
        Scope? scope = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        TaskVerificationStatus? outcome = null)
    {
        var record = SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", lesson, status, confidence, scope, modelAuthored: modelAuthored, metadata: metadata);
        if (outcome is { } verified)
        {
            record = record with { Outcome = record.Outcome with { Status = verified } };
        }

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
    public static JsonObject ToSeedJson(LessonsViewCase definition)
    {
        var seed = InjectionRecordContract.ToSeedJson(new InjectionRecordCase(string.Empty, definition.Seed, definition.Injections, [], []));
        if (definition.Assignments.Count > 0)
        {
            // Contract 1.10: the holdout's rows, in holdout-outcomes' seed form.
            seed["assignments"] = HoldoutOutcomesContract.ToSeedJson(new HoldoutOutcomesCase([], definition.Assignments, new HoldoutOutcomesQuery(string.Empty, definition.Query.Scope, 1)))["assignments"]!.DeepClone();
        }

        return seed;
    }

    /// <summary>
    /// <c>expected.json</c>: the page. Each lesson holds <c>experienceId</c>, <c>taskId</c>, <c>status</c>,
    /// <c>confidence</c>, <c>createdAt</c>, <c>updatedAt</c>, <c>checks</c> (<c>checkId</c>, <c>evidenceKind</c>,
    /// <c>result</c>), <c>blockText</c>, <c>isEligible</c>, <c>ineligibility</c> and <c>ineligibleReason</c> (<c>null</c>
    /// when eligible), <c>lastInjectedAt</c> (<c>null</c> when never injected) and <c>injectionCount</c>.
    /// </summary>
    /// <remarks>
    /// Contract 1.10: with <paramref name="includeMeasuredBenefit"/> each lesson also holds <c>measuredBenefit</c>,
    /// <c>null</c> or one lesson of a benefit-estimate <c>expected.json</c> (<c>experienceId</c>, <c>comparison</c>,
    /// <c>byModelPair</c>); without it the lesson is exactly as contract 1.7 writes it.
    /// </remarks>
    public static JsonObject PageJson(ExperienceLessonsPage page, bool includeMeasuredBenefit = false)
    {
        var json = BasePageJson(page);
        if (includeMeasuredBenefit)
        {
            foreach (var (lesson, node) in page.Lessons.Zip(json["lessons"]!.AsArray()))
            {
                node!["measuredBenefit"] = lesson.MeasuredBenefit is { } benefit ? BenefitEstimateContract.LessonJson(benefit) : null;
            }
        }

        return json;
    }

    /// <summary>
    /// <c>expected.json</c> of a case whose list fails: the failure's <c>errorClass</c>, as .NET's telemetry classifies it.
    /// </summary>
    public static JsonObject FailureJson(string errorClass) => new() { ["errorClass"] = errorClass };

    private static JsonObject BasePageJson(ExperienceLessonsPage page) => new()
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
