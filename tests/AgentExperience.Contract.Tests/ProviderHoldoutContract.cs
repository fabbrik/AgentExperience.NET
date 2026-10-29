using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Contract.Tests;

/// <summary>Contract 1.11's files.</summary>
internal static class ProviderHoldoutStatements
{
    /// <summary>The statement that records a run's arm in a memory provider's holdout.</summary>
    public const string RecordFile = "sql/record-provider-holdout-assignment.sql";

    /// <summary>The statements that read one provider's holdout rows a page at a time.</summary>
    public const string QueryFile = "sql/query-provider-holdout-outcomes.sql";

    /// <summary>The application role's grants on <c>0029</c>'s table.</summary>
    public const string PrivilegesFile = "sql/application-role-privileges-1.11.sql";

    /// <summary>The table <c>0029</c> creates.</summary>
    public const string Table = "experience_provider_holdout_assignments";
}

/// <summary>
/// Contract 1.11's <c>provider-holdout-assign</c> family: one file, <c>cases.json</c>, of run IDs, provider keys and
/// salts with the provider salt the .NET wrapper derives (<see cref="ExperienceHoldoutContextProvider.ProviderSalt"/>),
/// the fraction the assignment function gives for it, and the arm at each of a list of rates (the last of them the
/// fraction itself). Needs no database.
/// </summary>
internal static class ProviderHoldoutAssignContract
{
    public const string Family = "provider-holdout-assign";

    public const string FileName = "cases.json";

    /// <summary>The run IDs: a few the 1.8 family uses, and two of the shape the other fixtures use.</summary>
    public static IReadOnlyList<Guid> RunIds { get; } =
    [
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479"),
        Guid.Parse("c9a646d3-9c61-4cb7-bfcd-ee2522c8f633"),
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
        Fixed.Id(0xd0, 0x101),
        Fixed.Id(0xd0, 0x102),
    ];

    /// <summary>
    /// The (key, salt) pairs. The first pairs are keys a host would choose, with the default salt and others; the last
    /// ones are pairs whose plain concatenation <c>key/salt</c> would collide (<c>"a/b"+"c"</c> against
    /// <c>"a"+"b/c"</c>, <c>"a:b"+"c"</c> against <c>"a"+"b:c"</c>), which the length prefix keeps apart. Those keys are
    /// not valid provider keys (<c>validKey</c> is false): a wrapper refuses them, but the derivation is defined for any
    /// string, and a port's derivation must give the same salt.
    /// </summary>
    public static IReadOnlyList<(string Key, string Salt)> Pairs { get; } =
    [
        ("mem0", ExperienceHoldoutOptions.DefaultSalt),
        ("zep", ExperienceHoldoutOptions.DefaultSalt),
        ("mem0", "another-experiment"),
        ("Letta.v2_prod-1", "expérience-実験-🧪"),
        (new string('k', ProviderHoldoutAssignment.MaxProviderKeyLength), ExperienceHoldoutOptions.DefaultSalt),
        ("a", "b/c"),
        ("a/b", "c"),
        ("a", "b:c"),
        ("a:b", "c"),
        ("1:a", "b"),
        ("1", "a/b"),
    ];

    /// <summary>The rates every case is assigned at, before the case's own fraction.</summary>
    public static IReadOnlyList<double> Rates { get; } = [0, 0.1, 0.5, 0.9, 1];

    /// <summary>The whole file: a description and every run ID × pair.</summary>
    public static JsonObject Run() => new()
    {
        ["description"] =
            "providerSalt = the key's length in UTF-16 code units, in decimal, then ':', the key, '/', and the holdout's salt. " +
            "The fraction and the arm are README-1.8's assignment function over the run ID and providerSalt: SHA-256 over the " +
            "run ID's 16 bytes in RFC 4122 order, then providerSalt's UTF-8 bytes; the digest's first 8 bytes as a big-endian " +
            "unsigned integer, divided by 2^64. The arm at a rate is Holdout when the rate is at least 1 or the fraction is " +
            "strictly below the rate, else Treatment. Each case's last rate is its own fraction (Treatment). validKey says " +
            "whether the key is a valid provider key; a wrapper refuses one that is not.",
        ["cases"] = new JsonArray([.. RunIds.SelectMany(runId => Pairs.Select(pair => (JsonNode)Case(runId, pair.Key, pair.Salt)))]),
    };

