using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Storage.Postgres;
using Npgsql;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 27.6b's cross-language proof (contract 1.16): a .NET, a Python and a Go writer race to commit a transition on the
/// same record at the same revision, in one PostgreSQL database, and exactly one commits per revision; the other two are
/// <see cref="LifecycleTransitionOutcome.StaleRevision"/> with the revision the winner produced, and all three then read
/// the same history.
/// </summary>
/// <remarks>
/// <para>
/// It needs all three toolchains (the .NET SDK, uv with the Python package's <c>postgres</c> extra synced, and Go), so it
/// runs only when <c>AGENTEXPERIENCE_CROSS_LANGUAGE_RACE=1</c> (CI's <c>cross-language-lifecycle</c> job sets it) and
/// passes without doing anything otherwise. The Python writer is <c>python/tests/cross-language/lifecycle_race.py</c>
/// and the Go writer <c>go/postgres/internal/lifecyclerace</c>.
/// </para>
/// <para>
/// <b>A real race, asserted only on outcomes.</b> This test holds one session-level advisory lock per round; each writer
/// waits on it (a shared lock on the same key) before it commits, and the test releases it once all three are queued, so
/// all three commits start together. Nothing is asserted about timing or about which writer wins: whichever does, the
/// others must lose, because every commit is a revision compare-and-set in the database.
/// </para>
/// </remarks>
[Collection(ContractPostgresCollection.Name)]
public sealed class CrossLanguageLifecycleRacePostgresTests(ContractPostgresDatabase database)
{
    private const string Variable = "AGENTEXPERIENCE_CROSS_LANGUAGE_RACE";
    private const string CaseName = "xl_lifecycle_race";
    private const int Rounds = 6;
    private const long LockBase = 0x0116_0000_0000;
    private const int F = 0x1163;

    private static readonly string Repository = Path.GetDirectoryName(ContractFiles.Root)!;
    private static readonly Scope Scope = Fixed.Scope;
    private static readonly string[] Writers = ["dotnet", "python", "go"];
    private static readonly string[] Targets = ["Reinforced", "Stale", "Contested"];

