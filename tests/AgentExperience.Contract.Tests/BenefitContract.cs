using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.MicrosoftAgentFramework;

namespace AgentExperience.Contract.Tests;

/// <summary>Contract 1.9's files.</summary>
internal static class BenefitStatements
{
    /// <summary>The statements that read the holdout's outcome rows a page at a time.</summary>
    public const string QueryFile = "sql/query-holdout-outcomes.sql";
}

/// <summary>
/// The JSON form of one holdout outcome row, as <c>rows.json</c> of a benefit-estimate case and <c>expected.json</c> of a
/// holdout-outcomes case hold it: <c>runId</c>, <c>arm</c>, <c>experienceId</c>, <c>assignedAt</c>, <c>runOutcome</c>
/// (<c>null</c> when the run has no record), <c>runModel</c> and <c>lessonModel</c> (<c>null</c> when none).
/// </summary>
internal static class HoldoutOutcomeRowJson
{
    public static JsonObject Write(HoldoutOutcomeRow row) => new()
    {
        ["runId"] = Id(row.RunId),
        ["arm"] = row.Arm.ToString(),
        ["experienceId"] = Id(row.ExperienceId),
        ["assignedAt"] = ContractJson.Timestamp(row.AssignedAt),
        ["runOutcome"] = row.RunOutcome?.ToString(),
        ["runModel"] = row.RunModel,
        ["lessonModel"] = row.LessonModel,
    };

