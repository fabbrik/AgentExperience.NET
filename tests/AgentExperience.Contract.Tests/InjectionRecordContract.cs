using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;

namespace AgentExperience.Contract.Tests;

/// <summary>One step of a record-injection case: record an injected block, or erase a record as its owner.</summary>
internal abstract record InjectionStep;

/// <summary>Runs <c>sql/record-injection.sql</c> once, for <paramref name="Scope"/>, under bounds declared for it.</summary>
internal sealed record RecordInjectionStep(Scope Scope, IReadOnlyList<Guid> ExperienceIds, DateTimeOffset InjectedAt) : InjectionStep;

/// <summary>Erases a record through <c>agent_experience.purge_experience_record</c>, as the schema's owner.</summary>
internal sealed record EraseStep(Guid ExperienceId, Scope Scope, DateTimeOffset DeletedAt) : InjectionStep;

/// <summary>A seeded row of <c>agent_experience.experience_injections</c>, inserted as the schema's owner.</summary>
internal sealed record SeededInjection(Guid ExperienceId, Scope Scope, DateTimeOffset LastInjectedAt, long InjectionCount);

/// <summary>
/// A record-injection case: the records seeded (as stored), the injection rows seeded, the steps, and the records whose
/// row's <c>last_injected_at</c> is the database clock at the step that clamped it.
/// </summary>
internal sealed record InjectionRecordCase(
    string Description,
    IReadOnlyList<ExperienceRecord> Records,
    IReadOnlyList<SeededInjection> Injections,
    IReadOnlyList<InjectionStep> Steps,
    IReadOnlyList<Guid> Clamped);

/// <summary>
/// Contract 1.4's record-injection family. Running it needs PostgreSQL, so the PostgreSQL contract test runs it. Each case
/// writes <c>seed.json</c> (<c>records</c>, stored records as a search seed holds them, and <c>injections</c>, rows of
/// <c>agent_experience.experience_injections</c> with every column in PostgreSQL's text form), <c>steps.json</c> (the
/// description and the steps, in order) and <c>expected.json</c> (every row of the table afterwards, ordered by
/// <c>experience_id</c>, every column in PostgreSQL's text form in UTC; a clamped instant is <see cref="DatabaseClock"/>).
/// </summary>
internal static class InjectionRecordContract
{
    public const string Family = "record-injection";

    /// <summary>The value an expected row's <c>last_injected_at</c> holds when the database clock clamped it.</summary>
    public const string DatabaseClock = "database-clock";