    [Fact]
    public async Task Dotnet_python_and_go_writers_racing_on_one_revision_commit_exactly_once_and_read_one_history()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            return;
        }

        var application = await database.CreateAsync(CaseName);
        var owner = database.OwnerOf(CaseName);
        var records = Enumerable.Range(1, Rounds)
            .Select(n => SearchContract.Seed(F, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt."))
            .ToList();
        foreach (var record in records)
        {
            await SearchContractPostgresTests.InsertAsync(application, ContractJson.ToStored(record));
        }

        // Each round, each writer moves the round's record to a different status, all decided against revision 1; the
        // assignment rotates so no language always asks for the same move.
        JsonObject Request(string writer, int round)
        {
            var index = Array.IndexOf(Writers, writer);
            return new JsonObject
            {
                ["eventId"] = LifecycleContract.D(Fixed.Id(F, 0x1000 + (round * 0x10) + index)),
                ["experienceId"] = LifecycleContract.D(records[round].ExperienceId),
                ["scope"] = LifecycleContract.ScopeJson(Scope),
                ["priorStatus"] = "Validated",
                ["currentStatus"] = Targets[(index + round) % Targets.Length],
                ["reason"] = $"the {writer} writer's decision in round {round}",
                ["producer"] = $"lifecycle-race/{writer}",
                ["occurredAt"] = ContractJson.Timestamp(Fixed.T0.AddMinutes(round)),
                ["expectedRevision"] = 1L,
                ["replacementExperienceId"] = null,
            };
        }

        var authorization = LifecycleContract.Auth(Scope);
        var url = database.ApplicationUrlOf(CaseName);
        var directory = Directory.CreateTempSubdirectory("lifecycle-race-");
        try
        {
            // The barrier: every round's key held exclusively until all three writers queue behind it.
            await using var barrier = await owner.OpenConnectionAsync();
            for (var round = 0; round < Rounds; round++)
            {
                await ExecuteAsync(barrier, $"SELECT pg_catalog.pg_advisory_lock({LockBase + round})");
            }

            var ports = new Dictionary<string, Task<JsonObject>>(StringComparer.Ordinal);
            foreach (var writer in new[] { "python", "go" })
            {
                var input = new JsonObject
                {
                    ["dsn"] = url,
                    ["mode"] = "commit",
                    ["rounds"] = new JsonArray([.. Enumerable.Range(0, Rounds).Select(round => (JsonNode)new JsonObject
                    {
                        ["lockKey"] = LockBase + round,
                        ["authorization"] = authorization.DeepClone(),
                        ["request"] = Request(writer, round),
                    })]),
                    ["histories"] = new JsonArray(),
                };
                ports[writer] = RunWriterAsync(writer, input, directory.FullName, "commit");
            }

            var service = LifecycleContract.Service(new PostgresExperienceRecordStore(application), new JsonObject { ["engine"] = null, ["hostTrustedEvidence"] = "Counted" });
            var dotnet = Task.Run(async () =>
            {
                var results = new JsonArray();
                for (var round = 0; round < Rounds; round++)
                {
                    await using (var wait = await application.OpenConnectionAsync())
                    {
                        await ExecuteAsync(wait, $"SELECT pg_catalog.pg_advisory_lock_shared({LockBase + round})");
                        await ExecuteAsync(wait, $"SELECT pg_catalog.pg_advisory_unlock_shared({LockBase + round})");
                    }

                    var request = Request("dotnet", round);
                    var result = await service.CommitAsync(LifecycleContract.ReadAuthorization(authorization), ReadRequest(request), CancellationToken.None);
                    results.Add(LifecycleContract.CommitResult(result, request));
                }

                return new JsonObject { ["results"] = results };
            });

            for (var round = 0; round < Rounds; round++)
            {
                await WaitForQueuedAsync(barrier, LockBase + round, [dotnet, .. ports.Values]);
                await ExecuteAsync(barrier, $"SELECT pg_catalog.pg_advisory_unlock({LockBase + round})");
            }

            var outcomes = new Dictionary<string, JsonArray>(StringComparer.Ordinal)
            {
                ["dotnet"] = (await dotnet)["results"]!.AsArray(),
                ["python"] = (await ports["python"])["results"]!.AsArray(),
                ["go"] = (await ports["go"])["results"]!.AsArray(),
            };

            var winners = new string[Rounds];
            for (var round = 0; round < Rounds; round++)
            {
                var committed = Writers.Where(writer => Outcome(outcomes[writer][round]) == "Committed").ToList();
                Assert.True(committed.Count == 1, $"Round {round}: {string.Join(", ", Writers.Select(writer => $"{writer} {outcomes[writer][round]!.ToJsonString()}"))}.");
                winners[round] = committed[0];
                foreach (var writer in Writers)
                {
                    var result = outcomes[writer][round]!;
                    Assert.Equal(2, result["revision"]!.GetValue<long>());
                    if (writer != winners[round])
                    {
                        // .NET's conflict outcome: the revision moved, and the result says to what.
                        Assert.Equal("StaleRevision", Outcome(result));
                        Assert.Null(result["currentStatus"]?.GetValue<string>());
                    }
                }
            }

            // Every writer reads every record's history, and all three read the same one: the winner's event, alone.
            var queries = new JsonArray([.. records.Select(record => (JsonNode)new JsonObject
            {
                ["authorization"] = authorization.DeepClone(),
                ["query"] = new JsonObject
                {
                    ["scope"] = LifecycleContract.ScopeJson(Scope),
                    ["experienceId"] = LifecycleContract.D(record.ExperienceId),
                    ["limit"] = 100,
                    ["startAfterRevision"] = null,
                },
            })]);
            var store = new PostgresExperienceRecordStore(application);
            var histories = new Dictionary<string, JsonArray>(StringComparer.Ordinal)
            {
                ["dotnet"] = new JsonArray([.. await Task.WhenAll(records.Select(async record => (JsonNode)HistoryJson(
                    await store.GetHistoryAsync(LifecycleContract.ReadAuthorization(authorization), new ExperienceRecordHistoryQuery(Scope, record.ExperienceId), CancellationToken.None))))]),
            };
            foreach (var writer in new[] { "python", "go" })
            {
                var input = new JsonObject { ["dsn"] = url, ["mode"] = "history", ["rounds"] = new JsonArray(), ["histories"] = queries.DeepClone() };
                histories[writer] = (await RunWriterAsync(writer, input, directory.FullName, "history"))["histories"]!.AsArray();
            }

            for (var round = 0; round < Rounds; round++)
            {
                var history = histories["dotnet"][round]!;
                Assert.Equal("Found", history["outcome"]!.GetValue<string>());
                Assert.Equal(2, history["revision"]!.GetValue<long>());
                var events = history["events"]!.AsArray();
                var winning = events.Single()!;
                Assert.Equal(Request(winners[round], round)["eventId"]!.GetValue<string>(), winning["event_id"]!.GetValue<string>());
                Assert.Equal(Request(winners[round], round)["currentStatus"]!.GetValue<string>(), winning["current_status"]!.GetValue<string>());
                foreach (var writer in new[] { "python", "go" })
                {
                    Assert.True(
                        JsonNode.DeepEquals(history, histories[writer][round]),
                        $"Round {round}: {writer} read {histories[writer][round]!.ToJsonString()}, .NET {history.ToJsonString()}.");
                }

                var read = await store.GetAsync(LifecycleContract.ReadAuthorization(authorization), Scope, records[round].ExperienceId, CancellationToken.None);
                Assert.Equal(Request(winners[round], round)["currentStatus"]!.GetValue<string>(), read.Record!.Status.ToString());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string Outcome(JsonNode? result) => result!["outcome"]!.GetValue<string>();

    private static JsonObject HistoryJson(ExperienceRecordHistoryResult result) => new()
    {
        ["outcome"] = result.Outcome.ToString(),
        ["revision"] = result.Revision,
        ["events"] = new JsonArray([.. result.Events.Select(stored => (JsonNode)LifecycleContract.EventRow(stored, Scope))]),
        ["errors"] = LifecycleContract.Errors(result.Errors),
        ["nextStartAfterRevision"] = result.NextStartAfterRevision,
    };

    private static CommitLifecycleTransitionRequest ReadRequest(JsonObject request) => new(
        Guid.Parse(request["eventId"]!.GetValue<string>()),
        Guid.Parse(request["experienceId"]!.GetValue<string>()),
        LifecycleContract.ReadScope(request["scope"]!),
        LifecycleContract.ReadStatus(request["priorStatus"]!.GetValue<string>()),
        LifecycleContract.ReadStatus(request["currentStatus"]!.GetValue<string>()),
        request["reason"]!.GetValue<string>(),
        request["producer"]!.GetValue<string>(),
        LifecycleContract.Instant(request["occurredAt"])!.Value,
        request["expectedRevision"]!.GetValue<long>());

    /// <summary>
    /// Waits until three sessions queue behind the round's lock, which is how the test knows all three writers are ready
    /// to commit. Bounded, and it fails fast when a writer has already ended (a crash, or a refusal to start).
    /// </summary>
    private static async Task WaitForQueuedAsync(NpgsqlConnection connection, long key, IReadOnlyList<Task> writers)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid::bigint = @high AND objid::bigint = @low AND objsubid = 1",
                connection);
            command.Parameters.AddWithValue("high", (long)(uint)(key >> 32));
            command.Parameters.AddWithValue("low", (long)(uint)(key & 0xffff_ffff));
            if ((long)(await command.ExecuteScalarAsync())! >= Writers.Length)
            {
                return;
            }

            if (writers.FirstOrDefault(writer => writer.IsCompleted) is { } ended)
            {
                await ended;
                throw new InvalidOperationException($"A writer ended before queueing for lock {key}.");
            }

            Assert.True(deadline.Elapsed < TimeSpan.FromMinutes(10), $"The writers did not all queue for lock {key} within ten minutes.");
            await Task.Delay(50);
        }
    }

    private static async Task<JsonObject> RunWriterAsync(string writer, JsonObject input, string directory, string mode)
    {
        var path = Path.Combine(directory, $"{writer}-{mode}.json");
        await File.WriteAllTextAsync(path, input.ToJsonString());
        var start = writer == "python"
            ? new ProcessStartInfo("uv", ["run", "--no-sync", "python", "tests/cross-language/lifecycle_race.py", path]) { WorkingDirectory = Path.Combine(Repository, "python") }
            : new ProcessStartInfo("go", ["run", "./internal/lifecyclerace", path]) { WorkingDirectory = Path.Combine(Repository, "go", "postgres") };
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"The {writer} writer did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The {writer} writer ({mode}) exited {process.ExitCode}: {await stderr}");
        }

        return JsonNode.Parse(await stdout)!.AsObject();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
