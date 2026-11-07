using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.8's holdout-record family, against a schema the .NET migrator applied with
/// row-level security on. Each case runs twice, each time in a database of its own: once through the .NET store's
/// <see cref="PostgresExperienceRecordStore.RecordHoldoutAssignmentAsync"/> (which generates <c>expected.json</c>), and once
/// with <c>contract/sql/record-holdout-assignment.sql</c> run exactly as written, as a port runs it, as the application
/// role granted the contract's 1.0, 1.4 and 1.8 privileges files and nothing else (the 1.6 file needs the vectors
/// migrations, which this database does not have). Both must leave the checked-in rows.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class HoldoutRecordContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_holdout_record_fixture_is_what_the_dotnet_store_and_the_contract_statement_leave()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in HoldoutRecordContract.Cases)
        {
            var directory = $"fixtures/{HoldoutRecordContract.Family}/{name}";
            var seedJson = ContractJson.Write(HoldoutRecordContract.ToSeedJson(definition));
            var stepsJson = ContractJson.Write(HoldoutRecordContract.ToStepsJson(definition));
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/steps.json", stepsJson, drift);

            var seed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsObject();
            var steps = HoldoutRecordContract.ReadSteps(JsonNode.Parse(ContractFiles.Read($"{directory}/steps.json") ?? stepsJson)!);

            var byStore = await RunAsync($"hold_{name}_store", seed, steps, definition.Clamped, throughStore: true);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(byStore), drift);

            var byStatement = await RunAsync($"hold_{name}_sql", seed, steps, definition.Clamped, throughStore: false);
            if (!JsonNode.DeepEquals(byStore, byStatement))
            {
                drift.Add($"{directory}: the contract statement leaves {byStatement.ToJsonString()}, the .NET store {byStore.ToJsonString()}.");
            }
        }

        ContractFiles.VerifyNoStray(
            HoldoutRecordContract.Family,
            HoldoutRecordContract.Cases.Select(entry => entry.Name),
            ["seed.json", "steps.json", "expected.json"],
            drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    /// <summary>Seeds a fresh database, runs the steps, and reads the table back as <c>expected.json</c> holds it.</summary>
    private async Task<JsonObject> RunAsync(string caseName, JsonObject seed, IReadOnlyList<InjectionStep> steps, IReadOnlyList<(Guid, Guid)> clamped, bool throughStore)
    {
        var label = caseName.Replace('-', '_');
        var application = await database.CreateAsync(label);
        var owner = database.OwnerOf(label);
        if (!throughStore)
        {
            // What a port's setup runs: the contract's privileges files alone, the 1.8 one last.
            foreach (var file in new[] { "sql/application-role-privileges.sql", "sql/application-role-privileges-1.4.sql", HoldoutStatements.PrivilegesFile })
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
                case RecordHoldoutStep record:
                    await RecordAsync(record);
                    break;
                case EraseStep erase:
                    await InjectionRecordContractPostgresTests.EraseAsync(owner, erase);
                    break;
                case EraseDuringRecordStep race:
                    await EraseDuringAsync(owner, race.Erase, () => RecordAsync(race.Record));
                    break;
            }
        }

        async Task RecordAsync(RecordHoldoutStep record)
        {
            if (!throughStore)
            {
                await RecordAsWrittenAsync(application, record);
                return;
            }

            var result = await store.RecordHoldoutAssignmentAsync(
                SearchContractPostgresTests.Authorization(record.Scope),
                record.Scope,
                new HoldoutAssignment(record.RunId, record.Arm, record.Records, record.AssignedAt),
                CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
        }

        var after = await InjectionRecordContractPostgresTests.ClockAsync(owner);
        return new JsonObject { ["experience_holdout_assignments"] = await RowsAsync(owner, clamped, before, after) };
    }

    /// <summary>
    /// The late-write guard's race, as a runner stages it: the erasure runs as the owner in a transaction it does not yet
    /// commit; the record call starts, and once it is blocked on that transaction (the purging backend is among the
    /// record's backend's <c>pg_blocking_pids</c>), the erasure commits and the record call is awaited.
    /// </summary>
    internal static async Task EraseDuringAsync(NpgsqlDataSource owner, EraseStep erase, Func<Task> record)
    {
        await using var purging = await owner.OpenConnectionAsync();
        await using var transaction = await purging.BeginTransactionAsync();
        int purger;
        await using (var pid = new NpgsqlCommand("SELECT pg_catalog.pg_backend_pid()", purging, transaction))
        {
            purger = (int)(await pid.ExecuteScalarAsync())!;
        }

        await using (var command = new NpgsqlCommand(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(@id, @tenant_id, @application_id, @project_id, " +
            "@team_id, @agent_id, @user_id, NULL, @deleted_at)",
            purging,
            transaction))
        {
            var scope = erase.Scope;
            command.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = erase.ExperienceId });
            command.Parameters.Add(new("tenant_id", NpgsqlDbType.Text) { Value = scope.TenantId });
            command.Parameters.Add(new("application_id", NpgsqlDbType.Text) { Value = scope.ApplicationId });
            command.Parameters.Add(new("project_id", NpgsqlDbType.Text) { Value = scope.ProjectId });
            command.Parameters.Add(new("team_id", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
            command.Parameters.Add(new("agent_id", NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
            command.Parameters.Add(new("user_id", NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
            command.Parameters.Add(new("deleted_at", NpgsqlDbType.TimestampTz) { Value = erase.DeletedAt });
            Assert.Equal("Deleted", (string?)await command.ExecuteScalarAsync());
        }

        var recording = Task.Run(record);
        await using (var watcher = await owner.OpenConnectionAsync())
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                Assert.False(recording.IsCompleted, "The record call completed while the erasure held the run's record: it never waited for it.");
                await using var blocked = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity a WHERE a.datname = pg_catalog.current_database() " +
                    "AND @purger = ANY (pg_catalog.pg_blocking_pids(a.pid)))",
                    watcher);
                blocked.Parameters.Add(new("purger", NpgsqlDbType.Integer) { Value = purger });
                if ((bool)(await blocked.ExecuteScalarAsync())!)
                {
                    break;
                }

                Assert.True(DateTime.UtcNow < deadline, "The record call never blocked on the erasure.");
                await Task.Delay(20);
            }
        }

        await transaction.CommitAsync();
        await recording;
    }

    /// <summary>
    /// <c>contract/sql/declare-bounds.sql</c> for the step's scope, then <c>contract/sql/record-holdout-assignment.sql</c>
    /// as written, the instant truncated to whole microseconds.
    /// </summary>
    internal static async Task RecordAsWrittenAsync(NpgsqlDataSource application, RecordHoldoutStep step)
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

        await using (var record = new NpgsqlCommand(ContractFiles.Read(HoldoutStatements.RecordFile), connection, transaction))
        {
            var ticks = step.AssignedAt.UtcTicks;
            record.Parameters.Add(new("run_id", NpgsqlDbType.Uuid) { Value = step.RunId });
            record.Parameters.Add(new("arm", NpgsqlDbType.Text) { Value = step.Arm.ToString() });
            record.Parameters.Add(new("experience_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = step.Records.Select(exposure => exposure.ExperienceId).ToArray() });
            record.Parameters.Add(new("revisions", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = step.Records.Select(exposure => exposure.Revision).ToArray() });
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
    /// Every row of the table, ordered by <c>run_id</c> then <c>experience_id</c>, each column in PostgreSQL's text form in
    /// UTC; a clamped row's instant must lie between the database clock before and after the steps, and is written as
    /// <see cref="InjectionRecordContract.DatabaseClock"/>.
    /// </summary>
    internal static async Task<JsonArray> RowsAsync(NpgsqlDataSource owner, IReadOnlyList<(Guid RunId, Guid ExperienceId)> clamped, DateTimeOffset before, DateTimeOffset after)
    {
        await using var connection = await owner.OpenConnectionAsync();
        await using (var zone = new NpgsqlCommand("SET TIME ZONE 'UTC'", connection))
        {
            await zone.ExecuteNonQueryAsync();
        }

        var columns = HoldoutRecordContract.Columns;
        await using var select = new NpgsqlCommand(
            "SELECT " + string.Join(", ", columns.Select(column => column + "::text")) +
            ", assigned_at BETWEEN @before AND @after FROM agent_experience.experience_holdout_assignments ORDER BY run_id, experience_id",
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

            if (clamped.Contains((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)))))
            {
                Assert.True(reader.GetBoolean(columns.Count), $"{reader.GetString(0)}/{reader.GetString(1)}: {reader.GetString(10)} is not the database clock during the steps.");
                row["assigned_at"] = InjectionRecordContract.DatabaseClock;
            }

            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public async Task Without_the_1_8_grants_the_contract_statement_is_refused_and_with_them_it_records()
    {
        const string CaseName = "holdout_privileges";
        var application = await database.CreateAsync(CaseName);
        var owner = database.OwnerOf(CaseName);
        foreach (var file in new[] { "sql/application-role-privileges.sql", "sql/application-role-privileges-1.4.sql" })
        {
            await ContractPrivilegesOrderPostgresTests.ApplyContractFileAsync(owner, file);
        }

        var record = SearchContract.Seed(0x76, 1, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.");
        await SearchContractPostgresTests.InsertAsync(application, ContractJson.ToStored(record));
        var step = new RecordHoldoutStep(Fixed.Scope, Fixed.Id(0x76, 0x901), HoldoutArm.Holdout, [new(record.ExperienceId, 1)], Fixed.T0);

        // The 1.0 file's REVOKE ALL took 0027's grants away: the statement is refused, as a port reports it NotRecorded.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => RecordAsWrittenAsync(application, step));
        Assert.Equal("42501", refused.SqlState);

        await ContractPrivilegesOrderPostgresTests.ApplyContractFileAsync(owner, HoldoutStatements.PrivilegesFile);
        await RecordAsWrittenAsync(application, step);
        var rows = await RowsAsync(owner, [], Fixed.T0, Fixed.T0);
        Assert.Single(rows);

        // And the .NET call after it changes nothing it would refuse.
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner,
            new ExperienceApplicationRoleOptions(ContractPostgresDatabase.ApplicationRole) { EnableRowLevelSecurity = true },
            CancellationToken.None);
        await RecordAsWrittenAsync(application, step with { RunId = Fixed.Id(0x76, 0x902) });
        Assert.Equal(2, (await RowsAsync(owner, [], Fixed.T0, Fixed.T0)).Count);
    }

    private static string Bound(string? value) => value is null ? string.Empty : "=" + value;
}
