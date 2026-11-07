using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.8's <c>holdout-assign</c> family: one file, <c>cases.json</c>, of run IDs and salts with the fraction the
/// .NET assignment function (<see cref="HoldoutAssignmentFunction"/>) gives each, and the arm at each of a list of rates
/// (including the fraction itself, which pins "held out when the fraction is strictly below the rate"). Needs no database.
/// </summary>
internal static class HoldoutAssignContract
{
    public const string Family = "holdout-assign";

    public const string FileName = "cases.json";

    /// <summary>
    /// The run IDs: the six the .NET tests pin against an independent implementation, the nil and the all-ones UUIDs, and
    /// two of the shape the other fixtures use.
    /// </summary>
    public static IReadOnlyList<Guid> RunIds { get; } =
    [
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
        Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479"),
        Guid.Parse("a3bb189e-8bf9-3888-9912-ace4e6543002"),
        Guid.Parse("c9a646d3-9c61-4cb7-bfcd-ee2522c8f633"),
        Guid.Empty,
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
        Fixed.Id(0x70, 0x101),
        Fixed.Id(0x70, 0x102),
    ];

    /// <summary>The default salt, another ASCII one, and one whose UTF-8 bytes are not its UTF-16 code units.</summary>
    public static IReadOnlyList<string> Salts { get; } =
    [
        ExperienceHoldoutOptions.DefaultSalt,
        "another-experiment",
        "expérience-実験-🧪",
    ];

    /// <summary>The rates every case is assigned at, before the case's own fraction.</summary>
    public static IReadOnlyList<double> Rates { get; } = [0, 0.05, 0.25, 0.5, 0.75, 1];

    /// <summary>The whole file: a description and every run ID × salt.</summary>
    public static JsonObject Run() => new()
    {
        ["description"] =
            "fraction = SHA-256 over the run ID's 16 bytes in RFC 4122 order, then the salt's UTF-8 bytes; the digest's first " +
            "8 bytes as a big-endian unsigned integer, divided by 2^64. The arm at a rate is Holdout when the rate is at least 1 " +
            "or the fraction is strictly below the rate, else Treatment. Each case's last rate is its own fraction (Treatment).",
        ["cases"] = new JsonArray([.. RunIds.SelectMany(runId => Salts.Select(salt => (JsonNode)Case(runId, salt)))]),
    };

    private static JsonObject Case(Guid runId, string salt)
    {
        var fraction = HoldoutAssignmentFunction.Fraction(runId, salt);
        return new JsonObject
        {
            ["runId"] = runId.ToString("D", CultureInfo.InvariantCulture),
            ["salt"] = salt,
            ["fraction"] = fraction,
            ["arms"] = new JsonArray([.. Rates.Append(fraction).Select(rate => (JsonNode)new JsonObject
            {
                ["rate"] = rate,
                ["arm"] = HoldoutAssignmentFunction.Assign(runId, salt, rate).ToString(),
            })]),
        };
    }

    /// <summary>Every problem with a checked-in file: an arm or fraction the library no longer gives.</summary>
    public static void Recheck(string path, string text, List<string> drift)
    {
        foreach (var entry in JsonNode.Parse(text)!["cases"]!.AsArray())
        {
            var runId = Guid.Parse(entry!["runId"]!.GetValue<string>());
            var salt = entry["salt"]!.GetValue<string>();
            var fraction = HoldoutAssignmentFunction.Fraction(runId, salt);
            if (fraction.ToString("G15", CultureInfo.InvariantCulture) != entry["fraction"]!.GetValue<double>().ToString("G15", CultureInfo.InvariantCulture))
            {
                drift.Add($"{path}: {runId} with salt '{salt}' is now at {fraction}.");
            }

            foreach (var arm in entry["arms"]!.AsArray())
            {
                var rate = arm!["rate"]!.GetValue<double>();
                var now = HoldoutAssignmentFunction.Assign(runId, salt, rate).ToString();
                if (now != arm["arm"]!.GetValue<string>())
                {
                    drift.Add($"{path}: {runId} with salt '{salt}' at rate {rate} is now {now}.");
                }
            }
        }
    }
}