    /// <summary>The columns of <c>agent_experience.experience_injections</c>, in table order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "experience_id", "tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id", "last_injected_at", "injection_count",
    ];

    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };

    /// <summary>Every record-injection case, by directory name.</summary>
    public static IReadOnlyList<(string Name, InjectionRecordCase Case)> Cases { get; } =
    [
        ("repeat-and-earlier-instant", RepeatAndEarlierInstant()),
        ("own-scope-only", OwnScopeOnly()),
        ("clamped-to-the-database-clock", ClampedToTheDatabaseClock()),
        ("saturated-count", SaturatedCount()),
        ("erased-records", ErasedRecords()),
    ];

    private static InjectionRecordCase RepeatAndEarlierInstant()
    {
        const int f = 0x60;
        var (a, b) = (Record(f, 1), Record(f, 2));
        return new(
            "One row per record: the count rises by one per statement that names it (a repeated ID in one statement counts " +
            "once), and the instant only moves forward. The instant is stored truncated to whole microseconds.",
            [a, b],
            [],
            [
                new RecordInjectionStep(Fixed.Scope, [a.ExperienceId, b.ExperienceId], Fixed.T0.AddHours(1).AddTicks(1_234_567)),
                new RecordInjectionStep(Fixed.Scope, [a.ExperienceId], Fixed.T0.AddHours(2)),
                new RecordInjectionStep(Fixed.Scope, [a.ExperienceId, a.ExperienceId], Fixed.T0.AddMinutes(30)),
            ],
            []);
    }

    private static InjectionRecordCase OwnScopeOnly()
    {
        const int f = 0x61;
        var mine = Record(f, 1);
        var otherAgents = Record(f, 2, OtherAgent);
        var narrower = Record(f, 3, Fixed.Scope with { TeamId = "team-1" });
        return new(
            "Only a record live in exactly the injecting scope gets a row: not one of another agent, not one in a narrower " +
            "scope the bounds can see, not an ID with no record, and not a record named from another scope.",
            [mine, otherAgents, narrower],
            [],
            [
                new RecordInjectionStep(Fixed.Scope, [mine.ExperienceId, otherAgents.ExperienceId, narrower.ExperienceId, Fixed.Id(f, 9)], Fixed.T0.AddHours(1)),
                new RecordInjectionStep(OtherAgent, [mine.ExperienceId], Fixed.T0.AddHours(2)),
            ],
            []);
    }

    private static InjectionRecordCase ClampedToTheDatabaseClock()
    {
        const int f = 0x62;
        var a = Record(f, 1);
        return new(
            "An instant ahead of the database clock is stored as the database clock (clock_timestamp()) at the statement, " +
            "and a later statement with an earlier instant does not move it back.",
            [a],
            [],
            [
                new RecordInjectionStep(Fixed.Scope, [a.ExperienceId], new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_990)),
                new RecordInjectionStep(Fixed.Scope, [a.ExperienceId], Fixed.T0.AddHours(1)),
            ],
            [a.ExperienceId]);
    }

    private static InjectionRecordCase SaturatedCount()
    {
        const int f = 0x63;
        var (a, b) = (Record(f, 1), Record(f, 2));
        return new(
            "The count saturates at the bigint maximum: a row already there stays there, one below it reaches it, and the " +
            "instant still moves forward.",
            [a, b],
            [
                new SeededInjection(a.ExperienceId, a.Scope, Fixed.T0, long.MaxValue),
                new SeededInjection(b.ExperienceId, b.Scope, Fixed.T0, long.MaxValue - 1),
            ],
            [new RecordInjectionStep(Fixed.Scope, [a.ExperienceId, b.ExperienceId], Fixed.T0.AddHours(1))],
            []);
    }

    private static InjectionRecordCase ErasedRecords()
    {
        const int f = 0x64;
        var (a, b, c) = (Record(f, 1), Record(f, 2), Record(f, 3));
        return new(
            "Erasing a record removes its row, and an erased record is never recorded again: the statement selects live " +
            "records only.",
            [a, b, c],
            [],
            [
                new RecordInjectionStep(Fixed.Scope, [a.ExperienceId, b.ExperienceId], Fixed.T0.AddHours(1)),
                new EraseStep(a.ExperienceId, a.Scope, Fixed.T0.AddHours(2)),
                new RecordInjectionStep(Fixed.Scope, [a.ExperienceId, b.ExperienceId, c.ExperienceId], Fixed.T0.AddHours(3)),
                new EraseStep(c.ExperienceId, c.Scope, Fixed.T0.AddHours(4)),
            ],
            []);
    }

    private static ExperienceRecord Record(int family, int n, Scope? scope = null) =>
        SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", scope: scope);

    /// <summary><c>seed.json</c>: the records as stored, and the seeded injection rows in PostgreSQL's text form.</summary>
    public static JsonObject ToSeedJson(InjectionRecordCase definition) => new()
    {
        ["records"] = SearchContract.ToSeedJson(definition.Records),
        ["injections"] = new JsonArray([.. definition.Injections.Select(row => (JsonNode)Row(
            row.ExperienceId,
            row.Scope,
            PostgresText(row.LastInjectedAt),
            row.InjectionCount.ToString(CultureInfo.InvariantCulture)))]),
    };

    /// <summary><c>steps.json</c>: the description and the steps, in order.</summary>
    public static JsonObject ToStepsJson(InjectionRecordCase definition) => new()
    {
        ["description"] = definition.Description,
        ["steps"] = new JsonArray([.. definition.Steps.Select(step => (JsonNode)(step switch
        {
            RecordInjectionStep record => new JsonObject
            {
                ["record"] = new JsonObject
                {
                    ["scope"] = JsonSerializer.SerializeToNode(record.Scope, ContractJson.Options),
                    ["experienceIds"] = new JsonArray([.. record.ExperienceIds.Select(id => (JsonNode)Id(id))]),
                    ["injectedAt"] = ContractJson.Timestamp(record.InjectedAt),
                },
            },
            EraseStep erase => new JsonObject
            {
                ["erase"] = new JsonObject
                {
                    ["experienceId"] = Id(erase.ExperienceId),
                    ["scope"] = JsonSerializer.SerializeToNode(erase.Scope, ContractJson.Options),
                    ["deletedAt"] = ContractJson.Timestamp(erase.DeletedAt),
                },
            },
            _ => throw new InvalidOperationException($"Unknown step {step}."),
        }))]),
    };

    /// <summary>Reads <c>steps.json</c> back: the steps a runner runs.</summary>
    public static IReadOnlyList<InjectionStep> ReadSteps(JsonNode steps) =>
    [
        .. steps["steps"]!.AsArray().Select(step => step!["record"] is { } record
            ? (InjectionStep)new RecordInjectionStep(
                ScopeOf(record["scope"]!),
                [.. record["experienceIds"]!.AsArray().Select(id => Guid.Parse(id!.GetValue<string>()))],
                Instant(record["injectedAt"]!))
            : new EraseStep(
                Guid.Parse(step["erase"]!["experienceId"]!.GetValue<string>()),
                ScopeOf(step["erase"]!["scope"]!),
                Instant(step["erase"]!["deletedAt"]!))),
    ];

    /// <summary>One row of the table, every column as PostgreSQL's text form.</summary>
    public static JsonObject Row(Guid experienceId, Scope scope, string lastInjectedAt, string injectionCount) => new()
    {
        ["experience_id"] = Id(experienceId),
        ["tenant_id"] = scope.TenantId,
        ["application_id"] = scope.ApplicationId,
        ["project_id"] = scope.ProjectId,
        ["team_id"] = scope.TeamId,
        ["agent_id"] = scope.AgentId,
        ["user_id"] = scope.UserId,
        ["last_injected_at"] = lastInjectedAt,
        ["injection_count"] = injectionCount,
    };

    /// <summary>An instant as PostgreSQL writes a <c>timestamptz</c> in the time zone UTC: <c>2026-03-02 10:00:00.123456+00</c>.</summary>
    public static string PostgresText(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var text = utc.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return text + "+00";
    }

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static Scope ScopeOf(JsonNode scope) => JsonSerializer.Deserialize<Scope>(scope.ToJsonString(), ContractJson.Options)!;

    private static DateTimeOffset Instant(JsonNode value) =>
        DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
