using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.11's provider-holdout-record family, against a schema the .NET migrator applied
/// with row-level security on. Each case runs twice, each time in a database of its own: once through the .NET store's
/// <see cref="PostgresExperienceRecordStore.RecordProviderHoldoutAssignmentAsync"/> (which generates
/// <c>expected.json</c>), and once with <c>contract/sql/record-provider-holdout-assignment.sql</c> run exactly as
/// written, as a port runs it, as the application role granted the contract's 1.0, 1.4 and 1.11 privileges files and
/// nothing else. Both must leave the checked-in rows.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class ProviderHoldoutRecordContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_provider_holdout_record_fixture_is_what_the_dotnet_store_and_the_contract_statement_leave()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in ProviderHoldoutRecordContract.Cases)
        {
            var directory = $"fixtures/{ProviderHoldoutRecordContract.Family}/{name}";
            var seedJson = ContractJson.Write(ProviderHoldoutRecordContract.ToSeedJson(definition));
            var stepsJson = ContractJson.Write(ProviderHoldoutRecordContract.ToStepsJson(definition));
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/steps.json", stepsJson, drift);

            var seed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsObject();
            var steps = ProviderHoldoutRecordContract.ReadSteps(JsonNode.Parse(ContractFiles.Read($"{directory}/steps.json") ?? stepsJson)!);

            var byStore = await RunAsync($"phold_{name}_store", seed, steps, definition.Clamped, throughStore: true);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(byStore), drift);

            var byStatement = await RunAsync($"phold_{name}_sql", seed, steps, definition.Clamped, throughStore: false);
            if (!JsonNode.DeepEquals(byStore, byStatement))
            {
                drift.Add($"{directory}: the contract statement leaves {byStatement.ToJsonString()}, the .NET store {byStore.ToJsonString()}.");
            }
        }

        ContractFiles.VerifyNoStray(
            ProviderHoldoutRecordContract.Family,
            ProviderHoldoutRecordContract.Cases.Select(entry => entry.Name),
            ["seed.json", "steps.json", "expected.json"],
            drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    /// <summary>Seeds a fresh database, runs the steps, and reads the table back as <c>expected.json</c> holds it.</summary>
    private async Task<JsonObject> RunAsync(
        string caseName,
        JsonObject seed,
        IReadOnlyList<InjectionStep> steps,
        IReadOnlyList<(string, Guid, string)> clamped,
        bool throughStore)
    {
        var label = caseName.Replace('-', '_');
        var application = await database.CreateAsync(label);
        var owner = database.OwnerOf(label);
        if (!throughStore)
        {
            // What a port's setup runs: the contract's privileges files alone, the 1.11 one last.
            foreach (var file in new[] { "sql/application-role-privileges.sql", "sql/application-role-privileges-1.4.sql", ProviderHoldoutStatements.PrivilegesFile })
            {
                await ContractPrivilegesOrderPostgresTests.ApplyContractFileAsync(owner, file);
            }
        }

        foreach (var record in seed["records"]!.AsArray())
        {
            await SearchContractPostgresTests.InsertAsync(application, record!);
        }

        var before = await InjectionRecordContractPostgresTests.ClockAsync(owner);
        var store = new PostgresExperienceRecordStore(application);
        foreach (var step in steps)
        {
            switch (step)
            {
                case RecordProviderHoldoutStep record:
                    await RecordAsync(record);
                    break;
                case EraseStep erase:
                    await InjectionRecordContractPostgresTests.EraseAsync(owner, erase);
                    break;
                case EraseDuringProviderRecordStep race:
                    await HoldoutRecordContractPostgresTests.EraseDuringAsync(owner, race.Erase, () => RecordAsync(race.Record));
                    break;
            }
        }

        async Task RecordAsync(RecordProviderHoldoutStep record)
        {
            if (!throughStore)
            {
                await RecordAsWrittenAsync(application, record);
                return;
            }

            var result = await store.RecordProviderHoldoutAssignmentAsync(
                SearchContractPostgresTests.Authorization(record.Scope),
                record.Scope,
                new ProviderHoldoutAssignment(record.RunId, record.ProviderKey, record.Arm, record.AssignedAt),
                CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
        }

        var after = await InjectionRecordContractPostgresTests.ClockAsync(owner);
        var rows = await RowsAsync(owner, clamped, before, after);

        // Each case has a database of its own: release its pooled connections now, or the cases exhaust the server's slots.
        await application.DisposeAsync();
        await owner.DisposeAsync();
        return new JsonObject { [ProviderHoldoutStatements.Table] = rows };
    }

    /// <summary>
    /// <c>contract/sql/declare-bounds.sql</c> for the step's scope, then <c>contract/sql/record-provider-holdout-assignment.sql</c>
    /// as written, the instant truncated to whole microseconds.
    /// </summary>
    internal static async Task RecordAsWrittenAsync(NpgsqlDataSource application, RecordProviderHoldoutStep step)
    {
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var scope = step.Scope;
        await using (var declare = new NpgsqlCommand(ContractFiles.Read("sql/declare-bounds.sql"), connection, transaction))
        {
            declare.Parameters.Add(new("auth_tenant", NpgsqlDbType.Text) { Value = "=" + scope.TenantId });
            declare.Parameters.Add(new("auth_application", NpgsqlDbType.Text) { Value = Bound(scope.ApplicationId) });
            declare.Parameters.Add(new("auth_project", NpgsqlDbType.Text) { Value = Bound(scope.ProjectId) });
            declare.Parameters.Add(new("auth_team", NpgsqlDbType.Text) { Value = Bound(scope.TeamId) });
            declare.Parameters.Add(new("auth_agent", NpgsqlDbType.Text) { Value = Bound(scope.AgentId) });
            declare.Parameters.Add(new("auth_user", NpgsqlDbType.Text) { Value = Bound(scope.UserId) });
            await declare.ExecuteNonQueryAsync();
        }

        await using (var record = new NpgsqlCommand(ContractFiles.Read(ProviderHoldoutStatements.RecordFile), connection, transaction))
        {
            var ticks = step.AssignedAt.UtcTicks;
            record.Parameters.Add(new("run_id", NpgsqlDbType.Uuid) { Value = step.RunId });
            record.Parameters.Add(new("provider_key", NpgsqlDbType.Text) { Value = step.ProviderKey });
            record.Parameters.Add(new("arm", NpgsqlDbType.Text) { Value = step.Arm.ToString() });
            record.Parameters.Add(new("assigned_at", NpgsqlDbType.TimestampTz) { Value = new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero) });
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

    /// <summary>
    /// Every row of the table, ordered by <c>tenant_id</c>, <c>run_id</c> then <c>provider_key</c>, each column in
    /// PostgreSQL's text form in UTC; a clamped row's instant must lie between the database clock before and after the
    /// steps, and is written as <see cref="InjectionRecordContract.DatabaseClock"/>.
    /// </summary>
    internal static async Task<JsonArray> RowsAsync(
        NpgsqlDataSource owner,
        IReadOnlyList<(string TenantId, Guid RunId, string ProviderKey)> clamped,
        DateTimeOffset before,
        DateTimeOffset after)
    {
        await using var connection = await owner.OpenConnectionAsync();
        await using (var zone = new NpgsqlCommand("SET TIME ZONE 'UTC'", connection))
        {
            await zone.ExecuteNonQueryAsync();
        }

        var columns = ProviderHoldoutRecordContract.Columns;
        await using var select = new NpgsqlCommand(
            "SELECT " + string.Join(", ", columns.Select(column => column + "::text")) +
            $", assigned_at BETWEEN @before AND @after FROM agent_experience.{ProviderHoldoutStatements.Table} ORDER BY tenant_id, run_id, provider_key",
            connection);
        select.Parameters.Add(new("before", NpgsqlDbType.TimestampTz) { Value = before });
        select.Parameters.Add(new("after", NpgsqlDbType.TimestampTz) { Value = after });
        var rows = new JsonArray();
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new JsonObject();
            for (var i = 0; i < columns.Count; i++)
            {
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
            }

            var key = (reader.GetString(3), Guid.Parse(reader.GetString(0)), reader.GetString(1));
            if (clamped.Contains(key))
            {
                Assert.True(reader.GetBoolean(columns.Count), $"{key}: {reader.GetString(9)} is not the database clock during the steps.");
                row["assigned_at"] = InjectionRecordContract.DatabaseClock;
            }

            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public async Task Without_the_1_11_grants_the_contract_statements_are_refused_and_with_them_they_record_and_read()
    {
        const string CaseName = "provider_holdout_privileges";
        var application = await database.CreateAsync(CaseName);
        var owner = database.OwnerOf(CaseName);
        foreach (var file in new[] { "sql/application-role-privileges.sql", "sql/application-role-privileges-1.4.sql", HoldoutStatements.PrivilegesFile })
        {
            await ContractPrivilegesOrderPostgresTests.ApplyContractFileAsync(owner, file);
        }

        var step = new RecordProviderHoldoutStep(Fixed.Scope, Fixed.Id(0xd6, 0x901), "mem0", HoldoutArm.Holdout, Fixed.T0);

        // The 1.0 file's REVOKE ALL took 0029's grants away: the statement is refused, as a port reports it NotRecorded.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => RecordAsWrittenAsync(application, step));
        Assert.Equal("42501", refused.SqlState);

        await ContractPrivilegesOrderPostgresTests.ApplyContractFileAsync(owner, ProviderHoldoutStatements.PrivilegesFile);
        await RecordAsWrittenAsync(application, step);
        Assert.Single(await RowsAsync(owner, [], Fixed.T0, Fixed.T0));

        // The read, as written: the first page, then the next one after its last run, as the application role.
        var first = await ReadAsWrittenAsync(application, Fixed.Scope, "mem0", limit: 1, after: null);
        Assert.Equal([(step.RunId, "Holdout")], first);
        Assert.Empty(await ReadAsWrittenAsync(application, Fixed.Scope, "mem0", limit: 1, after: step.RunId));
        Assert.Empty(await ReadAsWrittenAsync(application, Fixed.Scope, "zep", limit: 1, after: null));

        // And the .NET call after it changes nothing it would refuse.
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner,
            new ExperienceApplicationRoleOptions(ContractPostgresDatabase.ApplicationRole) { EnableRowLevelSecurity = true },
            CancellationToken.None);
        await RecordAsWrittenAsync(application, step with { RunId = Fixed.Id(0xd6, 0x902) });
        Assert.Equal(2, (await RowsAsync(owner, [], Fixed.T0, Fixed.T0)).Count);
        await application.DisposeAsync();
        await owner.DisposeAsync();
    }

    /// <summary><c>contract/sql/query-provider-holdout-outcomes.sql</c>'s page as written: each row's run and arm.</summary>
    private static async Task<List<(Guid RunId, string Arm)>> ReadAsWrittenAsync(NpgsqlDataSource application, Scope scope, string key, int limit, Guid? after)
    {
        var statements = VectorStatements.Read(ProviderHoldoutStatements.QueryFile);
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var declare = new NpgsqlCommand(ContractFiles.Read("sql/declare-bounds.sql"), connection, transaction))
        {
            declare.Parameters.Add(new("auth_tenant", NpgsqlDbType.Text) { Value = "=" + scope.TenantId });
            declare.Parameters.Add(new("auth_application", NpgsqlDbType.Text) { Value = Bound(scope.ApplicationId) });
            declare.Parameters.Add(new("auth_project", NpgsqlDbType.Text) { Value = Bound(scope.ProjectId) });
            declare.Parameters.Add(new("auth_team", NpgsqlDbType.Text) { Value = Bound(scope.TeamId) });
            declare.Parameters.Add(new("auth_agent", NpgsqlDbType.Text) { Value = Bound(scope.AgentId) });
            declare.Parameters.Add(new("auth_user", NpgsqlDbType.Text) { Value = Bound(scope.UserId) });
            await declare.ExecuteNonQueryAsync();
        }

        await using var read = new NpgsqlCommand(statements[after is null ? "first-page" : "next-page"], connection, transaction);
        read.Parameters.Add(new("tenant_id", NpgsqlDbType.Text) { Value = scope.TenantId });
        read.Parameters.Add(new("application_id", NpgsqlDbType.Text) { Value = scope.ApplicationId });
        read.Parameters.Add(new("project_id", NpgsqlDbType.Text) { Value = scope.ProjectId });
        read.Parameters.Add(new("team_id", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
        read.Parameters.Add(new("agent_id", NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
        read.Parameters.Add(new("user_id", NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
        read.Parameters.Add(new("provider_key", NpgsqlDbType.Text) { Value = key });
        read.Parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = limit });
        if (after is { } run)
        {
            read.Parameters.Add(new("after_run_id", NpgsqlDbType.Uuid) { Value = run });
        }

        var rows = new List<(Guid, string)>();
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetGuid(18), reader.GetString(19)));
            }
        }

        await transaction.CommitAsync();
        return rows;
    }

    private static string Bound(string? value) => value is null ? string.Empty : "=" + value;
}
