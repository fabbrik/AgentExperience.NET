using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Export;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// <c>options.json</c> of a lesson-export case: the scope exported (the identity's), the scope the identity's
/// authorization bounds (the same, but in the denied case), the clock the export and the sink read, the verdict's
/// minimum runs per arm (<see langword="null"/>: the benefit policy's default, 20), and whether the file existed before
/// the export (<c>previous.md</c>).
/// </summary>
internal sealed record LessonExportOptions(
    string Description,
    Scope Scope,
    Scope AuthorizedScope,
    DateTimeOffset Now,
    int? MinimumRunsPerArm,
    bool HasPreviousFile);

/// <summary>A lesson-export case: the records and holdout rows seeded, the options, and the file as it was before.</summary>
internal sealed record LessonExportCase(
    IReadOnlyList<ExperienceRecord> Records,
    IReadOnlyList<SeededAssignment> Assignments,
    LessonExportOptions Options,
    Func<IReadOnlyList<ExportedLesson>, string>? Previous = null);

/// <summary>What one export did: the result, and the file the sink left (<see langword="null"/> when none was written).</summary>
internal sealed record LessonExportRun(ExperienceLessonExportResult Result, string? File);

/// <summary>
/// Contract 1.14's <c>lesson-export</c> family: what <see cref="AgentExperienceLessons.ExportMeasuredAsync"/> hands a
/// <see cref="MarkdownLessonFileSink"/> for the records and holdout rows of a scope, and the file the sink writes. No
/// database: the records and rows are seeded into the .NET in-memory store (as a port's in-memory store holds
/// lessons-view seeds), and a real lessons view over a real context provider exports them to a real sink.
/// </summary>
internal static class LessonExportContract
{
    public const string Family = "lesson-export";