    private static JsonObject Case(Guid runId, string key, string salt)
    {
        var providerSalt = ExperienceHoldoutContextProvider.ProviderSalt(key, salt);
        var fraction = HoldoutAssignmentFunction.Fraction(runId, providerSalt);
        return new JsonObject
        {
            ["runId"] = runId.ToString("D", CultureInfo.InvariantCulture),
            ["providerKey"] = key,
            ["salt"] = salt,
            ["validKey"] = ProviderHoldoutAssignment.IsValidProviderKey(key),
            ["providerSalt"] = providerSalt,
            ["fraction"] = fraction,
            ["arms"] = new JsonArray([.. Rates.Append(fraction).Select(rate => (JsonNode)new JsonObject
            {
                ["rate"] = rate,
                ["arm"] = HoldoutAssignmentFunction.Assign(runId, providerSalt, rate).ToString(),
            })]),
        };
    }

    /// <summary>Every problem with a checked-in file: a salt, fraction or arm the library no longer gives.</summary>
    public static void Recheck(string path, string text, List<string> drift)
    {
        foreach (var entry in JsonNode.Parse(text)!["cases"]!.AsArray())
        {
            var runId = Guid.Parse(entry!["runId"]!.GetValue<string>());
            var key = entry["providerKey"]!.GetValue<string>();
            var salt = entry["salt"]!.GetValue<string>();
            var providerSalt = ExperienceHoldoutContextProvider.ProviderSalt(key, salt);
            if (providerSalt != entry["providerSalt"]!.GetValue<string>())
            {
                drift.Add($"{path}: key '{key}' with salt '{salt}' now derives '{providerSalt}'.");
            }

            if (ProviderHoldoutAssignment.IsValidProviderKey(key) != entry["validKey"]!.GetValue<bool>())
            {
                drift.Add($"{path}: key '{key}' is no longer {(entry["validKey"]!.GetValue<bool>() ? "valid" : "invalid")}.");
            }

            var fraction = HoldoutAssignmentFunction.Fraction(runId, providerSalt);
            if (fraction.ToString("G15", CultureInfo.InvariantCulture) != entry["fraction"]!.GetValue<double>().ToString("G15", CultureInfo.InvariantCulture))
            {
                drift.Add($"{path}: {runId} with key '{key}' and salt '{salt}' is now at {fraction}.");
            }

            foreach (var arm in entry["arms"]!.AsArray())
            {
                var rate = arm!["rate"]!.GetValue<double>();
                var now = HoldoutAssignmentFunction.Assign(runId, providerSalt, rate).ToString();
                if (now != arm["arm"]!.GetValue<string>())
                {
                    drift.Add($"{path}: {runId} with key '{key}' and salt '{salt}' at rate {rate} is now {now}.");
                }
            }
        }
    }
}

/// <summary>Runs <c>sql/record-provider-holdout-assignment.sql</c> once, for <paramref name="Scope"/>, under bounds declared for it.</summary>
internal sealed record RecordProviderHoldoutStep(Scope Scope, Guid RunId, string ProviderKey, HoldoutArm Arm, DateTimeOffset AssignedAt) : InjectionStep;

/// <summary>
/// The late-write guard's race: <paramref name="Erase"/> starts (as the owner, uncommitted), <paramref name="Record"/>
/// starts and waits on the erasure's lock, then the erasure commits and the record call completes.
/// </summary>
internal sealed record EraseDuringProviderRecordStep(EraseStep Erase, RecordProviderHoldoutStep Record) : InjectionStep;

/// <summary>
/// A provider-holdout-record case: the records seeded (as stored), the steps, and the (tenant, run, key) rows whose
/// <c>assigned_at</c> is the database clock at the step that clamped it.
/// </summary>
internal sealed record ProviderHoldoutRecordCase(
    string Description,
    IReadOnlyList<ExperienceRecord> Records,
    IReadOnlyList<InjectionStep> Steps,
    IReadOnlyList<(string TenantId, Guid RunId, string ProviderKey)> Clamped);

