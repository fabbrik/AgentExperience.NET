using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Retrieval;
using AgentExperience.Storage.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The .NET half of a port's "the port writes, .NET reads" proof on PostgreSQL (stories 22.6 and 22.8), shared by the
/// Go and Python ports. The port's own PostgreSQL test runs its agent through its PostgreSQL store, reads back the rows
/// that store wrote, every column it writes in PostgreSQL's own text form, and checks them against a rows file checked
/// in beside a records file (what the port's reader reads them as). Here:
/// <list type="number">
/// <item>The raw rows are inserted as they are, each value cast from its text by PostgreSQL (no .NET encoding in
/// between), as the application role under declared bounds.</item>
/// <item>The .NET PostgreSQL record store must read every record back as the records file holds it.</item>
/// <item>Each initial lifecycle event must be the one .NET finalization derives for its record.</item>
/// <item>The .NET store then writes each record it read, and its initial event, the way finalization does (created
/// as a Candidate, then the initial transition), into a database of its own; every column the port wrote must be
/// exactly what .NET wrote there. A port that encodes a column differently from .NET but reads it back symmetrically
/// passes its own tests, and fails here, naming the column.</item>
/// <item>Optionally, .NET retrieval must find every record for a similar task.</item>
/// </list>
/// Test-only: the files are not part of the contract, so they live with the port's tests, outside <c>contract/</c>
/// and its lock.
/// </summary>
internal static class PortAgentRowsPostgres
{
    private const string RecordsTable = "experience_records";

    private const string EventsTable = "lifecycle_events";

    /// <summary>Runs the whole proof over one port's two files; every problem is collected and reported together.</summary>
    /// <param name="database">The shared PostgreSQL server.</param>
    /// <param name="label">A short database-name-safe label for the port.</param>
    /// <param name="rowsPath">The rows the port's store wrote: <c>experience_records</c> and <c>lifecycle_events</c>, each value as text.</param>
    /// <param name="recordsPath">The records the port's reader reads those rows as, in the contract's stored form.</param>
    /// <param name="similarTask">A task the .NET retrieval must find every record for, or <see langword="null"/> to skip it.</param>
    public static async Task VerifyAsync(ContractPostgresDatabase database, string label, string rowsPath, string recordsPath, string? similarTask)
    {
        Assert.True(File.Exists(rowsPath), $"{rowsPath} is missing.");
        Assert.True(File.Exists(recordsPath), $"{recordsPath} is missing.");
        var rows = JsonNode.Parse(await File.ReadAllTextAsync(rowsPath))!.AsObject();
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(recordsPath))!.AsArray();
        var recordRows = rows[RecordsTable]!.AsArray().Select(row => row!.AsObject()).ToList();
        var eventRows = rows[EventsTable]!.AsArray().Select(row => row!.AsObject()).ToList();
        Assert.NotEmpty(recordRows);
        Assert.Equal(expected.Count, recordRows.Count);
        Assert.Equal(recordRows.Count, eventRows.Count);

        var application = await database.CreateAsync(label + "_rows");
        foreach (var row in recordRows)
        {
            await InsertRawAsync(application, RecordsTable, row);
        }

        foreach (var row in eventRows)
        {
            await InsertRawAsync(application, EventsTable, row);
        }