    public static HoldoutOutcomeRow Read(JsonNode row) => new(
        Guid.Parse(row["runId"]!.GetValue<string>()),
        Enum.Parse<HoldoutArm>(row["arm"]!.GetValue<string>()),
        Guid.Parse(row["experienceId"]!.GetValue<string>()),
        DateTimeOffset.Parse(row["assignedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        row["runOutcome"] is { } outcome ? Enum.Parse<TaskVerificationStatus>(outcome.GetValue<string>()) : null)
    {
        RunModel = row["runModel"]?.GetValue<string>(),
        LessonModel = row["lessonModel"]?.GetValue<string>(),
    };

    public static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary><c>options.json</c> of a benefit-estimate case: the estimator's options.</summary>
internal sealed record BenefitEstimateOptions(int MinimumRunsPerArm, bool SplitByModel);

/// <summary>A benefit-estimate case: the rows (in the store's run-then-record order) and the options.</summary>
internal sealed record BenefitEstimateCase(string Description, IReadOnlyList<HoldoutOutcomeRow> Rows, BenefitEstimateOptions Options);

/// <summary>
/// Contract 1.9's <c>benefit-estimate</c> family: holdout outcome rows and the estimator's options in, the estimate
/// <see cref="ExperienceBenefitEstimator.Estimate"/> gives out. Needs no database.
/// </summary>
internal static class BenefitEstimateContract
{
    public const string Family = "benefit-estimate";

    private static readonly BenefitEstimateOptions Defaults = new(ExperienceBenefitOptions.DefaultMinimumRunsPerArm, SplitByModel: false);

    /// <summary>Every benefit-estimate case, by directory name.</summary>
    public static IReadOnlyList<(string Name, BenefitEstimateCase Case)> Cases { get; } =
    [
        ("below-the-run-minimum", Simple(0x90, "The treatment arm has 19 verified runs, one short of the default minimum of 20: the verdict is NoEvidence although the interval lies above zero. The interval is still given.", 19, 17, 25, 10)),
        ("helps", Simple(0x91, "40 runs per arm, 85% against 45%: the whole interval lies above zero, so the lessons help.", 40, 34, 40, 18)),
        ("hurts", Simple(0x92, "40 runs per arm, 30% against 75%: the whole interval lies below zero, so the lessons hurt.", 40, 12, 40, 30)),
        ("spans-zero", Simple(0x93, "30 runs per arm, 60% against 53%: the interval spans zero, so the verdict is NoEvidence.", 30, 18, 30, 16)),
        ("rates-of-one-and-zero", Simple(0x94, "Every treatment run verified and no holdout run did: the Wilson intervals stay inside 0..1 and the difference's interval inside -1..1.", 25, 25, 25, 0)),
        ("one-arm-empty", OneArmEmpty()),
        ("mixed-arm", MixedArm()),
        ("pending", Pending()),
        ("unverified", Unverified()),
        ("per-lesson", PerLesson()),
        ("split-by-model", SplitByModel()),
        ("custom-minimum", Simple(0x9b, "MinimumRunsPerArm is 5: six runs per arm, all against one verified, are enough for Helps.", 6, 6, 6, 1) with { Options = new(5, false) }),
        ("at-the-run-minimum", Simple(0x9c, "Exactly 20 verified runs per arm, the default minimum: enough for a verdict, and the interval lies above zero, so Helps.", 20, 18, 20, 6)),
        ("conflicting-run-outcomes", ConflictingRunOutcomes()),
        ("mixed-pending-unverified-by-model", MixedPendingUnverifiedByModel()),
        ("model-ordering", ModelOrdering()),
        ("lesson-models-within-a-run", LessonModelsWithinARun()),
    ];

    private static BenefitEstimateCase ConflictingRunOutcomes()
    {
        const int f = 0x9d;
        var (a, b, c) = (Fixed.Id(f, 1), Fixed.Id(f, 2), Fixed.Id(f, 3));
        var rows = new List<HoldoutOutcomeRow>
        {
            // Run 1: the row with the lowest experienceId that carries an outcome is b's (a's carries none): Failed.
            Row(f, 1, HoldoutArm.Treatment, a, null),
            Row(f, 1, HoldoutArm.Treatment, b, TaskVerificationStatus.Failed),
            Row(f, 1, HoldoutArm.Treatment, c, TaskVerificationStatus.Verified),

            // Run 2: a's Verified wins over c's Unknown.
            Row(f, 2, HoldoutArm.Holdout, a, TaskVerificationStatus.Verified),
            Row(f, 2, HoldoutArm.Holdout, c, TaskVerificationStatus.Unknown),

            // Run 3: a's Unknown wins over b's Verified, so the run is Unverified.
            Row(f, 3, HoldoutArm.Treatment, a, TaskVerificationStatus.Unknown),
            Row(f, 3, HoldoutArm.Treatment, b, TaskVerificationStatus.Verified),
        };
        return new(
            "Rows of one run that disagree on its outcome: the run reads the outcome of its row with the lowest experienceId among those that carry one, whatever the row order.",
            Ordered(rows),
            Defaults);
    }

    private static BenefitEstimateCase MixedPendingUnverifiedByModel()
    {
        const int f = 0x9e;
        var (a, b) = (Fixed.Id(f, 1), Fixed.Id(f, 2));
        var rows = new List<HoldoutOutcomeRow>();
        string?[] models = ["model-a", "model-b", null];
        for (var n = 1; n <= 30; n++)
        {
            var model = models[n % models.Length];
            var arm = n % 2 == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout;
            TaskVerificationStatus? outcome = (n % 5) switch
            {
                0 => null,
                1 => TaskVerificationStatus.Unknown,
                2 or 3 => TaskVerificationStatus.Verified,
                _ => TaskVerificationStatus.Failed,
            };
            rows.Add(Row(f, n, arm, a, outcome) with { RunModel = model, LessonModel = "model-a" });
            if (n % 4 == 0)
            {
                // A run recorded in both arms: its second row names lesson b from the other arm.
                rows.Add(Row(f, n, arm == HoldoutArm.Treatment ? HoldoutArm.Holdout : HoldoutArm.Treatment, b, outcome) with { RunModel = model, LessonModel = "model-b" });
            }
        }

        return new(
            "Mixed-arm, pending and unverified runs together, split by model: each run is counted in exactly one of them in its model's group and in every lesson pair it named, and the groups add up to the pooled counts.",
            Ordered(rows),
            Defaults with { SplitByModel = true });
    }

    private static BenefitEstimateCase ModelOrdering()
    {
        const int f = 0x9f;
        var (a, b) = (Fixed.Id(f, 1), Fixed.Id(f, 2));

        // Ordinal UTF-16 order: "Zeta" < "alpha" < "\U0001F600-model" (high surrogate U+D83D) < "ａ-model" (U+FF41).
        // By code point the astral model would sort last, and ignoring case "alpha" would come first.
        string[] models = ["ａ-model", "alpha", "\U0001F600-model", "Zeta"];
        var rows = new List<HoldoutOutcomeRow>();
        for (var n = 1; n <= 16; n++)
        {
            var model = models[n % models.Length];
            var arm = n % 2 == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout;
            var outcome = n % 3 == 0 ? TaskVerificationStatus.Failed : TaskVerificationStatus.Verified;
            rows.Add(Row(f, n, arm, a, outcome) with { RunModel = model, LessonModel = models[(n + 1) % models.Length] });
            rows.Add(Row(f, n, arm, b, outcome) with { RunModel = model, LessonModel = models[(n + 2) % models.Length] });
        }

        return new(
            "Models are ordered ordinally by UTF-16 code unit: upper case before lower case, and an astral character (a surrogate pair) before a BMP character above U+D7FF such as U+FF41.",
            Ordered(rows),
            Defaults with { SplitByModel = true });
    }

    private static BenefitEstimateCase LessonModelsWithinARun()
    {
        const int f = 0xa0;
        var lesson = Fixed.Id(f, 1);
        var rows = new List<HoldoutOutcomeRow>
        {
            // One run's rows for the same lesson disagree on its model: the ordinal least non-blank one is read.
            Row(f, 1, HoldoutArm.Treatment, lesson, TaskVerificationStatus.Verified) with { RunModel = "run-model", LessonModel = "model-y" },
            Row(f, 1, HoldoutArm.Treatment, lesson, TaskVerificationStatus.Verified) with { RunModel = "run-model", LessonModel = " " },
            Row(f, 1, HoldoutArm.Treatment, lesson, TaskVerificationStatus.Verified) with { RunModel = "run-model", LessonModel = "model-x" },

            // Only blank or absent lesson models: unknown.
            Row(f, 2, HoldoutArm.Holdout, lesson, TaskVerificationStatus.Failed) with { RunModel = "run-model", LessonModel = "\t" },
            Row(f, 2, HoldoutArm.Holdout, lesson, TaskVerificationStatus.Failed) with { RunModel = "run-model", LessonModel = null },

            // Another run reads its own least model for the same lesson.
            Row(f, 3, HoldoutArm.Holdout, lesson, TaskVerificationStatus.Verified) with { RunModel = "run-model", LessonModel = "model-y" },
        };
        return new(
            "One lesson's rows within a run carry different or blank lesson models: per run, the lesson's model is the ordinal least non-blank one, or unknown when every row's is blank or absent.",
            Ordered(rows),
            Defaults with { SplitByModel = true });
    }

    private static BenefitEstimateCase Simple(int f, string description, int treatmentRuns, int treatmentSuccesses, int holdoutRuns, int holdoutSuccesses)
    {
        var rows = new List<HoldoutOutcomeRow>();
        var lesson = Fixed.Id(f, 1);
        var n = 0;
        void Arm(HoldoutArm arm, int runs, int successes)
        {
            for (var i = 0; i < runs; i++)
            {
                rows.Add(Row(f, ++n, arm, lesson, i < successes ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed));
            }
        }

        Arm(HoldoutArm.Treatment, treatmentRuns, treatmentSuccesses);
        Arm(HoldoutArm.Holdout, holdoutRuns, holdoutSuccesses);
        return new(description, Ordered(rows), Defaults);
    }

    private static BenefitEstimateCase OneArmEmpty()
    {
        const int f = 0x95;
        var lesson = Fixed.Id(f, 1);
        List<HoldoutOutcomeRow> rows = [.. Enumerable.Range(1, 10).Select(n => Row(f, n, HoldoutArm.Treatment, lesson, n <= 7 ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed))];

        // The holdout arm has runs, but none with a verified outcome yet.
        rows.Add(Row(f, 11, HoldoutArm.Holdout, lesson, null));
        rows.Add(Row(f, 12, HoldoutArm.Holdout, lesson, TaskVerificationStatus.Unknown));
        return new(
            "No holdout run has a verified outcome (one is pending, one unverified): the holdout arm has 0 runs, so its rate, the difference and its interval are null, and the verdict is NoEvidence.",
            Ordered(rows),
            Defaults);
    }

    private static BenefitEstimateCase MixedArm()
    {
        const int f = 0x96;
        var (a, b) = (Fixed.Id(f, 1), Fixed.Id(f, 2));
        var rows = new List<HoldoutOutcomeRow>();
        for (var n = 1; n <= 24; n++)
        {
            rows.Add(Row(f, n, n % 2 == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout, a, n % 3 == 0 ? TaskVerificationStatus.Failed : TaskVerificationStatus.Verified));
        }

        // Runs recorded in both arms (the rate or salt changed while they ran): one row per arm, outcome or not.
        rows.Add(Row(f, 25, HoldoutArm.Treatment, a, TaskVerificationStatus.Verified));
        rows.Add(Row(f, 25, HoldoutArm.Holdout, b, TaskVerificationStatus.Verified));
        rows.Add(Row(f, 26, HoldoutArm.Holdout, a, null));
        rows.Add(Row(f, 26, HoldoutArm.Treatment, b, null));
        return new(
            "Two runs were recorded in both arms: each is counted once as MixedArm, overall and in every lesson it named, and left out of the rates.",
            Ordered(rows),
            Defaults);
    }

    private static BenefitEstimateCase Pending()
    {
        const int f = 0x97;
        var lesson = Fixed.Id(f, 1);
        var rows = new List<HoldoutOutcomeRow>();
        for (var n = 1; n <= 30; n++)
        {
            TaskVerificationStatus? outcome = n % 5 == 0 ? null : n % 2 == 0 ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed;
            rows.Add(Row(f, n, n <= 15 ? HoldoutArm.Treatment : HoldoutArm.Holdout, lesson, outcome));
        }

        return new(
            "Runs with no finalized record (runOutcome null) are counted as Pending and left out of the rates.",
            Ordered(rows),
            Defaults);
    }

    private static BenefitEstimateCase Unverified()
    {
        const int f = 0x98;
        var lesson = Fixed.Id(f, 1);
        var rows = new List<HoldoutOutcomeRow>();
        for (var n = 1; n <= 30; n++)
        {
            var outcome = n % 4 == 0 ? TaskVerificationStatus.Unknown : n % 3 == 0 ? TaskVerificationStatus.Failed : TaskVerificationStatus.Verified;
            rows.Add(Row(f, n, n <= 15 ? HoldoutArm.Treatment : HoldoutArm.Holdout, lesson, outcome));
        }

        return new(
            "Runs whose record's outcome is Unknown are counted as Unverified and left out of the rates.",
            Ordered(rows),
            Defaults);
    }

    private static BenefitEstimateCase PerLesson()
    {
        const int f = 0x99;
        var (a, b, c) = (Fixed.Id(f, 3), Fixed.Id(f, 1), Fixed.Id(f, 2));
        var rows = new List<HoldoutOutcomeRow>();
        for (var n = 1; n <= 60; n++)
        {
            var arm = n % 2 == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout;
            var success = arm == HoldoutArm.Treatment ? n % 10 != 0 : n % 3 == 0;
            var outcome = n % 17 == 0 ? (TaskVerificationStatus?)null : success ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed;
            rows.Add(Row(f, n, arm, a, outcome));
            if (n % 3 != 1)
            {
                rows.Add(Row(f, n, arm, b, outcome));
            }

            if (n <= 12)
            {
                rows.Add(Row(f, n, arm, c, outcome));
            }
        }

        return new(
            "Three lessons, each named by a different set of runs: each lesson's comparison counts exactly the runs whose blocks named it, and the lessons are ordered by experience ID.",
            Ordered(rows),
            Defaults);
    }

    private static BenefitEstimateCase SplitByModel()
    {
        const int f = 0x9a;
        var (a, b) = (Fixed.Id(f, 1), Fixed.Id(f, 2));
        var rows = new List<HoldoutOutcomeRow>();
        string?[] runModels = ["model-a", "model-b", null, "  "];
        for (var n = 1; n <= 176; n++)
        {
            // Every block of eight runs gives each model slot one run per arm: 22 runs per arm per slot.
            var arm = n % 2 == 0 ? HoldoutArm.Treatment : HoldoutArm.Holdout;
            var runModel = runModels[(n - 1) / 2 % runModels.Length];
            var success = runModel switch
            {
                // The lessons help model-a and hurt model-b; for the runs with no model they make no difference.
                "model-a" => arm == HoldoutArm.Treatment ? n % 7 != 0 : n % 5 == 0,
                "model-b" => arm == HoldoutArm.Treatment ? n % 5 == 0 : n % 7 != 0,
                _ => n % 3 != 0,
            };
            var outcome = success ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed;
            rows.Add(Row(f, n, arm, a, outcome) with { RunModel = runModel, LessonModel = "model-a" });
            if (n % 3 == 0)
            {
                rows.Add(Row(f, n, arm, b, outcome) with { RunModel = runModel, LessonModel = null });
            }
        }

        // A run whose rows disagree on the run's model reads the ordinal least non-blank one ("model-a"), whatever the
        // row order; a lesson whose rows disagree reads the same way ("model-c" over "model-d"), per run.
        rows.Add(Row(f, 177, HoldoutArm.Treatment, a, TaskVerificationStatus.Verified) with { RunModel = "model-b", LessonModel = "model-a" });
        rows.Add(Row(f, 177, HoldoutArm.Treatment, b, TaskVerificationStatus.Verified) with { RunModel = "model-a", LessonModel = "model-d" });
        rows.Add(Row(f, 178, HoldoutArm.Holdout, b, TaskVerificationStatus.Failed) with { RunModel = "\t", LessonModel = "model-c" });
        rows.Add(Row(f, 178, HoldoutArm.Holdout, a, TaskVerificationStatus.Failed) with { RunModel = null, LessonModel = "model-d" });
        return new(
            "Split by model: the runs per consuming model (no model, or a blank one, is 'unknown'), and each lesson's runs per (the model that learned it, the model that used it), ordered ordinally. The lessons help model-a and hurt model-b. Every group's counts sum to the pooled ones.",
            Ordered(rows),
            Defaults with { SplitByModel = true });
    }

    /// <summary>A row of run <paramref name="n"/> of family <paramref name="f"/>.</summary>
    private static HoldoutOutcomeRow Row(int f, int n, HoldoutArm arm, Guid lesson, TaskVerificationStatus? outcome) =>
        new(Fixed.Id(f, 0x100 + n), arm, lesson, Fixed.T0.AddMinutes(n), outcome);

    /// <summary>The rows in the order a store pages them: by run, then by record.</summary>
    private static List<HoldoutOutcomeRow> Ordered(List<HoldoutOutcomeRow> rows) =>
        [.. rows.OrderBy(row => row.RunId).ThenBy(row => row.ExperienceId)];

    /// <summary><c>rows.json</c>: the description and the rows.</summary>
    public static JsonObject ToRowsJson(BenefitEstimateCase definition) => new()
    {
        ["description"] = definition.Description,
        ["rows"] = new JsonArray([.. definition.Rows.Select(row => (JsonNode)HoldoutOutcomeRowJson.Write(row))]),
    };

    /// <summary>Reads <c>rows.json</c> back.</summary>
    public static IReadOnlyList<HoldoutOutcomeRow> ReadRows(JsonNode rows) =>
        [.. rows["rows"]!.AsArray().Select(row => HoldoutOutcomeRowJson.Read(row!))];

    public static ExperienceBenefitOptions ToOptions(BenefitEstimateOptions options) =>
        new() { MinimumRunsPerArm = options.MinimumRunsPerArm, SplitByModel = options.SplitByModel };

    /// <summary><c>expected.json</c>: the estimate, every field <see cref="ExperienceBenefitEstimate"/> has but <c>Tracked</c> and <c>Outcome</c>.</summary>
    public static JsonObject EstimateJson(ExperienceBenefitEstimate estimate) => new()
    {
        ["minimumRunsPerArm"] = estimate.MinimumRunsPerArm,
        ["overall"] = ComparisonJson(estimate.Overall),
        ["lessons"] = new JsonArray([.. estimate.Lessons.Select(lesson => (JsonNode)LessonJson(lesson))]),
        ["byConsumingModel"] = estimate.ByConsumingModel is null
            ? null
            : new JsonArray([.. estimate.ByConsumingModel.Select(model => (JsonNode)new JsonObject
            {
                ["model"] = model.Model,
                ["comparison"] = ComparisonJson(model.Comparison),
            })]),
    };

    /// <summary>One lesson of the estimate: <c>experienceId</c>, <c>comparison</c> and <c>byModelPair</c>.</summary>
    public static JsonObject LessonJson(ExperienceLessonBenefit lesson) => new()
    {
        ["experienceId"] = HoldoutOutcomeRowJson.Id(lesson.ExperienceId),
        ["comparison"] = ComparisonJson(lesson.Comparison),
        ["byModelPair"] = lesson.ByModelPair is null
            ? null
            : new JsonArray([.. lesson.ByModelPair.Select(pair => (JsonNode)new JsonObject
            {
                ["lessonModel"] = pair.LessonModel,
                ["runModel"] = pair.RunModel,
                ["comparison"] = ComparisonJson(pair.Comparison),
            })]),
    };

    private static JsonObject ComparisonJson(ExperienceBenefitComparison comparison) => new()
    {
        ["treatment"] = ArmJson(comparison.Treatment),
        ["holdout"] = ArmJson(comparison.Holdout),
        ["difference"] = comparison.Difference,
        ["lower"] = comparison.Lower,
        ["upper"] = comparison.Upper,
        ["verdict"] = comparison.Verdict.ToString(),
        ["unverified"] = comparison.Unverified,
        ["pending"] = comparison.Pending,
        ["mixedArm"] = comparison.MixedArm,
    };

    private static JsonObject ArmJson(ExperienceBenefitArm arm) => new()
    {
        ["runs"] = arm.Runs,
        ["successes"] = arm.Successes,
        ["rate"] = arm.Rate,
    };
}

/// <summary><c>query.json</c> of a holdout-outcomes case: the scope read (also the caller's bounds) and the page size.</summary>
internal sealed record HoldoutOutcomesQuery(string Description, Scope Scope, int Limit);

/// <summary>A holdout-outcomes case: the records and assignment rows seeded, and the query.</summary>
internal sealed record HoldoutOutcomesCase(IReadOnlyList<ExperienceRecord> Records, IReadOnlyList<SeededAssignment> Assignments, HoldoutOutcomesQuery Query)
{
    /// <summary>The records erased, in order, after the rows are seeded: each tombstoned as the owner (deleted_at set).</summary>
    public IReadOnlyList<ExperienceRecord> Erased { get; init; } = [];
}

/// <summary>One row of <c>agent_experience.experience_holdout_assignments</c>, as a case seeds it.</summary>
internal sealed record SeededAssignment(Guid RunId, Guid ExperienceId, long Revision, HoldoutArm Arm, Scope Scope, DateTimeOffset AssignedAt);

/// <summary>
/// Contract 1.9's <c>holdout-outcomes</c> family: what <c>sql/query-holdout-outcomes.sql</c>, paged and decoded, reads
/// for the records and assignment rows of a database. Running it needs PostgreSQL, so the PostgreSQL contract test runs
/// it. Each case writes <c>seed.json</c> (<c>records</c>, stored records as a search seed holds them, and
/// <c>assignments</c>, rows of the assignment table in PostgreSQL's text form), <c>query.json</c> and <c>expected.json</c>
/// (every page, with the cursor it was read after).
/// </summary>
internal static class HoldoutOutcomesContract
{
    public const string Family = "holdout-outcomes";

    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };

    private static readonly Scope Team = Fixed.Scope with { TeamId = "team-1" };

    /// <summary>When the seeded records were created, as <see cref="SearchContract.Seed"/> creates them.</summary>
    private static readonly DateTimeOffset Created = Fixed.T0.AddDays(-10);

    /// <summary>Every holdout-outcomes case, by directory name.</summary>
    public static IReadOnlyList<(string Name, HoldoutOutcomesCase Case)> Cases { get; } =
    [
        ("exact-scope-and-run-records", ExactScope()),
        ("models", Models()),
        ("paging-full-last-page", Paging(0x82, 4, "Eight rows read four at a time: the second page is full, so a third read is made, and it is empty. A run's rows may be split across pages; every row carries its run's outcome and model.")),
        ("paging-partial-last-page", Paging(0x83, 3, "Eight rows read three at a time: the third page has two rows, fewer than the limit, so the read stops there.")),
        ("erased-records", ErasedRecords()),
    ];

    private static HoldoutOutcomesCase ErasedRecords()
    {
        const int f = 0x84;
        var (a, b) = (Record(f, 1, metadata: Model("lesson-a")), Record(f, 2, metadata: Model("lesson-b")));
        var erasedEarlier = Record(f, 11, metadata: Model("erased-model")) with { CreatedAt = Created.AddHours(-1), UpdatedAt = Created.AddHours(-1) };
        var liveLater = Record(f, 12, status: ExperienceStatus.Quarantined, metadata: Model("live-model")) with { SourceRunId = erasedEarlier.SourceRunId };
        var erasedOnly = Record(f, 13);
        var noRecordRun = Fixed.Id(f, 0x900);
        return new(
            [a, b, erasedEarlier, liveLater, erasedOnly],
            [
                Assign(erasedEarlier.SourceRunId, a, HoldoutArm.Treatment, 1),
                Assign(erasedEarlier.SourceRunId, b, HoldoutArm.Treatment, 1),
                Assign(erasedOnly.SourceRunId, a, HoldoutArm.Holdout, 2),
                Assign(noRecordRun, a, HoldoutArm.Holdout, 3),
                Assign(noRecordRun, b, HoldoutArm.Holdout, 3),
            ],
            new(
                "Erased (tombstoned) records are never read: a run whose earliest record is erased reads its next live record's outcome and model; a run whose only record is erased loses its rows, and so does an erased lesson, as the erasure removes them.",
                Fixed.Scope,
                1000))
        {
            Erased = [erasedEarlier, erasedOnly, b],
        };
    }

    private static HoldoutOutcomesCase ExactScope()
    {
        const int f = 0x80;
        var lesson1 = Record(f, 1);
        var lesson2 = Record(f, 2);
        var lessonElsewhere = Record(f, 3, OtherAgent);
        var lessonInTeam = Record(f, 4, Team);

        // The runs' own records: record n's source_run_id is Fixed.Id(f, 0x100 + n).
        var verified = Record(f, 11);
        var failed = Record(f, 12, status: ExperienceStatus.Quarantined);
        var unknownEarlier = Record(f, 13, status: ExperienceStatus.Quarantined, outcome: TaskVerificationStatus.Unknown) with { CreatedAt = Created.AddHours(-1), UpdatedAt = Created.AddHours(-1) };
        var verifiedLater = Record(f, 14, status: ExperienceStatus.Quarantined, outcome: TaskVerificationStatus.Verified) with { SourceRunId = unknownEarlier.SourceRunId };
        var recordElsewhere = Record(f, 15, OtherAgent);
        var noRecordRun = Fixed.Id(f, 0x900);

        return new(
            [lesson1, lesson2, lessonElsewhere, lessonInTeam, verified, failed, unknownEarlier, verifiedLater, recordElsewhere],
            [
                Assign(verified.SourceRunId, lesson1, HoldoutArm.Treatment, 1),
                Assign(verified.SourceRunId, lesson2, HoldoutArm.Treatment, 1),
                Assign(failed.SourceRunId, lesson1, HoldoutArm.Holdout, 2),
                Assign(unknownEarlier.SourceRunId, lesson2, HoldoutArm.Treatment, 3),
                Assign(recordElsewhere.SourceRunId, lesson1, HoldoutArm.Holdout, 4),
                Assign(noRecordRun, lesson1, HoldoutArm.Treatment, 5),
                Assign(Fixed.Id(f, 0x901), lessonElsewhere, HoldoutArm.Treatment, 6),
                Assign(Fixed.Id(f, 0x902), lessonInTeam, HoldoutArm.Holdout, 7),
            ],
            new(
                "Only rows in exactly the scope read are returned, in (run, record) order. Each carries its run's outcome: the run's own live record in exactly that scope (Verified, Failed or Unknown), the earliest created when there are two; null when the run's only record is in another scope, or it has none.",
                Fixed.Scope,
                1000));
    }

    private static HoldoutOutcomesCase Models()
    {
        const int f = 0x81;
        var withModel = Record(f, 1, metadata: Model("lesson-model"));
        var blankModel = Record(f, 2, metadata: Model(" \t "));
        var noModel = Record(f, 3);
        var paddedModel = Record(f, 4, metadata: Model(" padded-model "));

        var runWithModel = Record(f, 11, metadata: Model("run-model"));
        var runBlank = Record(f, 12, status: ExperienceStatus.Quarantined, metadata: Model(""));
        var runNone = Record(f, 13);
        return new(
            [withModel, blankModel, noModel, paddedModel, runWithModel, runBlank, runNone],
            [
                Assign(runWithModel.SourceRunId, withModel, HoldoutArm.Treatment, 1),
                Assign(runWithModel.SourceRunId, blankModel, HoldoutArm.Treatment, 1),
                Assign(runBlank.SourceRunId, noModel, HoldoutArm.Holdout, 2),
                Assign(runBlank.SourceRunId, paddedModel, HoldoutArm.Holdout, 2),
                Assign(runNone.SourceRunId, withModel, HoldoutArm.Treatment, 3),
                Assign(Fixed.Id(f, 0x900), paddedModel, HoldoutArm.Holdout, 4),
            ],
            new(
                "runModel is the run's own record's environment metadata entry agentexperience.model, and lessonModel the named lesson's: null when the entry is absent, empty or white space only; any other value is returned exactly as stored, untrimmed.",
                Fixed.Scope,
                1000));
    }

    private static HoldoutOutcomesCase Paging(int f, int limit, string description)
    {
        var (a, b, c) = (Record(f, 1, metadata: Model("lesson-a")), Record(f, 2), Record(f, 3, metadata: Model("lesson-c")));
        var (run1, run2) = (Record(f, 11, metadata: Model("run-model-1")), Record(f, 12, status: ExperienceStatus.Quarantined));
        var run3 = Fixed.Id(f, 0x900);
        return new(
            [a, b, c, run1, run2],
            [
                Assign(run1.SourceRunId, a, HoldoutArm.Treatment, 1),
                Assign(run1.SourceRunId, b, HoldoutArm.Treatment, 1),
                Assign(run1.SourceRunId, c, HoldoutArm.Treatment, 1),
                Assign(run2.SourceRunId, a, HoldoutArm.Holdout, 2),
                Assign(run2.SourceRunId, b, HoldoutArm.Holdout, 2),
                Assign(run2.SourceRunId, c, HoldoutArm.Holdout, 2),
                Assign(run3, a, HoldoutArm.Treatment, 3),
                Assign(run3, c, HoldoutArm.Treatment, 3),
            ],
            new(description, Fixed.Scope, limit));
    }

    private static Dictionary<string, string> Model(string model) =>
        new(StringComparer.Ordinal) { ["region"] = "eu-west", [HoldoutOutcomeRow.ModelMetadataKey] = model };

    private static SeededAssignment Assign(Guid run, ExperienceRecord lesson, HoldoutArm arm, int minutes) =>
        new(run, lesson.ExperienceId, lesson.Revision, arm, lesson.Scope, Fixed.T0.AddMinutes(minutes).AddTicks(1_234_560));

    private static ExperienceRecord Record(
        int f,
        int n,
        Scope? scope = null,
        ExperienceStatus status = ExperienceStatus.Validated,
        TaskVerificationStatus? outcome = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var record = SearchContract.Seed(f, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", status: status, scope: scope, metadata: metadata);
        return outcome is { } status2 ? record with { Outcome = record.Outcome with { Status = status2 } } : record;
    }

    /// <summary><c>seed.json</c>: the records as stored and the assignment rows in PostgreSQL's text form.</summary>
    public static JsonObject ToSeedJson(HoldoutOutcomesCase definition) => new()
    {
        ["records"] = SearchContract.ToSeedJson(definition.Records),
        ["erased"] = new JsonArray([.. definition.Erased.Select(record => (JsonNode)new JsonObject
        {
            ["experienceId"] = HoldoutOutcomeRowJson.Id(record.ExperienceId),
            ["scope"] = JsonSerializer.SerializeToNode(record.Scope, ContractJson.Options),
            ["deletedAt"] = ContractJson.Timestamp(Fixed.T0.AddHours(1)),
        })]),
        ["assignments"] = new JsonArray([.. definition.Assignments.Select(row => (JsonNode)new JsonObject
        {
            ["run_id"] = HoldoutOutcomeRowJson.Id(row.RunId),
            ["experience_id"] = HoldoutOutcomeRowJson.Id(row.ExperienceId),
            ["revision"] = row.Revision.ToString(CultureInfo.InvariantCulture),
            ["arm"] = row.Arm.ToString(),
            ["tenant_id"] = row.Scope.TenantId,
            ["application_id"] = row.Scope.ApplicationId,
            ["project_id"] = row.Scope.ProjectId,
            ["team_id"] = row.Scope.TeamId,
            ["agent_id"] = row.Scope.AgentId,
            ["user_id"] = row.Scope.UserId,
            ["assigned_at"] = InjectionRecordContract.PostgresText(row.AssignedAt),
        })]),
    };

    /// <summary><c>query.json</c>.</summary>
    public static JsonNode ToQueryJson(HoldoutOutcomesQuery query) => JsonSerializer.SerializeToNode(query, ContractJson.Options)!;

    /// <summary>Reads <c>query.json</c> back.</summary>
    public static HoldoutOutcomesQuery ReadQuery(string json) => ContractJson.Read<HoldoutOutcomesQuery>(json);

    /// <summary>One page of <c>expected.json</c>: the cursor it was read after (<c>null</c> for the first) and its rows.</summary>
    public static JsonObject PageJson(HoldoutOutcomeCursor? after, IEnumerable<HoldoutOutcomeRow> rows) => new()
    {
        ["after"] = after is null
            ? null
            : new JsonObject { ["runId"] = HoldoutOutcomeRowJson.Id(after.RunId), ["experienceId"] = HoldoutOutcomeRowJson.Id(after.ExperienceId) },
        ["rows"] = new JsonArray([.. rows.Select(row => (JsonNode)HoldoutOutcomeRowJson.Write(row))]),
    };
}

/// <summary>
/// Contract 1.9's <c>model-metadata</c> family: one file, <c>cases.json</c>, of the key a captured run's model is recorded
/// under, how the model id a chat client reports is normalized, and how it joins the run's environment metadata.
/// </summary>
internal static class ModelMetadataContract
{
    public const string Family = "model-metadata";

    public const string FileName = "cases.json";

    /// <summary>The model ids a chat client may report.</summary>
    public static IReadOnlyList<string?> ModelIds { get; } =
    [
        "gpt-4o",
        "  claude-haiku  \n",
        " model-with-unicode-spaces ",
        "\u001fnot-trimmed\u001c",
        string.Empty,
        " \t\r\n ",
        null,
        new string('a', 300),
        new string('a', 255) + "\U0001F9EA" + "tail",
        new string('a', 250) + new string(' ', 10) + "tail",
        new string('m', 256),
    ];

    /// <summary>The environments the model joins: the host's metadata and the model id the chat client reports.</summary>
    public static IReadOnlyList<(Dictionary<string, string> Metadata, string? ModelId)> Environments { get; } =
    [
        (new(StringComparer.Ordinal) { ["region"] = "eu-west" }, "gpt-4o"),
        (new(StringComparer.Ordinal), " gpt-4o "),
        (new(StringComparer.Ordinal) { ["region"] = "eu-west", [HoldoutOutcomeRow.ModelMetadataKey] = "host-deployment" }, "gpt-4o"),
        (new(StringComparer.Ordinal) { [HoldoutOutcomeRow.ModelMetadataKey] = "  " }, "gpt-4o"),
        (new(StringComparer.Ordinal) { ["region"] = "eu-west" }, null),
        (new(StringComparer.Ordinal) { ["region"] = "eu-west" }, "   "),
    ];

    /// <summary>The whole file.</summary>
    public static JsonObject Run() => new()
    {
        ["description"] =
            "A captured run records the model its agent's chat client reports under the environment metadata key 'key': the " +
            "model id trimmed of white space at both ends, cut to at most 'maxLength' UTF-16 code units (never splitting a " +
            "surrogate pair) and trimmed at the end again; nothing when the id is absent or blank. 'normalize' maps a reported " +
            "id to the value recorded (null: nothing). 'environment' gives the host's metadata and the reported id, and the " +
            "metadata the run records: the host's own value under the key, even a blank one, is kept.",
        ["key"] = HoldoutOutcomeRow.ModelMetadataKey,
        ["maxLength"] = AgentModel.MaxLength,
        ["normalize"] = new JsonArray([.. ModelIds.Select(id => (JsonNode)new JsonObject
        {
            ["modelId"] = id,
            ["recorded"] = AgentModel.Normalize(id),
        })]),
        ["environment"] = new JsonArray([.. Environments.Select(entry => (JsonNode)new JsonObject
        {
            ["metadata"] = Metadata(entry.Metadata),
            ["modelId"] = entry.ModelId,
            ["expected"] = Metadata(AgentModel.WithModel(Fixed.Environment with { Metadata = entry.Metadata }, AgentModel.Normalize(entry.ModelId)).Metadata),
        })]),
    };

    private static JsonObject Metadata(IReadOnlyDictionary<string, string> metadata)
    {
        var json = new JsonObject();
        foreach (var (key, value) in metadata.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            json[key] = value;
        }

        return json;
    }

    /// <summary>Every problem with a checked-in file: a value the library no longer records.</summary>
    public static void Recheck(string path, string text, List<string> drift)
    {
        var root = JsonNode.Parse(text)!;
        if (root["key"]!.GetValue<string>() != HoldoutOutcomeRow.ModelMetadataKey || root["maxLength"]!.GetValue<int>() != AgentModel.MaxLength)
        {
            drift.Add($"{path}: the key or the maximum length changed.");
        }

        foreach (var entry in root["normalize"]!.AsArray())
        {
            var id = entry!["modelId"]?.GetValue<string>();
            if (AgentModel.Normalize(id) != entry["recorded"]?.GetValue<string>())
            {
                drift.Add($"{path}: '{id}' is now recorded as '{AgentModel.Normalize(id)}'.");
            }
        }

        foreach (var entry in root["environment"]!.AsArray())
        {
            var metadata = entry!["metadata"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);
            var recorded = AgentModel.WithModel(Fixed.Environment with { Metadata = metadata }, AgentModel.Normalize(entry["modelId"]?.GetValue<string>())).Metadata;
            if (!JsonNode.DeepEquals(Metadata(recorded), entry["expected"]))
            {
                drift.Add($"{path}: the environment {entry["metadata"]!.ToJsonString()} with {entry["modelId"]?.ToJsonString()} now records {Metadata(recorded).ToJsonString()}.");
            }
        }
    }
}
