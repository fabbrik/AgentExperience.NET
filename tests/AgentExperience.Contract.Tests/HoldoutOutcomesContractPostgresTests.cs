using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.9's holdout-outcomes family, against a schema the .NET migrator applied with
/// row-level security on. Each case seeds a database as a port would (the record columns, and the assignment rows as the
/// owner), then pages through the .NET store's <see cref="PostgresExperienceRecordStore.QueryHoldoutOutcomesAsync"/>
/// (which generates <c>expected.json</c>), and runs <c>contract/sql/query-holdout-outcomes.sql</c> exactly as written,
/// decoding its rows as README-1.9.md tells a port to: both must give the same pages.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class HoldoutOutcomesContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_holdout_outcomes_fixture_is_what_the_dotnet_store_and_the_contract_statement_read()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in HoldoutOutcomesContract.Cases)
        {
            var directory = $"fixtures/{HoldoutOutcomesContract.Family}/{name}";
            var seedJson = ContractJson.Write(HoldoutOutcomesContract.ToSeedJson(definition));
            var queryJson = ContractJson.Write(HoldoutOutcomesContract.ToQueryJson(definition.Query));
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/query.json", queryJson, drift);

            var seed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsObject();
            var query = HoldoutOutcomesContract.ReadQuery(ContractFiles.Read($"{directory}/query.json") ?? queryJson);

            var label = "outcomes_" + name.Replace('-', '_');
            var application = await database.CreateAsync(label);
            var owner = database.OwnerOf(label);
            foreach (var record in seed["records"]!.AsArray())
            {
                await SearchContractPostgresTests.InsertAsync(application, record!);
            }

            foreach (var row in seed["assignments"]!.AsArray())
            {
                await InsertAssignmentAsync(owner, row!.AsObject());
            }

            foreach (var erased in seed["erased"]!.AsArray())
            {
                await InjectionRecordContractPostgresTests.EraseAsync(owner, new EraseStep(
                    Guid.Parse(erased!["experienceId"]!.GetValue<string>()),
                    InjectionRecordContract.ScopeOf(erased["scope"]!),
                    DateTimeOffset.Parse(erased["deletedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
            }

            var byStore = await PagesThroughStoreAsync(application, query);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(byStore), drift);

            var byStatement = await PagesAsWrittenAsync(application, query);
            if (!JsonNode.DeepEquals(byStore, byStatement))
            {
                drift.Add($"{directory}: the contract statement reads {byStatement.ToJsonString()}, the .NET store {byStore.ToJsonString()}.");
            }

            // Idle pooled connections of every case's database would exhaust the server's slots.
            application.Clear();
            owner.Clear();
        }

        ContractFiles.VerifyNoStray(
            HoldoutOutcomesContract.Family,
            HoldoutOutcomesContract.Cases.Select(entry => entry.Name),
            ["seed.json", "query.json", "expected.json"],
            drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    /// <summary>Every page the .NET store reads, each after the previous page's last row, until one is not full.</summary>
    private static async Task<JsonObject> PagesThroughStoreAsync(NpgsqlDataSource application, HoldoutOutcomesQuery query)
    {
        var store = new PostgresExperienceRecordStore(application);
        var pages = new JsonArray();
        HoldoutOutcomeCursor? after = null;
        while (true)
        {
            var page = await store.QueryHoldoutOutcomesAsync(
                SearchContractPostgresTests.Authorization(query.Scope),
                new HoldoutOutcomeQuery(query.Scope, query.Limit, after),
                CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Found, page.Outcome);
            pages.Add(HoldoutOutcomesContract.PageJson(after, page.Rows));
            if (page.Rows.Count < query.Limit)
            {
                return new JsonObject { ["pages"] = pages };
            }

            after = new HoldoutOutcomeCursor(page.Rows[^1].RunId, page.Rows[^1].ExperienceId);
        }
    }

    /// <summary>
    /// The same pages read with <c>declare-bounds.sql</c> and the file's statements as written, each row decoded as a
    /// port decodes it: a run's outcome and model from its part 0 record's payload, a lesson's model from its part 1
    /// record's payload, a model absent or blank read as none.
    /// </summary>
    private static async Task<JsonObject> PagesAsWrittenAsync(NpgsqlDataSource application, HoldoutOutcomesQuery query)
    {
        var statements = VectorStatements.Read(BenefitStatements.QueryFile);
        var pages = new JsonArray();
        HoldoutOutcomeCursor? after = null;
        while (true)
        {
            await using var connection = await application.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SearchContractPostgresTests.DeclareAsync(connection, transaction, query.Scope);
            await using var command = new NpgsqlCommand(statements[after is null ? "first-page" : "next-page"], connection, transaction);
            var scope = query.Scope;
            command.Parameters.Add(new("tenant_id", NpgsqlDbType.Text) { Value = scope.TenantId });
            command.Parameters.Add(new("application_id", NpgsqlDbType.Text) { Value = scope.ApplicationId });
            command.Parameters.Add(new("project_id", NpgsqlDbType.Text) { Value = scope.ProjectId });
            command.Parameters.Add(new("team_id", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
            command.Parameters.Add(new("agent_id", NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
            command.Parameters.Add(new("user_id", NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
            command.Parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = query.Limit });
            if (after is not null)
            {
                command.Parameters.Add(new("after_run_id", NpgsqlDbType.Uuid) { Value = after.RunId });
                command.Parameters.Add(new("after_experience_id", NpgsqlDbType.Uuid) { Value = after.ExperienceId });
            }

            var page = new List<(Guid RunId, HoldoutArm Arm, Guid ExperienceId, DateTimeOffset AssignedAt, JsonNode? Run)>();
            var lessons = new Dictionary<Guid, string?>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.Equal(23, reader.FieldCount);
                Assert.Equal("part", reader.GetName(22));
                while (await reader.ReadAsync())
                {
                    var payload = reader.IsDBNull(17) ? null : JsonNode.Parse(reader.GetString(17));
                    if (reader.GetInt32(22) == 0)
                    {
                        page.Add((reader.GetGuid(18), Enum.Parse<HoldoutArm>(reader.GetString(19)), reader.GetGuid(20), reader.GetFieldValue<DateTimeOffset>(21), payload));
                    }
                    else
                    {
                        lessons[reader.GetGuid(0)] = ModelOf(payload);
                    }
                }
            }

            await transaction.CommitAsync();
            var rows = page.Select(row => new HoldoutOutcomeRow(
                    row.RunId,
                    row.Arm,
                    row.ExperienceId,
                    row.AssignedAt,
                    row.Run?["outcome"]?["status"] is { } status ? Enum.Parse<TaskVerificationStatus>(status.GetValue<string>()) : null)
                {
                    RunModel = ModelOf(row.Run),
                    LessonModel = lessons.GetValueOrDefault(row.ExperienceId),
                })
                .ToList();
            pages.Add(HoldoutOutcomesContract.PageJson(after, rows));
            if (rows.Count < query.Limit)
            {
                return new JsonObject { ["pages"] = pages };
            }

            after = new HoldoutOutcomeCursor(rows[^1].RunId, rows[^1].ExperienceId);
        }
    }

    private static string? ModelOf(JsonNode? payload) =>
        payload?["environment"]?["metadata"]?[HoldoutOutcomeRow.ModelMetadataKey]?.GetValue<string>() is { } model && !string.IsNullOrWhiteSpace(model)
            ? model
            : null;

    /// <summary>One assignment row, every column from its PostgreSQL text form, as the schema's owner.</summary>
    internal static async Task InsertAssignmentAsync(NpgsqlDataSource owner, JsonObject row)
    {
        var columns = HoldoutRecordContract.Columns;
        await using var command = owner.CreateCommand(
            "INSERT INTO agent_experience.experience_holdout_assignments (" + string.Join(", ", columns) + ") VALUES (" +
            "CAST(@p0 AS uuid), CAST(@p1 AS uuid), CAST(@p2 AS bigint), @p3, @p4, @p5, @p6, @p7, @p8, @p9, CAST(@p10 AS timestamptz))");
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add(new($"p{i}", NpgsqlDbType.Text) { Value = (object?)row[columns[i]]?.GetValue<string>() ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync();
    }
}