        var problems = new List<string>();
        var store = new PostgresExperienceRecordStore(application);
        var records = new List<ExperienceRecord>();
        foreach (var stored in expected)
        {
            RecordSchema.Check(recordsPath, stored!, problems);
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
                problems.Add($"{id}: the .NET store reads it back differently at {FirstDifference(stored, again, "$")}.");
            }
        }

        // Each initial event is the one .NET finalization derives for the record's run, and takes it to its status.
        foreach (var record in records)
        {
            var eventId = ExperienceFinalizationService.InitialEventIdFor(record.SourceRunId, record.Scope);
            var initial = eventRows.SingleOrDefault(row => Guid.Parse(row["event_id"]!.GetValue<string>()) == eventId);
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
                || long.Parse(initial["applied_revision"]!.GetValue<string>(), CultureInfo.InvariantCulture) != record.Revision)
            {
                problems.Add($"{record.ExperienceId}: the initial event is not the record's own: {initial.ToJsonString()}");
            }
        }

        // What .NET writes for the same records -- the records the port's own reader reads its rows as, so a column the
        // port writes in its own encoding and reads back symmetrically still differs here: every column the port wrote
        // must be the text .NET writes.
        var twin = await database.CreateAsync(label + "_dotnet");
        foreach (var stored in expected)
        {
            var record = ContractJson.FromStored(stored!);
            var portRecord = recordRows.SingleOrDefault(row => Guid.Parse(row["experience_id"]!.GetValue<string>()) == record.ExperienceId);
            var portEvent = eventRows.SingleOrDefault(row => Guid.Parse(row["experience_id"]!.GetValue<string>()) == record.ExperienceId);
            if (portRecord is null || portEvent is null)
            {
                problems.Add($"{record.ExperienceId}: the port wrote no row or no initial event for it.");
                continue;
            }

            await WriteAsDotNetAsync(twin, record, portEvent["actor"]?.GetValue<string>(), problems);
            Compare(RecordsTable, record.ExperienceId, portRecord, await ReadRawAsync(twin, RecordsTable, record.Scope, "experience_id", record.ExperienceId, portRecord), problems);
            var eventId = Guid.Parse(portEvent["event_id"]!.GetValue<string>());
            Compare(EventsTable, eventId, portEvent, await ReadRawAsync(twin, EventsTable, record.Scope, "event_id", eventId, portEvent), problems);
        }

        // And the other way round: .NET's retrieval finds the port's lessons for a similar task.
        if (similarTask is not null && records.Count > 0)
        {
            var lessonScope = records[0].Scope;
            var retrieval = new ExperienceRetrievalService(
                new PostgresExperienceCandidateSource(application),
                RetrievalPolicy.Default,
                RankingWeights.Default,
                new FixedTimeProvider(records[^1].UpdatedAt));
            var result = await retrieval.RetrieveAsync(
                new RetrieveExperienceRequest(SearchContractPostgresTests.Authorization(lessonScope), lessonScope, similarTask),
                CancellationToken.None);
            Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
            var found = result.Records.Select(entry => entry.Record.ExperienceId).ToList();
            if (!records.All(record => found.Contains(record.ExperienceId)))
            {
                problems.Add($"The .NET retrieval found [{string.Join(", ", found)}], not every lesson the port wrote.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Writes <paramref name="record"/> through the .NET store as finalization does: created as a Candidate at
    /// revision 0, then committed to its status by its initial lifecycle event, on a clock that reads the record's
    /// creation instant (the instant a port records its initial event at), as <paramref name="actor"/>.
    /// </summary>
    private static async Task WriteAsDotNetAsync(NpgsqlDataSource twin, ExperienceRecord record, string? actor, List<string> problems)
    {
        var store = new PostgresExperienceRecordStore(twin, timeProvider: new FixedTimeProvider(record.CreatedAt));
        var scope = record.Scope;
        var authorization = new AuthorizationContext(
            scope.TenantId, actor ?? "contract-principal", [], Fixed.T0, scope.ApplicationId, scope.ProjectId, scope.TeamId, scope.AgentId, scope.UserId);
        var candidate = record with
        {
            Status = ExperienceFinalizationService.CreatedStatus,
            Revision = 0,
            UpdatedAt = record.CreatedAt,
        };
        var created = await store.CreateAsync(authorization, candidate, CancellationToken.None);
        if (created.Outcome != ExperienceStoreOutcome.Created)
        {
            problems.Add($"{record.ExperienceId}: the .NET store could not create it: {created.Outcome}.");
            return;
        }

        var committed = await new ExperienceLifecycleService(store).CommitAsync(
            authorization,
            new CommitLifecycleTransitionRequest(
                ExperienceFinalizationService.InitialEventIdFor(record.SourceRunId, scope),
                record.ExperienceId,
                scope,
                ExperienceFinalizationService.CreatedStatus,
                ExperienceFinalizationService.TargetStatusFor(candidate),
                ExperienceFinalizationService.InitialEventReason(candidate),
                ExperienceFinalizationService.ProducerIdentity,
                record.CreatedAt,
                ExpectedRevision: 0),
            CancellationToken.None);
        if (committed.Outcome != LifecycleTransitionOutcome.Committed)
        {
            problems.Add($"{record.ExperienceId}: the .NET store could not commit its initial event: {committed.Outcome}.");
        }
    }

    /// <summary>Each column the port wrote, against the text .NET wrote for it; a difference names the table, the row and the column.</summary>
    private static void Compare(string table, Guid id, JsonObject port, JsonObject? dotnet, List<string> problems)
    {
        if (dotnet is null)
        {
            problems.Add($"{table} {id}: .NET wrote no such row.");
            return;
        }

        foreach (var (column, value) in port)
        {
            var portText = value?.GetValue<string>();
            var dotnetText = dotnet[column]?.GetValue<string>();
            if (!string.Equals(portText, dotnetText, StringComparison.Ordinal))
            {
                problems.Add($"{table}.{column} of {id}: the port wrote {Show(portText)}, .NET writes {Show(dotnetText)}.");
            }
        }

        static string Show(string? text) => text is null ? "NULL" : $"'{text}'";
    }

    /// <summary>The path of the first place two JSON values differ, with both values there.</summary>
    private static string FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (JsonObject left, JsonObject right):
                foreach (var name in left.Select(member => member.Key).Union(right.Select(member => member.Key)))
                {
                    if (!JsonNode.DeepEquals(left[name], right[name]))
                    {
                        return FirstDifference(left[name], right[name], $"{path}.{name}");
                    }
                }

                break;
            case (JsonArray left, JsonArray right) when left.Count == right.Count:
                for (var i = 0; i < left.Count; i++)
                {
                    if (!JsonNode.DeepEquals(left[i], right[i]))
                    {
                        return FirstDifference(left[i], right[i], $"{path}[{i}]");
                    }
                }

                break;
        }

        return $"{path} (the port's reader: {expected?.ToJsonString() ?? "null"}, .NET: {actual?.ToJsonString() ?? "null"})";
    }

    /// <summary>
    /// One row, the columns <paramref name="shape"/> names, each in PostgreSQL's text form in UTC (as a port's test reads
    /// them), read as the application role under bounds declared for <paramref name="scope"/>.
    /// </summary>
    private static async Task<JsonObject?> ReadRawAsync(NpgsqlDataSource application, string table, Scope scope, string key, Guid id, JsonObject shape)
    {
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PrepareAsync(connection, transaction, scope);
        var names = shape.Select(member => member.Key).ToList();
        await using var select = new NpgsqlCommand(
            $"SELECT {string.Join(", ", names.Select(name => $"{name}::text"))} FROM agent_experience.{table} WHERE {key} = @id",
            connection,
            transaction);
        select.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = id });
        await using var reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var row = new JsonObject();
        for (var i = 0; i < names.Count; i++)
        {
            row[names[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
        }

        return row;
    }

    /// <summary>
    /// Inserts one raw row as the application role, under bounds declared for its scope: each column's value is the
    /// text the port's test read from PostgreSQL, cast back to the column's own type by PostgreSQL.
    /// </summary>
    private static async Task InsertRawAsync(NpgsqlDataSource application, string table, JsonObject row)
    {
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PrepareAsync(connection, transaction, ScopeOf(row));

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

    /// <summary>UTC for the transaction, then <c>contract/sql/declare-bounds.sql</c> for <paramref name="scope"/>.</summary>
    private static async Task PrepareAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Scope scope)
    {
        await using (var zone = new NpgsqlCommand("SET LOCAL TIME ZONE 'UTC'", connection, transaction))
        {
            await zone.ExecuteNonQueryAsync();
        }

        await using var declare = new NpgsqlCommand(ContractFiles.Read("sql/declare-bounds.sql"), connection, transaction);
        declare.Parameters.Add(new("auth_tenant", NpgsqlDbType.Text) { Value = "=" + scope.TenantId });
        declare.Parameters.Add(new("auth_application", NpgsqlDbType.Text) { Value = Bound(scope.ApplicationId) });
        declare.Parameters.Add(new("auth_project", NpgsqlDbType.Text) { Value = Bound(scope.ProjectId) });
        declare.Parameters.Add(new("auth_team", NpgsqlDbType.Text) { Value = Bound(scope.TeamId) });
        declare.Parameters.Add(new("auth_agent", NpgsqlDbType.Text) { Value = Bound(scope.AgentId) });
        declare.Parameters.Add(new("auth_user", NpgsqlDbType.Text) { Value = Bound(scope.UserId) });
        await declare.ExecuteNonQueryAsync();
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