    /// <summary>The clock every case exports with.</summary>
    public static readonly DateTimeOffset Now = Fixed.T0.AddDays(1).AddTicks(1_234_567);

    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };

    /// <summary>Every lesson-export case, by directory name.</summary>
    public static IReadOnlyList<(string Name, LessonExportCase Case)> Cases { get; } =
    [
        ("measured-helps-only", Measured(0x140, "Only lessons the holdout measured as helping (at a minimum of 3 runs per arm), eligible for retrieval, with a verified outcome and in exactly the scope are exported, sorted by experience ID: the lesson measured as hurting, the one no run was assigned, the one below the confidence floor, the Stale one, the one whose outcome did not verify and the other agent's are not. The model-authored lesson's text is fenced.", minimum: 3)),
        ("nothing-measured-helpful", Measured(0x141, "The same lessons at the default minimum of 20 runs per arm: no verdict is Helps, so the set is empty; the sink is still called and writes a file with no lesson.", minimum: null)),
        ("scope-with-every-segment", EverySegment()),
        ("sync-against-previous-file", Previous(0x143)),
        ("hand-edited-previous-file", HandEdited(0x144)),
        ("denied", Denied()),
    ];

    /// <summary>The files of one case: <c>previous.md</c> only when the file existed before the export.</summary>
    public static IReadOnlyCollection<string> Files { get; } = ["seed.json", "options.json", "previous.md", "expected.json", "expected.md"];

    private static LessonExportCase Measured(int f, string description, int? minimum)
    {
        var (records, assignments) = MeasuredRecords(f, Fixed.Scope);
        return new LessonExportCase(records, assignments, Options(description, minimum));
    }

    private static LessonExportCase EverySegment()
    {
        const int f = 0x142;
        var scope = Fixed.Scope with { TeamId = "payments", UserId = "user-17" };
        var (records, assignments) = MeasuredRecords(f, scope);
        return new LessonExportCase(
            records,
            assignments,
            Options(
                "A scope that sets every segment (team and user too): the file's scope block names each, in order. A task ID and lesson text that look like Markdown (a heading, a link, a list, an HTML tag) stay inside the indented code block.",
                3) with
            { Scope = scope, AuthorizedScope = scope });
    }

    private static LessonExportCase Previous(int f)
    {
        var (records, assignments) = MeasuredRecords(f, Fixed.Scope);
        return new LessonExportCase(
            records,
            assignments,
            Options(
                "The file already holds an earlier export (previous.md): one lesson at an older revision (updated), one exactly as now (neither added nor updated), and one no longer in the set (removed); the lesson the earlier export lacked is added. The file is rewritten whole.",
                3) with
            { HasPreviousFile = true },
            current =>
            {
                // An earlier export by the same sink: the first lesson one revision older, the second as it is now, the
                // third left out, and a lesson that has since gone.
                var gone = current[0] with { ExperienceId = Fixed.Id(f, 0xfff), TaskId = "retired-task", LessonText = "Lesson: A lesson that has since gone.\n" };
                return WritePrevious(Fixed.Scope, [current[0] with { Revision = current[0].Revision - 1 }, current[1], gone], Now.AddDays(-1));
            });
    }

    private static LessonExportCase HandEdited(int f)
    {
        var (records, assignments) = MeasuredRecords(f, Fixed.Scope);
        return new LessonExportCase(
            records,
            assignments,
            Options(
                "The file was edited by hand (previous.md): text before the first heading, a heading whose ID is not a GUID in the D format (ignored, part of the section before it), a current lesson's heading in upper case (the same lesson, but its section differs, so updated), the same lesson's heading again (the first section counts), and an indented heading (not a heading). Every edit is lost.",
                3) with
            { HasPreviousFile = true },
            current =>
            {
                var first = current[0].ExperienceId.ToString("D", CultureInfo.InvariantCulture);
                return "# Notes kept by hand\n\nSome text an editor added.\n\n"
                    + "## Lesson " + first.ToUpperInvariant() + "\n\nRevision 1. Edited by hand.\n\n    Task: rotate-keys\n\n"
                    + "## Lesson 1234\n\nNot a lesson heading this sink writes.\n\n"
                    + "## Lesson " + first + "\n\nA second section for the same lesson.\n\n"
                    + "    ## Lesson " + Fixed.Id(f, 0xffe).ToString("D", CultureInfo.InvariantCulture) + "\n";
            });
    }

    private static LessonExportCase Denied()
    {
        var (records, assignments) = MeasuredRecords(0x145, Fixed.Scope);
        return new LessonExportCase(
            records,
            assignments,
            Options(
                "The identity's authorization does not cover the scope exported: the store answers Denied, nothing is read, the sink is not called and no file is written (expected.md is empty).",
                3) with
            { AuthorizedScope = OtherAgent });
    }

    private static LessonExportOptions Options(string description, int? minimum) =>
        new(description, Fixed.Scope, Fixed.Scope, Now, minimum, HasPreviousFile: false);

    /// <summary>
    /// Lessons and the runs that measured them: H (helps, failed once then worked), M (helps, model-authored), A (helps,
    /// Markdown-looking text), B (helps, below the confidence floor), S (helps, Stale), U (helps, but its outcome did not
    /// verify), G (hurts) and N (no assigned run); and another agent's lesson. Three verified treatment runs and three
    /// failed holdout runs measured every lesson but G, which three failed treatment runs and three verified holdout runs
    /// measured. Each run's own record carries its outcome.
    /// </summary>
    private static (List<ExperienceRecord> Records, List<SeededAssignment> Assignments) MeasuredRecords(int f, Scope scope)
    {
        ExperienceRecord Lesson(int n, string taskId, string lesson, string[][] attempts, bool modelAuthored = false, bool failFirst = false) =>
            InjectOptionsContract.Lesson(n, taskId, lesson, attempts, modelAuthored, failFirst, f, scope);

        var h = Lesson(1, "rotate-keys", "Rotate the keys in the documented order.", [["list_keys", "rotate_key"], ["list_keys", "wait_for_lock", "rotate_key"]], failFirst: true);
        var m = Lesson(2, "rotate-keys", "A model wrote this: announce the rotation before starting.", [["announce"]], modelAuthored: true);
        var a = Lesson(
            3,
            "# Rotate *now* [docs](https://example.invalid) <b>bold</b>",
            "# Not a heading\n- not a list\n[not a link](https://example.invalid) <script>alert(1)</script>",
            [["rotate_key"]]);
        var b = Lesson(4, "rotate-keys", "Below the floor.", [["rotate_key"]]) with { ReuseConfidence = 0.4, SupportingValidations = 1, Contradictions = 2 };
        var s = Lesson(5, "rotate-keys", "Stale advice.", [["rotate_key"]]) with { Status = ExperienceStatus.Stale };
        var u = Lesson(6, "rotate-keys", "Its outcome did not verify.", [["rotate_key"]]);
        u = u with { Outcome = u.Outcome with { Status = TaskVerificationStatus.Unknown } };
        var g = Lesson(7, "rotate-keys", "Rotate every key at once.", [["rotate_all"]]);
        var n = Lesson(8, "rotate-keys", "Never measured.", [["rotate_key"]]);
        var other = InjectOptionsContract.Lesson(9, "rotate-keys", "Another agent's lesson.", [["rotate_key"]], family: f, scope: scope with { AgentId = "other-agent" });

        var helping = new[] { h, m, a, b, s, u };
        var records = new List<ExperienceRecord> { h, m, a, b, s, u, g, n, other };
        var assignments = new List<SeededAssignment>();
        var next = 0x20;

        void Runs(IEnumerable<ExperienceRecord> lessons, HoldoutArm arm, bool verified)
        {
            for (var i = 0; i < 3; i++)
            {
                var run = SearchContract.Seed(
                    f,
                    next,
                    "rotate-keys",
                    "Rotate the signing keys",
                    "Verified after 1 attempt.",
                    verified ? ExperienceStatus.Validated : ExperienceStatus.Quarantined,
                    scope: scope);
                records.Add(run);
                foreach (var lesson in lessons)
                {
                    assignments.Add(new SeededAssignment(run.SourceRunId, lesson.ExperienceId, lesson.Revision, arm, scope, Fixed.T0.AddMinutes(next).AddTicks(1_234_560)));
                }

                next++;
            }
        }

        Runs(helping, HoldoutArm.Treatment, verified: true);
        Runs(helping, HoldoutArm.Holdout, verified: false);
        Runs([g], HoldoutArm.Treatment, verified: false);
        Runs([g], HoldoutArm.Holdout, verified: true);
        return (records, assignments);
    }

    /// <summary>A file an earlier export by the real sink would have written for <paramref name="lessons"/> at <paramref name="at"/>.</summary>
    private static string WritePrevious(Scope scope, IReadOnlyList<ExportedLesson> lessons, DateTimeOffset at)
    {
        var directory = Directory.CreateTempSubdirectory("lesson-export-previous-");
        try
        {
            var path = Path.Combine(directory.FullName, "lessons.md");
            new MarkdownLessonFileSink(path, new FixedTimeProvider(at)).SyncAsync(scope, lessons, CancellationToken.None).GetAwaiter().GetResult();
            return File.ReadAllText(path, Encoding.UTF8);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary><c>seed.json</c>: the records as stored and the holdout rows, in the lessons-view seed form (no injections).</summary>
    public static JsonObject ToSeedJson(LessonExportCase definition) =>
        LessonsViewContract.ToSeedJson(new LessonsViewCase(
            definition.Records,
            [],
            new LessonsViewQuery(string.Empty, definition.Options.Scope, null, 50, true, definition.Options.Now))
        {
            Assignments = definition.Assignments,
        });

    /// <summary>The file before the export, or <see langword="null"/> when there was none.</summary>
    public static string? PreviousFile(LessonExportCase definition)
    {
        if (definition.Previous is not { } previous)
        {
            return null;
        }

        // What the current export hands the sink, so the earlier file can differ from it on purpose.
        var current = RunAsync(definition with { Previous = null }, previousFile: null).GetAwaiter().GetResult().Result.Lessons;
        return previous(current);
    }

    /// <summary>
    /// Runs one case: the records and rows seeded into a .NET in-memory store on the case's clock, the lessons view over a
    /// context provider with the default retrieval policy, and <see cref="AgentExperienceLessons.ExportMeasuredAsync"/>
    /// into a <see cref="MarkdownLessonFileSink"/> on the same clock, writing to a file in a fresh directory that holds
    /// <paramref name="previousFile"/> first when there is one.
    /// </summary>
    public static async Task<LessonExportRun> RunAsync(LessonExportCase definition, string? previousFile)
    {
        var options = definition.Options;
        var clock = new FixedTimeProvider(options.Now);
        var store = new InMemoryExperienceRecordStore(clock);
        foreach (var record in definition.Records)
        {
            var created = await store.CreateAsync(SearchContractPostgresTests.Authorization(record.Scope), record, CancellationToken.None);
            if (created.Outcome != ExperienceStoreOutcome.Created)
            {
                throw new InvalidOperationException($"{record.ExperienceId} was not stored: {created.Outcome}.");
            }
        }

        foreach (var row in definition.Assignments)
        {
            var recorded = await store.RecordHoldoutAssignmentAsync(
                SearchContractPostgresTests.Authorization(row.Scope),
                row.Scope,
                new HoldoutAssignment(row.RunId, row.Arm, [new RunExposure(row.ExperienceId, row.Revision)], row.AssignedAt),
                CancellationToken.None);
            if (recorded.Outcome != ExperienceStoreOutcome.Committed)
            {
                throw new InvalidOperationException($"The assignment of {row.RunId} was not recorded: {recorded.Outcome}.");
            }
        }

        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(new InMemoryExperienceCandidateSource(store), RetrievalPolicy.Default, RankingWeights.Default, clock),
            store,
            new ExperienceInjectionOptions { ResolveRequest = _ => null, TimeProvider = clock });
        var lessons = new AgentExperienceLessons(store, provider, clock);

        var directory = Directory.CreateTempSubdirectory("lesson-export-");
        try
        {
            var path = Path.Combine(directory.FullName, "lessons.md");
            if (previousFile is not null)
            {
                await File.WriteAllTextAsync(path, previousFile, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            var result = await lessons.ExportMeasuredAsync(
                new ExperienceIdentity(SearchContractPostgresTests.Authorization(options.AuthorizedScope), options.Scope),
                new MarkdownLessonFileSink(path, clock),
                options.MinimumRunsPerArm is { } minimum ? new ExperienceBenefitOptions { MinimumRunsPerArm = minimum } : null);
            var written = result.Sync is null ? null : await File.ReadAllTextAsync(path, Encoding.UTF8);
            return new LessonExportRun(result, written);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// <c>expected.json</c>: the export's <c>outcome</c>, <c>holdoutTracked</c>, <c>exportedAt</c>, the <c>lessons</c>
    /// handed to the sink in order (<c>experienceId</c>, <c>revision</c>, <c>taskId</c>, <c>lessonText</c>,
    /// <c>measuredBenefit</c> in the benefit-estimate comparison form, <c>exportedAt</c>) and what the sink reported,
    /// <c>sync</c> (<c>added</c>, <c>updated</c>, <c>removed</c>; <c>null</c> when it was not called).
    /// </summary>
    public static JsonObject ExpectedJson(ExperienceLessonExportResult result) => new()
    {
        ["outcome"] = result.Outcome.ToString(),
        ["holdoutTracked"] = result.HoldoutTracked,
        ["exportedAt"] = ContractJson.Timestamp(result.ExportedAt),
        ["lessons"] = new JsonArray([.. result.Lessons.Select(lesson => (JsonNode)new JsonObject
        {
            ["experienceId"] = lesson.ExperienceId.ToString("D", CultureInfo.InvariantCulture),
            ["revision"] = lesson.Revision,
            ["taskId"] = lesson.TaskId,
            ["lessonText"] = lesson.LessonText,
            ["measuredBenefit"] = BenefitEstimateContract.ComparisonJson(lesson.MeasuredBenefit),
            ["exportedAt"] = ContractJson.Timestamp(lesson.ExportedAt),
        })]),
        ["sync"] = result.Sync is not { } sync
            ? null
            : new JsonObject { ["added"] = sync.Added, ["updated"] = sync.Updated, ["removed"] = sync.Removed },
    };
}