/// <summary>
/// Contract 1.11's provider-holdout-record family. Running it needs PostgreSQL, so the PostgreSQL contract test runs it.
/// Each case writes <c>seed.json</c> (<c>records</c>, stored records as a search seed holds them), <c>steps.json</c> (the
/// description and the steps, in order) and <c>expected.json</c> (every row of
/// <c>agent_experience.experience_provider_holdout_assignments</c> afterwards, ordered by <c>tenant_id</c>, <c>run_id</c>
/// then <c>provider_key</c>, every column in PostgreSQL's text form in UTC; a clamped instant is
/// <see cref="InjectionRecordContract.DatabaseClock"/>).
/// </summary>
internal static class ProviderHoldoutRecordContract
{
    public const string Family = "provider-holdout-record";

    /// <summary>The columns of <c>agent_experience.experience_provider_holdout_assignments</c>, in table order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "run_id", "provider_key", "arm", "tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id", "assigned_at",
    ];

    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };

    private static readonly Scope OtherTenant = Fixed.Scope with { TenantId = "contract-tenant-2" };

    /// <summary>Every provider-holdout-record case, by directory name.</summary>
    public static IReadOnlyList<(string Name, ProviderHoldoutRecordCase Case)> Cases { get; } =
    [
        ("duplicate-keeps-the-first-row", DuplicateKeepsTheFirstRow()),
        ("exact-scope", ExactScope()),
        ("the-key", TheKey()),
        ("clamped-to-the-database-clock", ClampedToTheDatabaseClock()),
        ("erased-run", ErasedRun()),
    ];

    private static ProviderHoldoutRecordCase DuplicateKeepsTheFirstRow()
    {
        const int f = 0xd1;
        var run = Fixed.Id(f, 0x901);
        return new(
            "One row per run and key: a run recorded again under the same key keeps its first row (arm and instant), " +
            "whatever the later statement names. The instant is stored truncated to whole microseconds. No record is read: a " +
            "run with no record yet is recorded.",
            [],
            [
                new RecordProviderHoldoutStep(Fixed.Scope, run, "mem0", HoldoutArm.Treatment, Fixed.T0.AddHours(1).AddTicks(1_234_567)),
                new RecordProviderHoldoutStep(Fixed.Scope, run, "mem0", HoldoutArm.Holdout, Fixed.T0.AddHours(2)),
                new RecordProviderHoldoutStep(Fixed.Scope, run, "mem0", HoldoutArm.Treatment, Fixed.T0.AddMinutes(30)),
            ],
            []);
    }

    private static ProviderHoldoutRecordCase ExactScope()
    {
        const int f = 0xd2;
        var team = Fixed.Scope with { TeamId = "team-1" };
        var user = new Scope(Fixed.Scope.TenantId, Fixed.Scope.ApplicationId, Fixed.Scope.ProjectId, "team-1", "refund-agent", "user-1");
        var unset = new Scope(Fixed.Scope.TenantId, Fixed.Scope.ApplicationId, Fixed.Scope.ProjectId);
        return new(
            "A row holds exactly the scope the run is captured in (the caller's scope, which is also its bounds), every unset " +
            "field NULL: an agent's scope, a narrower one, one with every field set, and one with only the three required.",
            [],
            [
                new RecordProviderHoldoutStep(Fixed.Scope, Fixed.Id(f, 0x901), "mem0", HoldoutArm.Holdout, Fixed.T0.AddHours(1)),
                new RecordProviderHoldoutStep(team, Fixed.Id(f, 0x902), "mem0", HoldoutArm.Treatment, Fixed.T0.AddHours(2)),
                new RecordProviderHoldoutStep(user, Fixed.Id(f, 0x903), "mem0", HoldoutArm.Treatment, Fixed.T0.AddHours(3)),
                new RecordProviderHoldoutStep(unset, Fixed.Id(f, 0x904), "mem0", HoldoutArm.Holdout, Fixed.T0.AddHours(4)),
            ],
            []);
    }

    private static ProviderHoldoutRecordCase TheKey()
    {
        const int f = 0xd3;
        var run = Fixed.Id(f, 0x901);
        return new(
            "The row's key is (tenant_id, run_id, provider_key): the same run under another provider key gets a row of its own, " +
            "and so does the same run and key in another tenant (a run ID is the caller's value, so another tenant's reuse of " +
            "it never suppresses this tenant's row); the same run and key from another scope of the same tenant writes nothing.",
            [],
            [
                new RecordProviderHoldoutStep(Fixed.Scope, run, "mem0", HoldoutArm.Treatment, Fixed.T0.AddHours(1)),
                new RecordProviderHoldoutStep(Fixed.Scope, run, "zep", HoldoutArm.Holdout, Fixed.T0.AddHours(2)),
                new RecordProviderHoldoutStep(OtherTenant, run, "mem0", HoldoutArm.Holdout, Fixed.T0.AddHours(3)),
                new RecordProviderHoldoutStep(OtherAgent, run, "mem0", HoldoutArm.Holdout, Fixed.T0.AddHours(4)),
            ],
            []);
    }

    private static ProviderHoldoutRecordCase ClampedToTheDatabaseClock()
    {
        const int f = 0xd4;
        var run = Fixed.Id(f, 0x901);
        return new(
            "An instant ahead of the database clock is stored as the database clock (clock_timestamp()) at the statement.",
            [],
            [new RecordProviderHoldoutStep(Fixed.Scope, run, "mem0", HoldoutArm.Holdout, new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_990))],
            [(Fixed.Scope.TenantId, run, "mem0")]);
    }

    private static ProviderHoldoutRecordCase ErasedRun()
    {
        const int f = 0xd5;

        // Runs A and D have two records of their own; run B has one, and another tenant has a record naming the same run
        // ID; run C's record is erased while its row is being written; a lesson is no run's record.
        var (runA, runB, runC, runD) = (Fixed.Id(f, 0x901), Fixed.Id(f, 0x902), Fixed.Id(f, 0x903), Fixed.Id(f, 0x904));
        var a1 = OfRun(Record(f, 1), runA);
        var a2 = OfRun(Record(f, 2), runA);
        var b = OfRun(Record(f, 3), runB);
        var bElsewhere = OfRun(Record(f, 4, OtherTenant), runB);
        var c = OfRun(Record(f, 5), runC);
        var lesson = Record(f, 6);
        var d1 = OfRun(Record(f, 7), runD);
        var d2 = OfRun(Record(f, 8), runD);
        return new(
            "The erasure trigger: erasing a record removes the rows of the run it was captured from (its source_run_id), in " +
            "its own tenant only, and only when no live record of that run remains there. Erasing one of run A's two records " +
            "keeps its rows (run D ends that way); erasing the second removes them, every key. Erasing another tenant's record of run B removes that " +
            "tenant's row only. Erasing a record that is no run's leaves every row. The late-write guard: a write for a run " +
            "whose own record is being erased waits for the erasure, then records nothing.",
            [a1, a2, b, bElsewhere, c, lesson, d1, d2],
            [
                new RecordProviderHoldoutStep(Fixed.Scope, runA, "mem0", HoldoutArm.Treatment, Fixed.T0.AddHours(1)),
                new RecordProviderHoldoutStep(Fixed.Scope, runA, "zep", HoldoutArm.Holdout, Fixed.T0.AddHours(1)),
                new RecordProviderHoldoutStep(Fixed.Scope, runB, "mem0", HoldoutArm.Holdout, Fixed.T0.AddHours(1)),
                new RecordProviderHoldoutStep(OtherTenant, runB, "mem0", HoldoutArm.Treatment, Fixed.T0.AddHours(1)),
                new RecordProviderHoldoutStep(Fixed.Scope, runD, "mem0", HoldoutArm.Treatment, Fixed.T0.AddHours(1)),
                new EraseStep(a1.ExperienceId, a1.Scope, Fixed.T0.AddHours(2)),
                new EraseStep(lesson.ExperienceId, lesson.Scope, Fixed.T0.AddHours(2)),
                new EraseStep(bElsewhere.ExperienceId, bElsewhere.Scope, Fixed.T0.AddHours(2)),
                new EraseStep(d1.ExperienceId, d1.Scope, Fixed.T0.AddHours(2)),
                new EraseStep(a2.ExperienceId, a2.Scope, Fixed.T0.AddHours(3)),
                new EraseDuringProviderRecordStep(
                    new EraseStep(c.ExperienceId, c.Scope, Fixed.T0.AddHours(4)),
                    new RecordProviderHoldoutStep(Fixed.Scope, runC, "mem0", HoldoutArm.Treatment, Fixed.T0.AddHours(4))),
            ],
            []);
    }

    private static ExperienceRecord OfRun(ExperienceRecord record, Guid run) =>
        record with { SourceRunId = run, Reflection = record.Reflection! with { ExperienceRunId = run } };

    private static ExperienceRecord Record(int family, int n, Scope? scope = null) =>
        SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", scope: scope);

    /// <summary><c>seed.json</c>: the records as stored.</summary>
    public static JsonObject ToSeedJson(ProviderHoldoutRecordCase definition) => new()
    {
        ["records"] = SearchContract.ToSeedJson(definition.Records),
    };

    /// <summary><c>steps.json</c>: the description and the steps, in order.</summary>
    public static JsonObject ToStepsJson(ProviderHoldoutRecordCase definition) => new()
    {
        ["description"] = definition.Description,
        ["steps"] = new JsonArray([.. definition.Steps.Select(step => (JsonNode)StepJson(step))]),
    };

    private static JsonObject StepJson(InjectionStep step) => step switch
    {
        RecordProviderHoldoutStep record => new JsonObject { ["record"] = RecordJson(record) },
        EraseStep => InjectionRecordContract.StepJson(step),
        EraseDuringProviderRecordStep race => new JsonObject
        {
            ["eraseDuringRecord"] = new JsonObject
            {
                ["erase"] = InjectionRecordContract.StepJson(race.Erase)["erase"]!.DeepClone(),
                ["record"] = RecordJson(race.Record),
            },
        },
        _ => throw new InvalidOperationException($"Unknown step {step}."),
    };

    private static JsonObject RecordJson(RecordProviderHoldoutStep record) => new()
    {
        ["scope"] = JsonSerializer.SerializeToNode(record.Scope, ContractJson.Options),
        ["runId"] = record.RunId.ToString("D", CultureInfo.InvariantCulture),
        ["providerKey"] = record.ProviderKey,
        ["arm"] = record.Arm.ToString(),
        ["assignedAt"] = ContractJson.Timestamp(record.AssignedAt),
    };

    /// <summary>Reads <c>steps.json</c> back: the steps a runner runs.</summary>
    public static IReadOnlyList<InjectionStep> ReadSteps(JsonNode steps) =>
        [.. steps["steps"]!.AsArray().Select(step => ReadStep(step!))];

    private static InjectionStep ReadStep(JsonNode step) =>
        step["record"] is { } record ? ReadRecord(record)
        : step["eraseDuringRecord"] is { } race ? new EraseDuringProviderRecordStep(
            (EraseStep)InjectionRecordContract.ReadStep(new JsonObject { ["erase"] = race["erase"]!.DeepClone() }),
            ReadRecord(race["record"]!))
        : InjectionRecordContract.ReadStep(step);

    private static RecordProviderHoldoutStep ReadRecord(JsonNode record) => new(
        InjectionRecordContract.ScopeOf(record["scope"]!),
        Guid.Parse(record["runId"]!.GetValue<string>()),
        record["providerKey"]!.GetValue<string>(),
        Enum.Parse<HoldoutArm>(record["arm"]!.GetValue<string>()),
        DateTimeOffset.Parse(record["assignedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}

/// <summary><c>options.json</c> of a provider-benefit-estimate case: the estimator's minimum.</summary>
internal sealed record ProviderBenefitEstimateOptions(int MinimumRunsPerArm);

/// <summary>A provider-benefit-estimate case: one provider's rows (in the store's run order) and the options.</summary>
internal sealed record ProviderBenefitEstimateCase(string Description, string ProviderKey, IReadOnlyList<ProviderHoldoutOutcomeRow> Rows, ProviderBenefitEstimateOptions Options);

/// <summary>
/// Contract 1.11's <c>provider-benefit-estimate</c> family: one provider's holdout rows, each with its run's outcome, and
/// the estimator's options in; the estimate <see cref="MicrosoftAgentFramework.AgentExperienceBenefit.EstimateProviderAsync"/>
/// folds from them out. Needs no database.
/// </summary>
internal static class ProviderBenefitEstimateContract
{
    public const string Family = "provider-benefit-estimate";

    private static readonly ProviderBenefitEstimateOptions Defaults = new(ExperienceBenefitOptions.DefaultMinimumRunsPerArm);

    /// <summary>Every provider-benefit-estimate case, by directory name.</summary>
    public static IReadOnlyList<(string Name, ProviderBenefitEstimateCase Case)> Cases { get; } =
    [
        ("helps", Simple(0xd8, "40 runs per arm, 85% with the provider against 45% without: the whole interval lies above zero, so it helps.", 40, 34, 40, 18)),
        ("hurts", Simple(0xd9, "40 runs per arm, 30% with the provider against 75% without: the whole interval lies below zero, so it hurts.", 40, 12, 40, 30)),
        ("spans-zero", Simple(0xda, "30 runs per arm, 60% against 53%: the interval spans zero, so the verdict is NoEvidence.", 30, 18, 30, 16)),
        ("below-the-run-minimum", Simple(0xdb, "19 treatment runs, one short of the default minimum of 20: NoEvidence although the interval lies above zero.", 19, 17, 25, 10)),
        ("custom-minimum", Simple(0xdc, "MinimumRunsPerArm is 5: six runs per arm, all against one verified, are enough for Helps.", 6, 6, 6, 1) with { Options = new(5) }),
        ("pending-and-unverified", PendingAndUnverified()),
        ("one-arm-empty", OneArmEmpty()),
        ("no-rows", new("No run was assigned under the key: every count is 0 and the verdict is NoEvidence.", "mem0", [], Defaults)),
    ];

    private static ProviderBenefitEstimateCase Simple(int f, string description, int treatmentRuns, int treatmentSuccesses, int holdoutRuns, int holdoutSuccesses)
    {
        var rows = new List<ProviderHoldoutOutcomeRow>();
        var n = 0;
        void Arm(HoldoutArm arm, int runs, int successes)
        {
            for (var i = 0; i < runs; i++)
            {
                rows.Add(Row(f, ++n, arm, i < successes ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed));
            }
        }

        Arm(HoldoutArm.Treatment, treatmentRuns, treatmentSuccesses);
        Arm(HoldoutArm.Holdout, holdoutRuns, holdoutSuccesses);
        return new(description, "mem0", Ordered(rows), Defaults);
    }

    private static ProviderBenefitEstimateCase PendingAndUnverified()
    {
        const int f = 0xdd;
        var rows = new List<ProviderHoldoutOutcomeRow>();
        for (var n = 1; n <= 50; n++)
        {
            TaskVerificationStatus? outcome = n % 7 == 0 ? null
                : n % 5 == 0 ? TaskVerificationStatus.Unknown
                : n % 3 == 0 ? TaskVerificationStatus.Failed
                : TaskVerificationStatus.Verified;
            rows.Add(Row(f, n, n % 2 == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout, outcome, "zep"));
        }

        return new(
            "Runs with no finalized record (runOutcome null) are Pending, runs whose record's outcome is Unknown are Unverified; both are left out of the rates.",
            "zep",
            Ordered(rows),
            Defaults);
    }

    private static ProviderBenefitEstimateCase OneArmEmpty()
    {
        const int f = 0xde;
        List<ProviderHoldoutOutcomeRow> rows = [.. Enumerable.Range(1, 10).Select(n => Row(f, n, HoldoutArm.Treatment, n <= 7 ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed))];
        rows.Add(Row(f, 11, HoldoutArm.Holdout, null));
        return new(
            "No holdout run has a verified outcome yet: the holdout arm has 0 runs, so its rate, the difference and its interval are null, and the verdict is NoEvidence.",
            "mem0",
            Ordered(rows),
            Defaults);
    }

    private static ProviderHoldoutOutcomeRow Row(int f, int n, HoldoutArm arm, TaskVerificationStatus? outcome, string key = "mem0") =>
        new(Fixed.Id(f, 0x100 + n), key, arm, Fixed.T0.AddMinutes(n), outcome);

    private static List<ProviderHoldoutOutcomeRow> Ordered(List<ProviderHoldoutOutcomeRow> rows) => [.. rows.OrderBy(row => row.RunId)];

    /// <summary>
    /// The estimate, folded exactly as <c>EstimateProviderAsync</c> folds the store's pages: each row is its run, added
    /// to the 1.9 accumulator with no lesson, a page of <paramref name="pageSize"/> rows at a time.
    /// </summary>
    public static ExperienceProviderBenefitEstimate Estimate(ProviderBenefitEstimateCase definition, int pageSize = 1000)
    {
        var options = new ExperienceBenefitOptions { MinimumRunsPerArm = definition.Options.MinimumRunsPerArm };
        var accumulator = new ExperienceBenefitAccumulator(options);
        foreach (var page in definition.Rows.Chunk(pageSize))
        {
            accumulator.AddRange(page.Select(row => new HoldoutOutcomeRow(row.RunId, row.Arm, Guid.Empty, row.AssignedAt, row.RunOutcome)));
        }

        return new ExperienceProviderBenefitEstimate(definition.ProviderKey, accumulator.ToEstimate().Overall) { MinimumRunsPerArm = options.MinimumRunsPerArm };
    }

    /// <summary><c>rows.json</c>: the description, the key and the rows.</summary>
    public static JsonObject ToRowsJson(ProviderBenefitEstimateCase definition) => new()
    {
        ["description"] = definition.Description,
        ["providerKey"] = definition.ProviderKey,
        ["rows"] = new JsonArray([.. definition.Rows.Select(row => (JsonNode)RowJson(row))]),
    };

    /// <summary>One provider outcome row: <c>runId</c>, <c>providerKey</c>, <c>arm</c>, <c>assignedAt</c>, <c>runOutcome</c>.</summary>
    public static JsonObject RowJson(ProviderHoldoutOutcomeRow row) => new()
    {
        ["runId"] = HoldoutOutcomeRowJson.Id(row.RunId),
        ["providerKey"] = row.ProviderKey,
        ["arm"] = row.Arm.ToString(),
        ["assignedAt"] = ContractJson.Timestamp(row.AssignedAt),
        ["runOutcome"] = row.RunOutcome?.ToString(),
    };

    /// <summary>Reads <c>rows.json</c> back into a case.</summary>
    public static ProviderBenefitEstimateCase Read(JsonNode rows, ProviderBenefitEstimateOptions options) => new(
        rows["description"]!.GetValue<string>(),
        rows["providerKey"]!.GetValue<string>(),
        [.. rows["rows"]!.AsArray().Select(row => new ProviderHoldoutOutcomeRow(
            Guid.Parse(row!["runId"]!.GetValue<string>()),
            row["providerKey"]!.GetValue<string>(),
            Enum.Parse<HoldoutArm>(row["arm"]!.GetValue<string>()),
            DateTimeOffset.Parse(row["assignedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            row["runOutcome"] is { } outcome ? Enum.Parse<TaskVerificationStatus>(outcome.GetValue<string>()) : null))],
        options);

    /// <summary><c>expected.json</c>: <c>providerKey</c>, <c>minimumRunsPerArm</c> and <c>overall</c>, a 1.9 comparison.</summary>
    public static JsonObject EstimateJson(ExperienceProviderBenefitEstimate estimate) => new()
    {
        ["providerKey"] = estimate.ProviderKey,
        ["minimumRunsPerArm"] = estimate.MinimumRunsPerArm,
        ["overall"] = BenefitEstimateContract.ComparisonJson(estimate.Overall),
    };
}