/// <summary>Runs <c>sql/record-holdout-assignment.sql</c> once, for <paramref name="Scope"/>, under bounds declared for it.</summary>
internal sealed record RecordHoldoutStep(Scope Scope, Guid RunId, HoldoutArm Arm, IReadOnlyList<RunExposure> Records, DateTimeOffset AssignedAt) : InjectionStep;

/// <summary>
/// The late-write guard's race: <paramref name="Erase"/> starts (as the owner, uncommitted), <paramref name="Record"/>
/// starts and waits on the erasure's lock, then the erasure commits and the record call completes.
/// </summary>
internal sealed record EraseDuringRecordStep(EraseStep Erase, RecordHoldoutStep Record) : InjectionStep;

/// <summary>
/// A holdout-record case: the records seeded (as stored), the steps (record an assignment, or erase a record as its
/// owner), and the (run, record) rows whose <c>assigned_at</c> is the database clock at the step that clamped it.
/// </summary>
internal sealed record HoldoutRecordCase(
    string Description,
    IReadOnlyList<ExperienceRecord> Records,
    IReadOnlyList<InjectionStep> Steps,
    IReadOnlyList<(Guid RunId, Guid ExperienceId)> Clamped);

/// <summary>
/// Contract 1.8's holdout-record family. Running it needs PostgreSQL, so the PostgreSQL contract test runs it. Each case
/// writes <c>seed.json</c> (<c>records</c>, stored records as a search seed holds them), <c>steps.json</c> (the
/// description and the steps, in order) and <c>expected.json</c> (every row of
/// <c>agent_experience.experience_holdout_assignments</c> afterwards, ordered by <c>run_id</c> then <c>experience_id</c>,
/// every column in PostgreSQL's text form in UTC; a clamped instant is <see cref="InjectionRecordContract.DatabaseClock"/>).
/// </summary>
internal static class HoldoutRecordContract
{
    public const string Family = "holdout-record";

