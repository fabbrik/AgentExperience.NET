using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.4's record-injection family, against a schema the .NET migrator applied with
/// row-level security on. Each case runs twice, each time in a database of its own: once through the .NET store's
/// <see cref="PostgresExperienceRecordStore.RecordInjectionAsync"/> (which generates <c>expected.json</c>), and once with
/// <c>contract/sql/record-injection.sql</c> run exactly as written, as a port runs it. Both must leave the checked-in rows.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class InjectionRecordContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_record_injection_fixture_is_what_the_dotnet_store_and_the_contract_statement_leave()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in InjectionRecordContract.Cases)
        {
            var directory = $"fixtures/{InjectionRecordContract.Family}/{name}";
            var seedJson = ContractJson.Write(InjectionRecordContract.ToSeedJson(definition));
            var stepsJson = ContractJson.Write(InjectionRecordContract.ToStepsJson(definition));
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/steps.json", stepsJson, drift);

            var seed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsObject();
            var steps = InjectionRecordContract.ReadSteps(JsonNode.Parse(ContractFiles.Read($"{directory}/steps.json") ?? stepsJson)!);

            var byStore = await RunAsync($"inj_{name}_store", seed, steps, definition.Clamped, throughStore: true);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(byStore), drift);

            var byStatement = await RunAsync($"inj_{name}_sql", seed, steps, definition.Clamped, throughStore: false);
            if (!JsonNode.DeepEquals(byStore, byStatement))
            {
                drift.Add($"{directory}: the contract statement leaves {byStatement.ToJsonString()}, the .NET store {byStore.ToJsonString()}.");
            }
        }

        ContractFiles.VerifyNoStray(
            InjectionRecordContract.Family,
            InjectionRecordContract.Cases.Select(entry => entry.Name),
            ["seed.json", "steps.json", "expected.json"],
            drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    /// <summary>Seeds a fresh database, runs the steps, and reads the table back as <c>expected.json</c> holds it.</summary>
    private async Task<JsonObject> RunAsync(string caseName, JsonObject seed, IReadOnlyList<InjectionStep> steps, IReadOnlyList<Guid> clamped, bool throughStore)
    {
        var application = await database.CreateAsync(caseName.Replace('-', '_'));
        var owner = database.OwnerOf(caseName.Replace('-', '_'));
        foreach (var record in seed["records"]!.AsArray())
        {
            await SearchContractPostgresTests.InsertAsync(application, record!);
        }

        foreach (var row in seed["injections"]!.AsArray())
        {
            await InsertInjectionAsync(owner, row!.AsObject());
        }

        var before = await ClockAsync(owner);
        var store = new PostgresExperienceRecordStore(application);
        foreach (var step in steps)
        {
            switch (step)
            {
                case RecordInjectionStep record when throughStore:
                    var result = await store.RecordInjectionAsync(
                        SearchContractPostgresTests.Authorization(record.Scope), record.Scope, record.ExperienceIds, record.InjectedAt, CancellationToken.None);
                    Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
                    break;
                case RecordInjectionStep record:
                    await RecordAsWrittenAsync(application, record);
                    break;
                case EraseStep erase:
                    await EraseAsync(owner, erase);
                    break;
            }
        }

        var after = await ClockAsync(owner);
        return new JsonObject { ["experience_injections"] = await RowsAsync(owner, clamped, before, after) };
    }

    /// <summary>
    /// <c>contract/sql/declare-bounds.sql</c> for the step's scope, then <c>contract/sql/record-injection.sql</c> as
    /// written, the instant truncated to whole microseconds.
    /// </summary>
    internal static async Task RecordAsWrittenAsync(NpgsqlDataSource application, RecordInjectionStep step)
    {
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var declare = new NpgsqlCommand(ContractFiles.Read("sql/declare-bounds.sql"), connection, transaction))
        {
            var scope = step.Scope;
            declare.Parameters.Add(new("auth_tenant", NpgsqlDbType.Text) { Value = "=" + scope.TenantId });
            declare.Parameters.Add(new("auth_application", NpgsqlDbType.Text) { Value = Bound(scope.ApplicationId) });
            declare.Parameters.Add(new("auth_project", NpgsqlDbType.Text) { Value = Bound(scope.ProjectId) });
            declare.Parameters.Add(new("auth_team", NpgsqlDbType.Text) { Value = Bound(scope.TeamId) });
            declare.Parameters.Add(new("auth_agent", NpgsqlDbType.Text) { Value = Bound(scope.AgentId) });
            declare.Parameters.Add(new("auth_user", NpgsqlDbType.Text) { Value = Bound(scope.UserId) });
            await declare.ExecuteNonQueryAsync();
        }

        await using (var record = new NpgsqlCommand(ContractFiles.Read("sql/record-injection.sql"), connection, transaction))
        {
            var scope = step.Scope;
            var ticks = step.InjectedAt.UtcTicks;
            record.Parameters.Add(new("experience_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = step.ExperienceIds.ToArray() });
            record.Parameters.Add(new("injected_at", NpgsqlDbType.TimestampTz) { Value = new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero) });
            record.Parameters.Add(new("tenant_id", NpgsqlDbType.Text) { Value = scope.TenantId });
            record.Parameters.Add(new("application_id", NpgsqlDbType.Text) { Value = scope.ApplicationId });
            record.Parameters.Add(new("project_id", NpgsqlDbType.Text) { Value = scope.ProjectId });
            record.Parameters.Add(new("team_id", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
            record.Parameters.Add(new("agent_id", NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
            record.Parameters.Add(new("user_id", NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
            await record.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>A seeded row, inserted as the owner, each column cast from its text by PostgreSQL.</summary>
    private static async Task InsertInjectionAsync(NpgsqlDataSource owner, JsonObject row)
    {
        var columns = InjectionRecordContract.Columns;
        await using var command = owner.CreateCommand(
            "INSERT INTO agent_experience.experience_injections (" + string.Join(", ", columns) + ") VALUES (" +
            "CAST(@p0 AS uuid), @p1, @p2, @p3, @p4, @p5, @p6, CAST(@p7 AS timestamptz), CAST(@p8 AS bigint))");
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add(new($"p{i}", NpgsqlDbType.Text) { Value = (object?)row[columns[i]]?.GetValue<string>() ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The erasure, as the owner, through the one function every erasure path runs.</summary>
    private static async Task EraseAsync(NpgsqlDataSource owner, EraseStep step)
    {
        await using var command = owner.CreateCommand(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(@id, @tenant_id, @application_id, @project_id, " +
            "@team_id, @agent_id, @user_id, NULL, @deleted_at)");
        var scope = step.Scope;
        command.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = step.ExperienceId });
        command.Parameters.Add(new("tenant_id", NpgsqlDbType.Text) { Value = scope.TenantId });
        command.Parameters.Add(new("application_id", NpgsqlDbType.Text) { Value = scope.ApplicationId });
        command.Parameters.Add(new("project_id", NpgsqlDbType.Text) { Value = scope.ProjectId });
        command.Parameters.Add(new("team_id", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
        command.Parameters.Add(new("agent_id", NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
        command.Parameters.Add(new("user_id", NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
        command.Parameters.Add(new("deleted_at", NpgsqlDbType.TimestampTz) { Value = step.DeletedAt });
        Assert.Equal("Deleted", (string?)await command.ExecuteScalarAsync());
    }

    private static async Task<DateTimeOffset> ClockAsync(NpgsqlDataSource owner)
    {
        await using var command = owner.CreateCommand("SELECT pg_catalog.clock_timestamp()");
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// Every row of the table, ordered by <c>experience_id</c>, each column in PostgreSQL's text form in UTC; a clamped
    /// row's instant must lie between the database clock before and after the steps, and is written as
    /// <see cref="InjectionRecordContract.DatabaseClock"/>.
    /// </summary>
    private static async Task<JsonArray> RowsAsync(NpgsqlDataSource owner, IReadOnlyList<Guid> clamped, DateTimeOffset before, DateTimeOffset after)
    {
        await using var connection = await owner.OpenConnectionAsync();
        await using (var zone = new NpgsqlCommand("SET TIME ZONE 'UTC'", connection))
        {
            await zone.ExecuteNonQueryAsync();
        }

        await using var select = new NpgsqlCommand(
            "SELECT " + string.Join(", ", InjectionRecordContract.Columns.Select(column => column + "::text")) +
            ", last_injected_at BETWEEN @before AND @after FROM agent_experience.experience_injections ORDER BY experience_id",
            connection);
        select.Parameters.Add(new("before", NpgsqlDbType.TimestampTz) { Value = before });
        select.Parameters.Add(new("after", NpgsqlDbType.TimestampTz) { Value = after });
        var rows = new JsonArray();
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new JsonObject();
            for (var i = 0; i < InjectionRecordContract.Columns.Count; i++)
            {
                row[InjectionRecordContract.Columns[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
            }

            if (clamped.Contains(Guid.Parse(reader.GetString(0))))
            {
                Assert.True(reader.GetBoolean(InjectionRecordContract.Columns.Count), $"{reader.GetString(0)}: {reader.GetString(7)} is not the database clock during the steps.");
                row["last_injected_at"] = InjectionRecordContract.DatabaseClock;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string Bound(string? value) => value is null ? string.Empty : "=" + value;
}
