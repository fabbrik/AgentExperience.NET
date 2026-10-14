using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Every stored record in the fixtures -- each finalize <c>expected-record.json</c>, each record in an inject
/// <c>records.json</c> and each search <c>seed.json</c> -- inserted the way a port inserts it (the eighteen columns, as the
/// application role, under declared bounds, row-level security on), then read back through the PostgreSQL record store's
/// own <c>GetAsync</c> as the application role: the record it returns, written in the stored form again, must be the
/// fixture exactly. Each fixture file gets its own database, because two files may hold different records under one ID.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class StoredRecordPostgresRoundTripTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_fixture_record_reads_back_through_the_store_unchanged()
    {
        var files = new List<(string Path, List<JsonNode> Records)>();
        foreach (var (name, _) in FinalizeContract.Cases)
        {
            var path = $"fixtures/{FinalizeContract.Family}/{name}/expected-record.json";
            files.Add((path, [Parse(path)]));
        }

        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(ContractFiles.Fixtures, InjectContract.Family)).Order(StringComparer.Ordinal))
        {
            var path = $"fixtures/{InjectContract.Family}/{Path.GetFileName(directory)}/records.json";
            files.Add((path, [.. Parse(path).AsArray().Select(entry => entry!["record"]!)]));
        }

        foreach (var (name, _) in SearchContract.Cases)
        {
            var path = $"fixtures/{SearchContract.Family}/{name}/seed.json";
            files.Add((path, [.. Parse(path).AsArray().Select(entry => entry!)]));
        }

        var problems = new List<string>();
        for (var index = 0; index < files.Count; index++)
        {
            var (path, records) = files[index];
            var application = await database.CreateAsync($"round_trip_{index}");
            var store = new PostgresExperienceRecordStore(application);
            foreach (var stored in records)
            {
                await SearchContractPostgresTests.InsertAsync(application, stored);

                var columns = stored["columns"]!;
                var scope = new Scope(
                    columns["tenant_id"]!.GetValue<string>(),
                    columns["application_id"]!.GetValue<string>(),
                    columns["project_id"]!.GetValue<string>(),
                    columns["team_id"]?.GetValue<string>(),
                    columns["agent_id"]?.GetValue<string>(),
                    columns["user_id"]?.GetValue<string>());
                var id = Guid.Parse(columns["experience_id"]!.GetValue<string>());
                var read = await store.GetAsync(SearchContractPostgresTests.Authorization(scope), scope, id, CancellationToken.None);
                if (read.Outcome != ExperienceStoreOutcome.Found)
                {
                    problems.Add($"{path} {id}: the store answered {read.Outcome}.");
                    continue;
                }

                var again = ContractJson.ToStored(read.Record!);
                if (!JsonNode.DeepEquals(again, stored))
                {
                    problems.Add($"{path} {id}: read back differently.\n    fixture: {stored.ToJsonString()}\n    read:    {again.ToJsonString()}");
                }
            }

            // Released per file: one database per fixture file, and pooled connections would exhaust the server's slots.
            await application.DisposeAsync();
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static JsonNode Parse(string path) =>
        JsonNode.Parse(ContractFiles.Read(path) ?? throw new InvalidOperationException($"{path} is missing."))!;
}
