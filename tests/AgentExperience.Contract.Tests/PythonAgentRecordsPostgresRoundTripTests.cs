using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 22.3's cross-language proof, the .NET half: the records a Python MAF agent wrote
/// (<c>python/tests/cross-language/python-agent-records.json</c>, which the Python tests check its agent writes exactly,
/// on a fixed clock and fixed identifiers) are inserted the way a port inserts them, then read back through the
/// PostgreSQL record store's own <c>GetAsync</c> as the application role: each must come back unchanged. Test-only:
/// the file is not part of the contract, so it is outside <c>contract/</c> and its lock.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class PythonAgentRecordsPostgresRoundTripTests(ContractPostgresDatabase database)
{
    private static readonly string RecordsPath = Path.Combine(
        Path.GetDirectoryName(ContractFiles.Root)!, "python", "tests", "cross-language", "python-agent-records.json");

    [Fact]
    public async Task Every_record_the_python_agent_wrote_reads_back_through_the_store_unchanged()
    {
        Assert.True(File.Exists(RecordsPath), $"{RecordsPath} is missing.");
        var records = JsonNode.Parse(await File.ReadAllTextAsync(RecordsPath))!.AsArray();
        Assert.NotEmpty(records);

        var application = await database.CreateAsync("python_agent_records");
        var store = new PostgresExperienceRecordStore(application);
        var problems = new List<string>();
        foreach (var stored in records)
        {
            RecordSchema.Check(RecordsPath, stored!, problems);
            await SearchContractPostgresTests.InsertAsync(application, stored!);

            var columns = stored!["columns"]!;
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
                problems.Add($"{id}: the store answered {read.Outcome}.");
                continue;
            }

            var again = ContractJson.ToStored(read.Record!);
            if (!JsonNode.DeepEquals(again, stored))
            {
                problems.Add($"{id}: read back differently.\n    python: {stored.ToJsonString()}\n    read:   {again.ToJsonString()}");
            }
        }

        await application.DisposeAsync();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