    /// <summary>The columns of <c>agent_experience.experience_holdout_assignments</c>, in table order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "run_id", "experience_id", "revision", "arm", "tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id", "assigned_at",
    ];

    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };

    /// <summary>Every holdout-record case, by directory name.</summary>
    public static IReadOnlyList<(string Name, HoldoutRecordCase Case)> Cases { get; } =
    [
        ("repeat-keeps-the-first-row", RepeatKeepsTheFirstRow()),
        ("own-scope-only", OwnScopeOnly()),
        ("rendered-revision-kept", RenderedRevisionKept()),
        ("clamped-to-the-database-clock", ClampedToTheDatabaseClock()),
        ("erased-lesson", ErasedLesson()),
        ("erased-run", ErasedRun()),
    ];

    private static HoldoutRecordCase RepeatKeepsTheFirstRow()
    {
        const int f = 0x70;
        var (a, b) = (Record(f, 1), Record(f, 2));
        var (first, second) = (Fixed.Id(f, 0x901), Fixed.Id(f, 0x902));
        return new(
            "One row per run and record: a run recorded again keeps its first rows (arm, revision and instant), whatever the " +
            "later statement names; another run gets rows of its own. The instant is stored truncated to whole microseconds.",
            [a, b],
            [
                Step(Fixed.Scope, first, HoldoutArm.Treatment, [a, b], Fixed.T0.AddHours(1).AddTicks(1_234_567)),
                Step(Fixed.Scope, first, HoldoutArm.Holdout, [a], Fixed.T0.AddHours(2)),
                Step(Fixed.Scope, second, HoldoutArm.Holdout, [a], Fixed.T0.AddMinutes(30)),
            ],
            []);
    }

    private static HoldoutRecordCase OwnScopeOnly()
    {
        const int f = 0x71;
        var mine = Record(f, 1);
        var otherAgents = Record(f, 2, OtherAgent);
        var narrower = Record(f, 3, Fixed.Scope with { TeamId = "team-1" });
        var run = Fixed.Id(f, 0x901);
        return new(
            "Only a record live in exactly the caller's scope gets a row, its scope copied from the record: not one of another " +
            "agent, not one in a narrower scope the bounds can see, not an ID with no record, and not a record named from " +
            "another scope.",
            [mine, otherAgents, narrower],
            [
                new RecordHoldoutStep(
                    Fixed.Scope,
                    run,
                    HoldoutArm.Holdout,
                    [new(mine.ExperienceId, 1), new(otherAgents.ExperienceId, 1), new(narrower.ExperienceId, 1), new(Fixed.Id(f, 9), 1)],
                    Fixed.T0.AddHours(1)),
                Step(OtherAgent, Fixed.Id(f, 0x902), HoldoutArm.Treatment, [mine], Fixed.T0.AddHours(2)),
            ],
            []);
    }

    private static HoldoutRecordCase RenderedRevisionKept()
    {
        const int f = 0x72;
        var a = Record(f, 1) with { Revision = 4 };
        var b = Record(f, 2);
        return new(
            "The row keeps the revision the caller names (the one the block rendered), not the record's current revision.",
            [a, b],
            [
                new RecordHoldoutStep(Fixed.Scope, Fixed.Id(f, 0x901), HoldoutArm.Treatment, [new(a.ExperienceId, 2), new(b.ExperienceId, 1)], Fixed.T0.AddHours(1)),
            ],
            []);
    }

    private static HoldoutRecordCase ClampedToTheDatabaseClock()
    {
        const int f = 0x73;
        var a = Record(f, 1);
        var run = Fixed.Id(f, 0x901);
        return new(
            "An instant ahead of the database clock is stored as the database clock (clock_timestamp()) at the statement.",
            [a],
            [Step(Fixed.Scope, run, HoldoutArm.Holdout, [a], new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_990))],
            [(run, a.ExperienceId)]);
    }

    private static HoldoutRecordCase ErasedLesson()
    {
        const int f = 0x74;
        var (a, b) = (Record(f, 1), Record(f, 2));
        var (first, second) = (Fixed.Id(f, 0x901), Fixed.Id(f, 0x902));
        return new(
            "Erasing a lesson removes every row that names it, and an erased record is never recorded again: the statement " +
            "selects live records only.",
            [a, b],
            [
                Step(Fixed.Scope, first, HoldoutArm.Treatment, [a, b], Fixed.T0.AddHours(1)),
                new EraseStep(a.ExperienceId, a.Scope, Fixed.T0.AddHours(2)),
                Step(Fixed.Scope, second, HoldoutArm.Holdout, [a, b], Fixed.T0.AddHours(3)),
            ],
            []);
    }

    private static HoldoutRecordCase ErasedRun()
    {
        const int f = 0x75;
        var (a, b) = (Record(f, 1), Record(f, 2));

        // The runs' own records: the records finalized runs 0x103 and 0x104 wrote. Each one's source_run_id is its run.
        var (own, ownOfAnother) = (Record(f, 3), Record(f, 4));
        var unfinalized = Fixed.Id(f, 0x901);
        return new(
            "A run's rows go when its own record (the one whose source_run_id is the run) is erased. The late-write guard: a " +
            "write for a run whose own record is being erased waits for the erasure, then records nothing. A run with no " +
            "record yet is recorded.",
            [a, b, own, ownOfAnother],
            [
                Step(Fixed.Scope, own.SourceRunId, HoldoutArm.Treatment, [a], Fixed.T0.AddHours(1)),
                new EraseStep(own.ExperienceId, own.Scope, Fixed.T0.AddHours(2)),
                new EraseDuringRecordStep(
                    new EraseStep(ownOfAnother.ExperienceId, ownOfAnother.Scope, Fixed.T0.AddHours(3)),
                    Step(Fixed.Scope, ownOfAnother.SourceRunId, HoldoutArm.Treatment, [a, b], Fixed.T0.AddHours(3))),
                Step(Fixed.Scope, unfinalized, HoldoutArm.Holdout, [b], Fixed.T0.AddHours(4)),
            ],
            []);
    }

    private static RecordHoldoutStep Step(Scope scope, Guid runId, HoldoutArm arm, IReadOnlyList<ExperienceRecord> records, DateTimeOffset at) =>
        new(scope, runId, arm, [.. records.Select(record => new RunExposure(record.ExperienceId, record.Revision))], at);

    private static ExperienceRecord Record(int family, int n, Scope? scope = null) =>
        SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", scope: scope);

    /// <summary><c>seed.json</c>: the records as stored.</summary>
    public static JsonObject ToSeedJson(HoldoutRecordCase definition) => new()
    {
        ["records"] = SearchContract.ToSeedJson(definition.Records),
    };

    /// <summary><c>steps.json</c>: the description and the steps, in order.</summary>
    public static JsonObject ToStepsJson(HoldoutRecordCase definition) => new()
    {
        ["description"] = definition.Description,
        ["steps"] = new JsonArray([.. definition.Steps.Select(step => (JsonNode)StepJson(step))]),
    };

    private static JsonObject StepJson(InjectionStep step) => step switch
    {
        RecordHoldoutStep record => new JsonObject { ["record"] = RecordJson(record) },
        EraseStep => InjectionRecordContract.StepJson(step),
        EraseDuringRecordStep race => new JsonObject
        {
            ["eraseDuringRecord"] = new JsonObject
            {
                ["erase"] = InjectionRecordContract.StepJson(race.Erase)["erase"]!.DeepClone(),
                ["record"] = RecordJson(race.Record),
            },
        },
        _ => throw new InvalidOperationException($"Unknown step {step}."),
    };

    private static JsonObject RecordJson(RecordHoldoutStep record) => new()
    {
        ["scope"] = JsonSerializer.SerializeToNode(record.Scope, ContractJson.Options),
        ["runId"] = Id(record.RunId),
        ["arm"] = record.Arm.ToString(),
        ["records"] = new JsonArray([.. record.Records.Select(exposure => (JsonNode)new JsonObject
        {
            ["experienceId"] = Id(exposure.ExperienceId),
            ["revision"] = exposure.Revision,
        })]),
        ["assignedAt"] = ContractJson.Timestamp(record.AssignedAt),
    };

    /// <summary>Reads <c>steps.json</c> back: the steps a runner runs.</summary>
    public static IReadOnlyList<InjectionStep> ReadSteps(JsonNode steps) =>
        [.. steps["steps"]!.AsArray().Select(step => ReadStep(step!))];

    private static InjectionStep ReadStep(JsonNode step) =>
        step["record"] is { } record ? ReadRecord(record)
        : step["eraseDuringRecord"] is { } race ? new EraseDuringRecordStep(
            (EraseStep)InjectionRecordContract.ReadStep(new JsonObject { ["erase"] = race["erase"]!.DeepClone() }),
            ReadRecord(race["record"]!))
        : InjectionRecordContract.ReadStep(step);

    private static RecordHoldoutStep ReadRecord(JsonNode record) => new(
        InjectionRecordContract.ScopeOf(record["scope"]!),
        Guid.Parse(record["runId"]!.GetValue<string>()),
        Enum.Parse<HoldoutArm>(record["arm"]!.GetValue<string>()),
        [.. record["records"]!.AsArray().Select(exposure => new RunExposure(
            Guid.Parse(exposure!["experienceId"]!.GetValue<string>()),
            exposure["revision"]!.GetValue<long>()))],
        DateTimeOffset.Parse(record["assignedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
