using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Storage.Postgres;
using Npgsql;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 27.6c's cross-language proof (contract 1.17): reuse feedback submitted by a .NET, a Python and a Go agent on the
/// same lessons lands as the same rows and the same lifecycle effects, and every agent then reads the same state.
/// </summary>
/// <remarks>
/// <para>
/// It needs all three toolchains (the .NET SDK, uv with the Python package's <c>postgres</c> extra synced, and Go), so it
/// runs only when <c>AGENTEXPERIENCE_CROSS_LANGUAGE_REUSE_FEEDBACK=1</c> (CI's <c>cross-language-lifecycle</c> job sets it)
/// and passes without doing anything otherwise. The Python writer is <c>python/tests/cross-language/reuse_feedback.py</c>
/// and the Go writer <c>go/postgres/internal/reusefeedback</c>; each runs its own feedback service over its own
/// PostgreSQL ledger and lifecycle store, with host-trusted evidence <c>Counted</c> so the lifecycle effects are visible.
/// </para>
/// <para>
/// <b>One database, three agents.</b> Each submission is made by one agent and then made again, identically (its
/// exposures listed in another order), by the other two: the first is <c>Recorded</c> and the others
/// <c>AlreadyRecorded</c>, which the ledger answers only when the stored row equals, field by field, what the agent
/// would have written. A changed resubmission is a <c>Conflict</c> from every agent. Every agent then reads each
/// lesson's history and confidence, and all three read the same.
/// </para>
/// <para>
/// <b>Three databases, one agent each.</b> Each agent alone submits the same feedback into its own database, with the
/// same store clocks; the ledger, evidence, event and record tables the three leave are then identical, column by column.
/// </para>
/// </remarks>
[Collection(ContractPostgresCollection.Name)]
public sealed class CrossLanguageReuseFeedbackPostgresTests(ContractPostgresDatabase database)
{
    private const string Variable = "AGENTEXPERIENCE_CROSS_LANGUAGE_REUSE_FEEDBACK";
    private const int F = 0x1173;

    private static readonly string Repository = Path.GetDirectoryName(ContractFiles.Root)!;
    private static readonly Scope Scope = Fixed.Scope;
    private static readonly string[] Writers = ["dotnet", "python", "go"];
    private static readonly DateTimeOffset Clock = Fixed.T0.AddHours(5);
    private static readonly DateTimeOffset Observed = Fixed.T0.AddMinutes(30);
    private static readonly Guid Run = Fixed.Id(F, 0x5100);
    private static readonly Guid Round = Fixed.Id(F, 0x5200);

