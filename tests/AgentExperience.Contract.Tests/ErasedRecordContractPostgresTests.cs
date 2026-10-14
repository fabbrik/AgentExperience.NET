using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.5's erased family, against a schema the .NET migrator applied with row-level
/// security on. Each case runs twice, each time in a database of its own: once through the .NET store (its record call,
/// its read by ID, and its candidate source, which generates <c>expected.json</c>), and once with the contract's
/// statements run exactly as written, as a port runs them. Both must give the checked-in results.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class ErasedRecordContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_erased_fixture_is_what_the_dotnet_store_and_the_contract_statements_give()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in ErasedRecordContract.Cases)
        {
            var directory = $"fixtures/{ErasedRecordContract.Family}/{name}";
            var seedJson = ContractJson.Write(ErasedRecordContract.ToSeedJson(definition));
            var stepsJson = ContractJson.Write(ErasedRecordContract.ToStepsJson(definition));
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/steps.json", stepsJson, drift);

            var seed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsArray();
            var steps = ErasedRecordContract.ReadSteps(JsonNode.Parse(ContractFiles.Read($"{directory}/steps.json") ?? stepsJson)!);

            var byStore = await RunAsync($"era_{name}_store", seed, steps, throughStore: true);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(byStore), drift);

            var byStatement = await RunAsync($"era_{name}_sql", seed, steps, throughStore: false);
            if (!JsonNode.DeepEquals(byStore, byStatement))
            {
                drift.Add($"{directory}: the contract statements give {byStatement.ToJsonString()}, the .NET store {byStore.ToJsonString()}.");
            }
        }

        ContractFiles.VerifyNoStray(
            ErasedRecordContract.Family,
            ErasedRecordContract.Cases.Select(entry => entry.Name),
            ["seed.json", "steps.json", "expected.json"],
            drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    /// <summary>Seeds a fresh database, runs the steps, and returns what <c>expected.json</c> holds.</summary>
    private async Task<JsonObject> RunAsync(string caseName, JsonArray seed, IReadOnlyList<ErasedStep> steps, bool throughStore)
    {
        var name = caseName.Replace('-', '_');
        var application = await database.CreateAsync(name);
        var owner = database.OwnerOf(name);
        foreach (var record in seed)
        {
            await SearchContractPostgresTests.InsertAsync(application, record!);
        }

        var store = new PostgresExperienceRecordStore(application);
        var results = new JsonArray();
        foreach (var step in steps)
        {
            switch (step)
            {
                case SearchStep search:
                    results.Add(await SearchAsync(application, search.Query));
                    break;
                case GetStep get:
                    results.Add(new JsonObject { ["found"] = await GetAsync(store, application, get, throughStore) });
                    break;
                case RecordStep record when throughStore:
                    var recorded = await store.RecordInjectionAsync(
                        SearchContractPostgresTests.Authorization(record.Record.Scope),
                        record.Record.Scope,
                        record.Record.ExperienceIds,
                        record.Record.InjectedAt,
                        CancellationToken.None);
                    Assert.Equal(ExperienceStoreOutcome.Committed, recorded.Outcome);
                    results.Add(null);
                    break;
                case RecordStep record:
                    await InjectionRecordContractPostgresTests.RecordAsWrittenAsync(application, record.Record);
                    results.Add(null);
                    break;
                case EraseRecordStep erase:
                    await InjectionRecordContractPostgresTests.EraseAsync(owner, erase.Erase);
                    results.Add(null);
                    break;
            }
        }

        var now = await InjectionRecordContractPostgresTests.ClockAsync(owner);
        return new JsonObject
        {
            ["steps"] = results,
            ["experience_injections"] = await InjectionRecordContractPostgresTests.RowsAsync(owner, [], now, now),
        };
    }

    /// <summary>
    /// The contract's search statement as written, which the .NET candidate source must agree with, then Core's ranking:
    /// the search family's <c>expected.json</c> shape.
    /// </summary>
    private static async Task<JsonNode> SearchAsync(Npgsql.NpgsqlDataSource application, SearchQuery query)
    {
        var rows = await SearchContractPostgresTests.SearchAsync(application, query);
        var candidates = await new PostgresExperienceCandidateSource(application).SearchAsync(
            SearchContractPostgresTests.Authorization(query.Scope),
            new ExperienceCandidateQuery(query.Scope, query.TaskText, query.Statuses, query.MinConfidence, query.Limit)
            {
                ExcludeModelAuthored = query.ExcludeModelAuthored,
                MinimumMatchedTerms = query.MinimumMatchedTerms,
            },
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, candidates.Outcome);
        Assert.Equal(rows.Select(row => row.ExperienceId), candidates.Candidates.Select(candidate => candidate.Record.ExperienceId));

        var (ranked, truncated) = await SearchContractPostgresTests.RankAsync(application, query);
        return JsonSerializer.SerializeToNode(new SearchExpected(rows, truncated, ranked), ContractJson.Options)!;
    }

    /// <summary>
    /// A read by ID in exactly the step's scope: through the .NET store, or as a port reads it (the record's columns in
    /// exactly that scope, live rows only, under bounds declared for it).
    /// </summary>
    private static async Task<bool> GetAsync(PostgresExperienceRecordStore store, Npgsql.NpgsqlDataSource application, GetStep get, bool throughStore)
    {
        if (throughStore)
        {
            var result = await store.GetAsync(SearchContractPostgresTests.Authorization(get.Scope), get.Scope, get.ExperienceId, CancellationToken.None);
            // .NET tells an erased record (Deleted) from an unknown ID (NotFound); either way no record is read.
            Assert.Contains(result.Outcome, new[] { ExperienceStoreOutcome.Found, ExperienceStoreOutcome.NotFound, ExperienceStoreOutcome.Deleted });
            Assert.Equal(result.Outcome == ExperienceStoreOutcome.Found, result.Record is not null);
            return result.Outcome == ExperienceStoreOutcome.Found;
        }

        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var declare = new Npgsql.NpgsqlCommand(ContractFiles.Read("sql/declare-bounds.sql"), connection, transaction))
        {
            var bounds = get.Scope;
            declare.Parameters.AddWithValue("auth_tenant", "=" + bounds.TenantId);
            declare.Parameters.AddWithValue("auth_application", Bound(bounds.ApplicationId));
            declare.Parameters.AddWithValue("auth_project", Bound(bounds.ProjectId));
            declare.Parameters.AddWithValue("auth_team", Bound(bounds.TeamId));
            declare.Parameters.AddWithValue("auth_agent", Bound(bounds.AgentId));
            declare.Parameters.AddWithValue("auth_user", Bound(bounds.UserId));
            await declare.ExecuteNonQueryAsync();
        }

        await using var select = new Npgsql.NpgsqlCommand(
            "SELECT payload IS NOT NULL FROM agent_experience.experience_records WHERE experience_id = @id AND tenant_id = @tenant_id " +
            "AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id " +
            "AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL",
            connection,
            transaction);
        var scope = get.Scope;
        select.Parameters.Add(new("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = get.ExperienceId });
        select.Parameters.Add(new("tenant_id", NpgsqlTypes.NpgsqlDbType.Text) { Value = scope.TenantId });
        select.Parameters.Add(new("application_id", NpgsqlTypes.NpgsqlDbType.Text) { Value = scope.ApplicationId });
        select.Parameters.Add(new("project_id", NpgsqlTypes.NpgsqlDbType.Text) { Value = scope.ProjectId });
        select.Parameters.Add(new("team_id", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
        select.Parameters.Add(new("agent_id", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
        select.Parameters.Add(new("user_id", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
        var found = await select.ExecuteScalarAsync() is true;
        await transaction.CommitAsync();
        return found;
    }

    private static string Bound(string? value) => value is null ? string.Empty : "=" + value;
}
