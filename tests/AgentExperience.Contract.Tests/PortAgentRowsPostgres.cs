using System.Globalization;
using System.Text.Json;
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
/// between), as the application role under declared bounds. A column the port does not write is left to the
/// database, as the port's store leaves it.</item>
/// <item>The .NET PostgreSQL record store must read every record back as the records file holds it.</item>
/// <item>Each initial lifecycle event must be the one .NET finalization derives for its record.</item>
/// <item>The .NET store then writes each record of the records file, and its initial event, the way finalization
/// does (created as a Candidate, then the initial transition), into a database of its own. Every column of both
/// tables -- not only the ones the port writes -- must then hold the same text in the port's row as in .NET's: a
/// port that encodes a column differently from .NET but reads it back symmetrically, or that leaves a column to the
/// database where .NET writes it, passes its own tests and fails here, naming the column.</item>
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
        var problems = new List<string>();
        var recordRows = RowsOf(rows, RecordsTable, "experience_id", problems);
        var eventRows = RowsOf(rows, EventsTable, "event_id", problems);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.NotEmpty(recordRows);
        Assert.Equal(expected.Count, recordRows.Count);
        Assert.Equal(recordRows.Count, eventRows.Count);

        await using var application = await database.CreateAsync(label + "_rows");
        foreach (var row in recordRows)
        {
            await InsertRawAsync(application, RecordsTable, row);
        }

        foreach (var row in eventRows)
        {
            await InsertRawAsync(application, EventsTable, row);
        }

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
            var matching = eventRows.Where(row => IdOf(row, "event_id") == eventId).ToList();
            if (matching.Count != 1)
            {
                problems.Add($"{record.ExperienceId}: {matching.Count} lifecycle events have the derived initial event ID {eventId}, not 1.");
                continue;
            }

            var initial = matching[0];
            if (IdOf(initial, "experience_id") != record.ExperienceId
                || record.ExperienceId != ExperienceFinalizationService.ExperienceIdFor(record.SourceRunId, record.Scope)
                || Text(initial, "current_status") != record.Status.ToString()
                || Text(initial, "prior_status") != nameof(ExperienceStatus.Candidate)
                || Text(initial, "expected_revision") != "0"
                || Text(initial, "applied_revision") != record.Revision.ToString(CultureInfo.InvariantCulture))
            {
                problems.Add($"{record.ExperienceId}: the initial event is not the record's own: {initial.ToJsonString()}");
            }
        }

        // What .NET writes for the same records -- the records the port's own reader reads its rows as, so a column the
        // port writes in its own encoding and reads back symmetrically still differs here -- column for column, over
        // every column of both tables.
        await using var twin = await database.CreateAsync(label + "_dotnet");
        var recordColumns = await ColumnsAsync(twin, RecordsTable);
        var eventColumns = await ColumnsAsync(twin, EventsTable);
        foreach (var stored in expected)
        {
            var record = ContractJson.FromStored(stored!);
            var portRecords = recordRows.Where(row => IdOf(row, "experience_id") == record.ExperienceId).ToList();
            var portEvents = eventRows.Where(row => IdOf(row, "experience_id") == record.ExperienceId).ToList();
            if (portRecords.Count != 1 || portEvents.Count != 1)
            {
                problems.Add($"{record.ExperienceId}: the port wrote {portRecords.Count} record rows and {portEvents.Count} lifecycle events for it, not 1 of each.");
                continue;
            }

            await WriteAsDotNetAsync(twin, record, Text(portEvents[0], "actor"), problems);
            await CompareAsync(RecordsTable, recordColumns, "experience_id", record.ExperienceId, record.Scope, portRecords[0], application, twin, problems);
            await CompareAsync(EventsTable, eventColumns, "event_id", IdOf(portEvents[0], "event_id"), record.Scope, portEvents[0], application, twin, problems);
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
    /// The rows of one table in the rows file. Every value must be a JSON string (PostgreSQL's text form) or null, and
    /// the key column a UUID; anything else is a named problem, never an exception.
    /// </summary>
    private static List<JsonObject> RowsOf(JsonObject rows, string table, string key, List<string> problems)
    {
        if (rows[table] is not JsonArray array)
        {
            problems.Add($"rows file: {table} is not an array.");
            return [];
        }

        var result = new List<JsonObject>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject row)
            {
                problems.Add($"rows file: {table}[{i}] is not an object.");
                continue;
            }

            foreach (var (column, value) in row)
            {
                if (value is not null && value.GetValueKind() != JsonValueKind.String)
                {
                    problems.Add($"rows file: {table}[{i}].{column} is {value.GetValueKind()}, not text or null.");
                }
            }

            if (row[key] is not JsonValue id || !id.TryGetValue<string>(out var text) || !Guid.TryParse(text, out _))
            {
                problems.Add($"rows file: {table}[{i}].{key} is not a UUID.");
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>A validated row's value as text, or <see langword="null"/>.</summary>
    private static string? Text(JsonObject row, string column) => row[column]?.GetValue<string>();

    /// <summary>A validated row's UUID column.</summary>
    private static Guid IdOf(JsonObject row, string column) => Guid.TryParse(Text(row, column), out var id) ? id : Guid.Empty;

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

    /// <summary>
    /// Every column of the table, the port's row against .NET's. A column in the rows file is the port's value as it
    /// wrote it; any other column is what the database left in the port's row. A difference names the table, the row
    /// and the column.
    /// </summary>
    private static async Task CompareAsync(
        string table,
        IReadOnlyList<string> columns,
        string key,
        Guid id,
        Scope scope,
        JsonObject portFile,
        NpgsqlDataSource application,
        NpgsqlDataSource twin,
        List<string> problems)
    {
        foreach (var column in portFile.Select(member => member.Key).Where(column => !columns.Contains(column)))
        {
            problems.Add($"{table} {id}: the rows file names a column {column} the table does not have.");
        }

        var port = await ReadRawAsync(application, table, scope, key, id, columns);
        var dotnet = await ReadRawAsync(twin, table, scope, key, id, columns);
        if (port is null || dotnet is null)
        {
            problems.Add($"{table} {id}: {(port is null ? "the port's row" : ".NET's row")} is missing.");
            return;
        }

        foreach (var column in columns)
        {
            var written = portFile.ContainsKey(column);
            var portText = written ? Text(portFile, column) : port[column];
            var dotnetText = dotnet[column];
            if (!string.Equals(portText, dotnetText, StringComparison.Ordinal))
            {
                problems.Add(written
                    ? $"{table}.{column} of {id}: the port wrote {Show(portText)}, .NET writes {Show(dotnetText)}."
                    : $"{table}.{column} of {id}: the port does not write it (the database left {Show(portText)}), .NET writes {Show(dotnetText)}.");
            }
        }

        static string Show(string? text) => text is null ? "NULL" : $"'{text}'";
    }

    /// <summary>The table's columns, in their order, without dropped or system columns.</summary>
    private static async Task<IReadOnlyList<string>> ColumnsAsync(NpgsqlDataSource source, string table)
    {
        await using var command = source.CreateCommand(
            "SELECT attname FROM pg_attribute WHERE attrelid = ('agent_experience.' || @table)::regclass " +
            "AND attnum > 0 AND NOT attisdropped ORDER BY attnum");
        command.Parameters.Add(new("table", NpgsqlDbType.Text) { Value = table });
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
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
    /// One row, every one of <paramref name="columns"/> in PostgreSQL's text form in UTC (as a port's test reads them),
    /// read as the application role under bounds declared for <paramref name="scope"/>; <see langword="null"/> when
    /// there is no such row.
    /// </summary>
    private static async Task<Dictionary<string, string?>?> ReadRawAsync(
        NpgsqlDataSource application, string table, Scope scope, string key, Guid id, IReadOnlyList<string> columns)
    {
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PrepareAsync(connection, transaction, scope);
        await using var select = new NpgsqlCommand(
            $"SELECT {string.Join(", ", columns.Select(name => $"\"{name}\"::text"))} FROM agent_experience.{table} WHERE {key} = @id",
            connection,
            transaction);
        select.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = id });
        await using var reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var row = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
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
            insert.Parameters.Add(new($"p{i}", NpgsqlDbType.Text) { Value = (object?)Text(row, names[i]) ?? DBNull.Value });
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