    private static readonly List<ExperienceRecord> Lessons =
        [.. Enumerable.Range(1, 3).Select(n => SearchContract.Seed(F, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt."))];

    [Fact]
    public async Task Feedback_from_dotnet_python_and_go_lands_as_the_same_rows_and_reads_the_same_everywhere()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("reuse-feedback-");
        try
        {
            await OneDatabaseAsync(directory.FullName);
            await ThreeDatabasesAsync(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task OneDatabaseAsync(string directory)
    {
        const string CaseName = "xl_reuse_feedback";
        var application = await database.CreateAsync(CaseName);
        await SeedAsync(application);
        var url = database.ApplicationUrlOf(CaseName);

        var submissions = Submissions();
        var step = 0;
        for (var i = 0; i < submissions.Count; i++)
        {
            var (feedback, principal, expected) = submissions[i];

            // Each agent in turn goes first; the others then submit the same thing, its exposures listed in reverse.
            var order = Writers.Skip(i % Writers.Length).Concat(Writers.Take(i % Writers.Length)).ToList();
            var results = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var writer in order)
            {
                var submitted = writer == order[0] ? feedback : feedback with { ExposedExperienceIds = [.. feedback.ExposedExperienceIds.Reverse()] };
                var steps = new JsonArray(Submit(submitted, principal, Clock.AddMinutes(step++)));
                results[writer] = (await RunAsync(writer, application, url, steps, directory, $"one-{i}"))[0]!;
            }

            foreach (var writer in order)
            {
                var outcome = results[writer]["outcome"]!.GetValue<string>();
                Assert.True(
                    outcome == (expected ?? (writer == order[0] ? "Recorded" : "AlreadyRecorded")),
                    $"Submission {i}: {writer} answered {results[writer].ToJsonString()}.");

                // What every agent reports about the records is the same, whoever wrote the row.
                var reported = Normalized(results[writer]);
                Assert.True(
                    JsonNode.DeepEquals(Normalized(results[order[0]]), reported),
                    $"Submission {i}: {writer} reported {reported.ToJsonString()}, {order[0]} {Normalized(results[order[0]]).ToJsonString()}.");
            }
        }

        // Every agent reads every lesson's history and confidence, and all three read the same.
        var reads = new JsonArray([.. Lessons.Select(lesson => (JsonNode)new JsonObject
        {
            ["read"] = new JsonObject
            {
                ["authorization"] = LifecycleContract.Auth(Scope),
                ["scope"] = LifecycleContract.ScopeJson(Scope),
                ["experienceId"] = LifecycleContract.D(lesson.ExperienceId),
            },
        })]);
        var read = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var writer in Writers)
        {
            read[writer] = await RunAsync(writer, application, url, reads.DeepClone().AsArray(), directory, "one-read");
        }

        for (var n = 0; n < Lessons.Count; n++)
        {
            var history = read["dotnet"][n]!["history"]!;
            Assert.Equal("Found", history["outcome"]!.GetValue<string>());
            Assert.NotEmpty(history["events"]!.AsArray());
            foreach (var writer in new[] { "python", "go" })
            {
                Assert.True(
                    JsonNode.DeepEquals(read["dotnet"][n], read[writer][n]),
                    $"Lesson {n}: {writer} read {read[writer][n]!.ToJsonString()}, .NET {read["dotnet"][n]!.ToJsonString()}.");
            }
        }

        // The submissions' lifecycle effects: harm contested the two lessons it named; improvement left the first Validated.
        Assert.Equal(["Validated", "Contested", "Contested"], Lessons.Select((_, n) => read["dotnet"][n]!["confidence"]!["report"]!["status"]!.GetValue<string>()));

        // Every case gets a database: idle pooled connections left behind would exhaust the server's slots.
        application.Clear();
    }

    private async Task ThreeDatabasesAsync(string directory)
    {
        var steps = new JsonArray();
        var minute = 0;
        foreach (var (feedback, principal, _) in Submissions())
        {
            steps.Add(Submit(feedback, principal, Clock.AddMinutes(minute++)));
        }

        var results = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var tables = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var writer in Writers)
        {
            var caseName = "xl_reuse_feedback_" + writer;
            var application = await database.CreateAsync(caseName);
            await SeedAsync(application);
            results[writer] = await RunAsync(writer, application, database.ApplicationUrlOf(caseName), steps.DeepClone().AsArray(), directory, "three");
            var owner = database.OwnerOf(caseName);
            tables[writer] = await TablesAsync(owner);
            application.Clear();
            owner.Clear();
        }

        Assert.Equal(["Recorded", "Recorded", "Recorded", "Conflict"], results["dotnet"].Select(result => result!["outcome"]!.GetValue<string>()));
        Assert.NotEmpty(tables["dotnet"]["confidence_evidence"]!.AsArray());
        foreach (var writer in new[] { "python", "go" })
        {
            Assert.True(
                JsonNode.DeepEquals(results["dotnet"], results[writer]),
                $"{writer} answered {results[writer].ToJsonString()}, .NET {results["dotnet"].ToJsonString()}.");
            foreach (var (table, rows) in tables["dotnet"])
            {
                Assert.True(
                    JsonNode.DeepEquals(rows, tables[writer][table]),
                    $"{writer} left {table} as {tables[writer][table]!.ToJsonString()}, .NET as {rows!.ToJsonString()}.");
            }
        }
    }

    /// <summary>
    /// The submissions, in order, each with the principal it is made under and the outcome every agent must answer
    /// (<see langword="null"/>: the first agent Recorded, the others AlreadyRecorded).
    /// </summary>
    private static List<(ExperienceReuseFeedback Feedback, string Principal, string? Expected)> Submissions()
    {
        var (l1, l2, l3) = (Lessons[0].ExperienceId, Lessons[1].ExperienceId, Lessons[2].ExperienceId);
        var human = new ExperienceReuseFeedback(Fixed.Id(F, 0xf001), Run, Scope, [l1, l2], TaskVerificationStatus.Verified, new ReuseMeasure("tool-calls", 7), Observed)
        {
            HumanAssessment = new HumanReuseAssessment(Fixed.Id(F, 0xa001), ExperienceReuseBenefit.Improved, [l1], "The reviewer saw the lesson applied.", Observed.AddMinutes(5), Round, "aexat1.not-checked"),
            TrialLabel = "memory-enabled",
        };
        var comparative = new ExperienceReuseFeedback(Fixed.Id(F, 0xf002), Run, Scope, [l2, l3], TaskVerificationStatus.Failed, new ReuseMeasure("tool-calls", 12.5), Observed)
        {
            ComparativeEvaluation = new ComparativeEvaluationResult(
                "comparative-evaluator/2.0",
                Run,
                Round,
                ExperienceReuseBenefit.Harmed,
                [l3, l2],
                [new Evidence(Fixed.Id(F, 0xe001), Round, "rev-1", "fewer-tool-calls", "comparative-run", CheckResult.Fail, "comparative-evaluator/2.0", null, Observed)],
                "Runs with the lesson needed more tool calls.",
                Observed.AddMinutes(10)),
        };
        var exposureOnly = new ExperienceReuseFeedback(Fixed.Id(F, 0xf003), Fixed.Id(F, 0x5101), Scope, [l1, l2, l3], TaskVerificationStatus.Verified, new ReuseMeasure("wall-clock-ms", -0.125), Observed.AddTicks(1_234_567))
        {
            ClaimedBenefit = ExperienceReuseBenefit.Improved,
        };
        return
        [
            (human, "reviewer-ü", null),
            (comparative, "contract-principal", null),
            (exposureOnly, "contract-principal", null),
            (human with { Measure = new ReuseMeasure("tool-calls", 8) }, "reviewer-ü", "Conflict"),
        ];
    }

    private static JsonObject Submit(ExperienceReuseFeedback feedback, string principal, DateTimeOffset clock) => new()
    {
        ["submit"] = new JsonObject
        {
            ["clock"] = ContractJson.Timestamp(clock),
            ["authorization"] = LifecycleContract.Auth(Scope, principal),
            ["feedback"] = ReuseFeedbackContract.FeedbackJson(feedback),
        },
    };

    /// <summary>A result without its outcome, which is the one thing that depends on who submitted first.</summary>
    private static JsonNode Normalized(JsonNode result)
    {
        var copy = result.DeepClone().AsObject();
        copy.Remove("outcome");
        return copy;
    }

    private static async Task SeedAsync(NpgsqlDataSource application)
    {
        foreach (var lesson in Lessons)
        {
            await SearchContractPostgresTests.InsertAsync(application, ContractJson.ToStored(lesson));
        }
    }

    private static async Task<JsonObject> TablesAsync(NpgsqlDataSource owner) => new()
    {
        ["reuse_feedback"] = await LifecycleSqlContractPostgresTests.TableAsync(owner, "reuse_feedback", ReuseFeedbackSqlContract.SubmissionColumns, "feedback_id"),
        ["reuse_feedback_exposures"] = await LifecycleSqlContractPostgresTests.TableAsync(owner, "reuse_feedback_exposures", ReuseFeedbackSqlContract.ExposureColumns, "feedback_id, ordinal"),
        ["confidence_evidence"] = await LifecycleSqlContractPostgresTests.TableAsync(owner, "confidence_evidence", LifecycleSqlContract.EvidenceColumns, "experience_id, evidence_id"),
        ["lifecycle_events"] = await LifecycleSqlContractPostgresTests.TableAsync(owner, "lifecycle_events", LifecycleSqlContract.EventColumns, "experience_id, applied_revision"),
        ["experience_records"] = await LifecycleSqlContractPostgresTests.TableAsync(owner, "experience_records", LifecycleSqlContract.RecordColumns, "experience_id"),
    };

    /// <summary>Runs the steps through one agent: .NET in process, the ports through their writers.</summary>
    private static async Task<JsonArray> RunAsync(string writer, NpgsqlDataSource application, string url, JsonArray steps, string directory, string label) =>
        writer == "dotnet"
            ? await RunDotnetAsync(application, steps)
            : (await RunWriterAsync(writer, new JsonObject { ["dsn"] = url, ["hostTrustedEvidence"] = "Counted", ["steps"] = steps }, directory, label))["results"]!.AsArray();

    private static async Task<JsonArray> RunDotnetAsync(NpgsqlDataSource application, JsonArray steps)
    {
        var clock = new SettableTimeProvider(Clock);
        var records = new PostgresExperienceRecordStore(application, timeProvider: clock);
        var lifecycle = LifecycleContract.Service(records, new JsonObject { ["engine"] = null, ["hostTrustedEvidence"] = "Counted" });
        var service = new ExperienceReuseFeedbackService(new PostgresExperienceReuseFeedbackStore(application, clock), lifecycle);
        var results = new JsonArray();
        foreach (var step in steps)
        {
            if (step!["submit"] is JsonObject submit)
            {
                clock.Now = LifecycleContract.Instant(submit["clock"])!.Value;
                var result = await service.RecordAsync(
                    LifecycleContract.ReadAuthorization(submit["authorization"]!),
                    ReuseFeedbackContract.ReadFeedback(submit["feedback"]!.AsObject()),
                    CancellationToken.None);
                results.Add(ReuseFeedbackContract.SubmitJson(result));
                continue;
            }

            var read = step["read"]!.AsObject();
            results.Add(new JsonObject
            {
                ["history"] = LifecycleContract.HistoryResult(records, new JsonObject
                {
                    ["authorization"] = read["authorization"]!.DeepClone(),
                    ["query"] = new JsonObject
                    {
                        ["scope"] = read["scope"]!.DeepClone(),
                        ["experienceId"] = read["experienceId"]!.DeepClone(),
                        ["limit"] = ExperienceRecordHistoryQuery.MaxLimit,
                        ["startAfterRevision"] = null,
                    },
                }),
                ["confidence"] = LifecycleContract.ReadResult(lifecycle, new JsonObject
                {
                    ["authorization"] = read["authorization"]!.DeepClone(),
                    ["scope"] = read["scope"]!.DeepClone(),
                    ["experienceId"] = read["experienceId"]!.DeepClone(),
                    ["filter"] = "All",
                }),
            });
        }

        return results;
    }

    private static async Task<JsonObject> RunWriterAsync(string writer, JsonObject input, string directory, string label)
    {
        var path = Path.Combine(directory, $"{writer}-{label}.json");
        await File.WriteAllTextAsync(path, input.ToJsonString());
        var start = writer == "python"
            ? new ProcessStartInfo("uv", ["run", "--no-sync", "python", "tests/cross-language/reuse_feedback.py", path]) { WorkingDirectory = Path.Combine(Repository, "python") }
            : new ProcessStartInfo("go", ["run", "./internal/reusefeedback", path]) { WorkingDirectory = Path.Combine(Repository, "go", "postgres") };
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"The {writer} writer did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The {writer} writer ({label}) exited {process.ExitCode}: {await stderr}");
        }

        return JsonNode.Parse(await stdout)!.AsObject();
    }
}
