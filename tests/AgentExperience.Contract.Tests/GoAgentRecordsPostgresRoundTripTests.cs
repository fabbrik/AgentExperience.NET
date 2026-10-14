using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Retrieval;
using AgentExperience.Storage.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 22.6's cross-language proof, the .NET half. A Go MAF agent wrote two runs through the Go PostgreSQL store;
/// the Go tests check that the rows it wrote, every column it writes in PostgreSQL's own text form, are exactly
/// <c>go/maf/testdata/cross-language/go-agent-rows.json</c>, and that the records read back are exactly
/// <c>go-agent-records.json</c>. Here those raw rows are inserted as they are, each value cast from its text by
/// PostgreSQL (no .NET encoding in between), as the application role under declared bounds. Then the .NET PostgreSQL
/// record store must read every record back as <c>go-agent-records.json</c>, each initial lifecycle event must be the
/// one .NET finalization derives for its run, and the .NET retrieval (candidate source and ranking) must find the Go
/// agent's lesson for a similar task. Test-only: the files are not part of the contract, so they are outside
/// <c>contract/</c> and its lock.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class GoAgentRecordsPostgresRoundTripTests(ContractPostgresDatabase database)
{
    private static readonly string Directory = Path.Combine(
        Path.GetDirectoryName(ContractFiles.Root)!, "go", "maf", "testdata", "cross-language");

    private static readonly string RowsPath = Path.Combine(Directory, "go-agent-rows.json");

    private static readonly string RecordsPath = Path.Combine(Directory, "go-agent-records.json");

    [Fact]
    public async Task Every_row_the_go_agent_wrote_reads_back_through_the_store_unchanged_and_is_found_by_retrieval()
    {
        Assert.True(File.Exists(RowsPath), $"{RowsPath} is missing.");
        Assert.True(File.Exists(RecordsPath), $"{RecordsPath} is missing.");
        var rows = JsonNode.Parse(await File.ReadAllTextAsync(RowsPath))!.AsObject();
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(RecordsPath))!.AsArray();
        var recordRows = rows["experience_records"]!.AsArray();
        var eventRows = rows["lifecycle_events"]!.AsArray();
        Assert.NotEmpty(recordRows);
        Assert.Equal(expected.Count, recordRows.Count);
        Assert.Equal(recordRows.Count, eventRows.Count);

        await using var application = await database.CreateAsync("go_agent_rows");
        foreach (var row in recordRows)
        {
            await InsertRawAsync(application, "experience_records", row!.AsObject());
        }

        foreach (var row in eventRows)
        {
            await InsertRawAsync(application, "lifecycle_events", row!.AsObject());
        }

        var problems = new List<string>();
        var store = new PostgresExperienceRecordStore(application);
        var records = new List<ExperienceRecord>();
        foreach (var stored in expected)
        {
            RecordSchema.Check(RecordsPath, stored!, problems);
            var scope = ScopeOf(stored!["columns"]!);
            var id = Guid.Parse(stored["columns"]!["experience_id"]!.GetValue<string>());
            var read = await store.GetAsync(SearchContractPostgresTests.Authorization(scope), scope, id, CancellationToken.None);
            if (read.Outcome != ExperienceStoreOutcome.Found)
            {
                problems.Add($"{id}: the store answered {read.Outcome}.");
                continue;
            }

            records.Add(read.Record!);
            var again = ContractJson.ToStored(read.Record!);
            if (!JsonNode.DeepEquals(again, stored))
            {
                problems.Add($"{id}: read back differently.\n    go:   {stored.ToJsonString()}\n    read: {again.ToJsonString()}");
            }
        }

        // Each initial event is the one .NET finalization derives for the record's run, and takes it to its status.
        foreach (var record in records)
        {
            var eventId = ExperienceFinalizationService.InitialEventIdFor(record.SourceRunId, record.Scope);
            var initial = eventRows.Select(row => row!.AsObject())
                .SingleOrDefault(row => Guid.Parse(row["event_id"]!.GetValue<string>()) == eventId);
            if (initial is null)
            {
                problems.Add($"{record.ExperienceId}: no initial event with the derived ID {eventId}.");
                continue;
            }

            if (Guid.Parse(initial["experience_id"]!.GetValue<string>()) != record.ExperienceId
                || record.ExperienceId != ExperienceFinalizationService.ExperienceIdFor(record.SourceRunId, record.Scope)
                || initial["current_status"]!.GetValue<string>() != record.Status.ToString()
                || initial["prior_status"]!.GetValue<string>() != nameof(ExperienceStatus.Candidate)
                || initial["expected_revision"]!.GetValue<string>() != "0"
                || long.Parse(initial["applied_revision"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) != record.Revision)
            {
                problems.Add($"{record.ExperienceId}: the initial event is not the record's own: {initial.ToJsonString()}");
            }
        }

        // And the other way round: .NET's retrieval finds the Go agent's lesson for a similar task.
        Assert.NotEmpty(records);
        var lessonScope = records[0].Scope;
        var retrieval = new ExperienceRetrievalService(
            new PostgresExperienceCandidateSource(application),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            new FixedTimeProvider(records[^1].UpdatedAt));
        var result = await retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(
                SearchContractPostgresTests.Authorization(lessonScope),
                lessonScope,
                "Ticket #5120: a refund is stuck on a lock. Triage it."),
            CancellationToken.None);
        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        var found = result.Records.Select(entry => entry.Record.ExperienceId).ToList();
        if (!records.All(record => found.Contains(record.ExperienceId)))
        {
            problems.Add($"The .NET retrieval found [{string.Join(", ", found)}], not every Go lesson.");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Inserts one raw row as the application role, under bounds declared for its scope: each column's value is the
    /// text the Go test read from PostgreSQL, cast back to the column's own type by PostgreSQL.
    /// </summary>
    private static async Task InsertRawAsync(NpgsqlDataSource application, string table, JsonObject row)
    {
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var zone = new NpgsqlCommand("SET LOCAL TIME ZONE 'UTC'", connection, transaction))
        {
            await zone.ExecuteNonQueryAsync();
        }

        var scope = ScopeOf(row);
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

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var columns = new NpgsqlCommand(
            "SELECT attname, format_type(atttypid, atttypmod) FROM pg_attribute " +
            "WHERE attrelid = ('agent_experience.' || @table)::regclass AND attnum > 0 AND NOT attisdropped",
            connection,
            transaction))
        {
            columns.Parameters.Add(new("table", NpgsqlDbType.Text) { Value = table });
            await using var reader = await columns.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                types[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var names = row.Select(member => member.Key).ToList();
        Assert.All(names, name => Assert.True(types.ContainsKey(name), $"{table} has no column {name}."));
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO agent_experience.{table} ({string.Join(", ", names)}) VALUES (" +
            string.Join(", ", names.Select((name, i) => $"CAST(@p{i} AS {types[name]})")) + ")",
            connection,
            transaction);
        for (var i = 0; i < names.Count; i++)
        {
            insert.Parameters.Add(new($"p{i}", NpgsqlDbType.Text) { Value = (object?)row[names[i]]?.GetValue<string>() ?? DBNull.Value });
        }

        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static Scope ScopeOf(JsonNode columns) => new(
        columns["tenant_id"]!.GetValue<string>(),
        columns["application_id"]!.GetValue<string>(),
        columns["project_id"]!.GetValue<string>(),
        columns["team_id"]?.GetValue<string>(),
        columns["agent_id"]?.GetValue<string>(),
        columns["user_id"]?.GetValue<string>());

    private static string Bound(string? value) => value is null ? string.Empty : "=" + value;
}
