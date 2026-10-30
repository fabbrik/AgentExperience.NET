using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.16's <c>lifecycle-sql</c> family, against a schema the .NET migrator applied with
/// row-level security on: each case's steps run through the .NET <see cref="PostgresExperienceRecordStore"/> (whose
/// statements <c>sql/lifecycle.sql</c> is generated from), and <c>expected.json</c> is each step's result and the three
/// tables afterwards, every column in PostgreSQL's text form.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class LifecycleSqlContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_lifecycle_sql_fixture_is_what_the_dotnet_postgres_store_leaves()
    {
        var drift = new List<string>();
        foreach (var (name, seed, steps) in LifecycleSqlContract.Cases)
        {
            var directory = $"fixtures/{LifecycleSqlContract.Family}/{name}";
            var seedJson = ContractJson.Write(seed);
            var stepsJson = ContractJson.Write(steps);
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/steps.json", stepsJson, drift);

            // As checked in, read as a port reads them.
            var readSeed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsObject();
            var readSteps = JsonNode.Parse(ContractFiles.Read($"{directory}/steps.json") ?? stepsJson)!.AsObject();
            var expected = await RunAsync("lc_" + name.Replace('-', '_'), readSeed, readSteps);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(expected), drift);
        }

        ContractFiles.VerifyNoStray(
            LifecycleSqlContract.Family,
            LifecycleSqlContract.Cases.Select(entry => entry.Name),
            ["seed.json", "steps.json", "expected.json"],
            drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    private async Task<JsonObject> RunAsync(string caseName, JsonObject seed, JsonObject steps)
    {
        var application = await database.CreateAsync(caseName);
        var owner = database.OwnerOf(caseName);
        foreach (var record in seed["records"]!.AsArray())
        {
            await SearchContractPostgresTests.InsertAsync(application, record!);
        }

        var clock = new SettableTimeProvider(Fixed.T0);
        var store = new PostgresExperienceRecordStore(application, timeProvider: clock);
        var results = new JsonArray();
        foreach (var step in steps["steps"]!.AsArray())
        {
            clock.Now = LifecycleContract.Instant(step!["clock"])!.Value;
            results.Add(await RunStepAsync(store, owner, step.AsObject()));
        }

        return new JsonObject
        {
            ["results"] = results,
            ["experience_records"] = await TableAsync(owner, "experience_records", LifecycleSqlContract.RecordColumns, "experience_id"),
            ["lifecycle_events"] = await TableAsync(owner, "lifecycle_events", LifecycleSqlContract.EventColumns, "experience_id, applied_revision"),
            ["confidence_evidence"] = await TableAsync(owner, "confidence_evidence", LifecycleSqlContract.EvidenceColumns, "experience_id, evidence_id"),
        };
    }

    private static async Task<JsonNode?> RunStepAsync(PostgresExperienceRecordStore store, NpgsqlDataSource owner, JsonObject step)
    {
        var (operation, body) = step.Where(member => member.Key != "clock").Select(member => (member.Key, member.Value!.AsObject())).Single();
        switch (operation)
        {
            case "commit":
                var committed = await store.CommitLifecycleEventAsync(
                    LifecycleContract.ReadAuthorization(body["authorization"]!),
                    LifecycleContract.ReadScope(body["scope"]!),
                    LifecycleSqlContract.ReadEvent(body["event"]!.AsObject()),
                    CancellationToken.None);
                return new JsonObject
                {
                    ["outcome"] = committed.Outcome.ToString(),
                    ["revision"] = committed.Revision,
                    ["currentStatus"] = committed.CurrentStatus?.ToString(),
                    ["errors"] = LifecycleContract.Errors(committed.Errors),
                    ["appliedConfidence"] = LifecycleContract.UpdateJson(committed.AppliedConfidence),
                };

            case "history":
                var query = body["query"]!.AsObject();
                var scope = LifecycleContract.ReadScope(query["scope"]!);
                var history = await store.GetHistoryAsync(
                    LifecycleContract.ReadAuthorization(body["authorization"]!),
                    new ExperienceRecordHistoryQuery(
                        scope,
                        Guid.Parse(query["experienceId"]!.GetValue<string>()),
                        query["limit"]!.GetValue<int>(),
                        query["startAfterRevision"]?.GetValue<long>()),
                    CancellationToken.None);
                return new JsonObject
                {
                    ["outcome"] = history.Outcome.ToString(),
                    ["revision"] = history.Revision,
                    ["events"] = new JsonArray([.. history.Events.Select(stored => (JsonNode)LifecycleContract.EventRow(stored, scope))]),
                    ["errors"] = LifecycleContract.Errors(history.Errors),
                    ["nextStartAfterRevision"] = history.NextStartAfterRevision,
                };

            case "checkSupersession":
                var check = await store.CheckSupersessionAsync(
                    LifecycleContract.ReadAuthorization(body["authorization"]!),
                    LifecycleContract.ReadScope(body["scope"]!),
                    Guid.Parse(body["experienceId"]!.GetValue<string>()),
                    Guid.Parse(body["replacementExperienceId"]!.GetValue<string>()),
                    CancellationToken.None);
                return new JsonObject
                {
                    ["outcome"] = check.Outcome.ToString(),
                    ["replacementStatus"] = check.ReplacementStatus?.ToString(),
                    ["errors"] = LifecycleContract.Errors(check.Errors),
                };

            case "read":
                var read = await store.GetAsync(
                    LifecycleContract.ReadAuthorization(body["authorization"]!),
                    LifecycleContract.ReadScope(body["scope"]!),
                    Guid.Parse(body["experienceId"]!.GetValue<string>()),
                    new ExperienceReadOptions(ExperienceReadPurpose.ScopeCheck),
                    CancellationToken.None);
                return new JsonObject
                {
                    ["outcome"] = read.Outcome.ToString(),
                    ["record"] = read.Record is { } record ? ContractJson.ToStored(record) : null,
                    ["errors"] = LifecycleContract.Errors(read.Errors),
                };

            case "erase":
                await InjectionRecordContractPostgresTests.EraseAsync(owner, new EraseStep(
                    Guid.Parse(body["experienceId"]!.GetValue<string>()),
                    LifecycleContract.ReadScope(body["scope"]!),
                    LifecycleContract.Instant(body["deletedAt"])!.Value));
                return null;

            default:
                throw new InvalidOperationException($"Unknown lifecycle-sql step '{operation}'.");
        }
    }

    /// <summary>Every row of one table, as the schema's owner reads it, each column as <c>::text</c> in the time zone UTC.</summary>
    internal static async Task<JsonArray> TableAsync(NpgsqlDataSource owner, string table, IReadOnlyList<string> columns, string orderBy)
    {
        await using var connection = await owner.OpenConnectionAsync();
        await using (var zone = new NpgsqlCommand("SET TIME ZONE 'UTC'", connection))
        {
            await zone.ExecuteNonQueryAsync();
        }

        await using var select = new NpgsqlCommand(
            $"SELECT {string.Join(", ", columns.Select(column => column + "::text"))} FROM agent_experience.{table} ORDER BY {orderBy}",
            connection);
        var rows = new JsonArray();
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new JsonObject();
            for (var i = 0; i < columns.Count; i++)
            {
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
            }

            rows.Add(row);
        }

        return rows;
    }
}
