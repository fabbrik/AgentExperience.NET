using AgentExperience.Abstractions;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// One test per row of the migrator's edge-case matrix, each on its own freshly created database in the
/// shared PostgreSQL 16 container, so a migration in one test can never be seen by another.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExperienceSchemaMigratorTests
{
    /// <summary>Test-only scripts embedded in this assembly: <c>0001_marker.sql</c> then a failing <c>0002_broken.sql</c>.</summary>
    private const string FailingPrefix = "AgentExperience.Storage.Postgres.Tests.FailingMigrations.";

    /// <summary>Test-only scripts embedded in this assembly: <c>0001_marker.sql</c> then a <c>$body$</c>-quoted <c>0002_dollar_quoted.sql</c>.</summary>
    private const string ExtraPrefix = "AgentExperience.Storage.Postgres.Tests.ExtraMigrations.";

    private const string ShippedPrefix = "AgentExperience.Storage.Postgres.Migrations.";

    private readonly PostgresFixture _fixture;

    public ExperienceSchemaMigratorTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Fresh_database_applies_and_journals_the_initial_script_and_the_store_round_trips()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("fresh");

        var result = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, result.AppliedScripts);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, await JournaledAsync(dataSource, ShippedPrefix));

        var store = new PostgresExperienceRecordStore(dataSource);
        var tenant = NewTenant();
        var record = Full(Scope(tenant));

        var created = await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var read = await store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Equal(Canonical(record), Canonical(read.Record!));
    }

    [Fact]
    public async Task Rerun_on_a_migrated_database_applies_nothing_and_leaves_rows_intact()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("rerun");
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        var store = new PostgresExperienceRecordStore(dataSource);
        var tenant = NewTenant();
        var record = Full(Scope(tenant));
        await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var appliedAt = await JournalAppliedAtAsync(dataSource);

        var rerun = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        Assert.Empty(rerun.AppliedScripts);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, await JournaledAsync(dataSource, ShippedPrefix));
        Assert.Equal(appliedAt, await JournalAppliedAtAsync(dataSource));

        var read = await store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Equal(Canonical(record), Canonical(read.Record!));
    }

    [Fact]
    public async Task Database_whose_initial_script_was_applied_by_hand_is_journaled_without_losing_rows()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("manual");

        // The pre-migrator way a host applied the schema: run the script text, no journal table.
        await using (var command = dataSource.CreateCommand(
            PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.InitialScriptName)))
        {
            await command.ExecuteNonQueryAsync();
        }

        var store = new PostgresExperienceRecordStore(dataSource);
        var tenant = NewTenant();
        var record = Full(Scope(tenant));
        await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        // 0001 is idempotent, so re-running it over the hand-applied schema is safe.
        var result = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, result.AppliedScripts);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, await JournaledAsync(dataSource, ShippedPrefix));

        var read = await store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Equal(Canonical(record), Canonical(read.Record!));
    }

    [Fact]
    public async Task Upgrading_a_database_that_already_holds_a_Superseded_event_with_no_replacement_succeeds()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("upgrade_0006");

        // A pre-0006 database: 0001-0005 only. The public port has always accepted a Superseded event,
        // because Core's transition table was never applied by the store, and such an event has no
        // replacement -- exactly the row a validating ADD CONSTRAINT would abort this script on.
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames
            .Where(name => !string.Equals(name, PostgresExperienceRecordSchema.SupersessionAndAppendOnlyScriptName, StringComparison.Ordinal)))
        {
            await using var command = dataSource.CreateCommand(PostgresExperienceRecordSchema.GetScript(scriptName));
            await command.ExecuteNonQueryAsync();
        }

        var store = new PostgresExperienceRecordStore(dataSource);
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = Minimal(scope);
        await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        await using (var legacy = dataSource.CreateCommand(
            "INSERT INTO agent_experience.lifecycle_events (event_id, experience_id, tenant_id, application_id, " +
            "project_id, prior_status, current_status, reason, producer, occurred_at, recorded_at, " +
            "expected_revision, applied_revision) VALUES " +
            "(gen_random_uuid(), @id, @tenant, @app, @project, 'Candidate', 'Validated', 'promoted', 'legacy', now(), now(), 0, 1), " +
            "(gen_random_uuid(), @id, @tenant, @app, @project, 'Validated', 'Superseded', 'replaced', 'legacy', now(), now(), 1, 2)"))
        {
            legacy.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            legacy.Parameters.Add(new NpgsqlParameter<string>("tenant", tenant));
            legacy.Parameters.Add(new NpgsqlParameter<string>("app", scope.ApplicationId));
            legacy.Parameters.Add(new NpgsqlParameter<string>("project", scope.ProjectId));
            Assert.Equal(2, await legacy.ExecuteNonQueryAsync());
        }

        // 0006 adds its CHECKs NOT VALID, so it does not scan those rows and the upgrade completes.
        var result = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, result.AppliedScripts);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, await JournaledAsync(dataSource, ShippedPrefix));

        // The legacy rows are intact and still readable, replacement column and all.
        var history = await store.GetFirstHistoryPageAsync(Authorize(tenant), scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(2, history.Events.Count);
        Assert.Equal(ExperienceStatus.Superseded, history.Events[^1].Event.CurrentStatus);
        Assert.Null(history.Events[^1].Event.ReplacementExperienceId);

        // NOT VALID still binds every new row, which is the whole point of deferring the scan.
        await using var offending = dataSource.CreateCommand(
            "INSERT INTO agent_experience.lifecycle_events (event_id, experience_id, tenant_id, application_id, " +
            "project_id, prior_status, current_status, reason, producer, occurred_at, recorded_at, " +
            "expected_revision, applied_revision) VALUES " +
            "(gen_random_uuid(), gen_random_uuid(), 'tenant', 'app', 'proj', 'Validated', 'Superseded', 'r', 'p', now(), now(), 0, 1)");
        var refused = await Assert.ThrowsAsync<PostgresException>(() => offending.ExecuteNonQueryAsync());
        Assert.Equal("lifecycle_events_replacement_only_when_superseded", refused.ConstraintName);

        // And the documented VALIDATE step fails loudly while the legacy row is still there, which is
        // what makes "reconcile, then validate" an instruction rather than a suggestion.
        await using var validate = dataSource.CreateCommand(
            "ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_replacement_only_when_superseded");
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => validate.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Search_script_applied_by_hand_first_is_journaled_without_failing_on_the_existing_column()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("search_manual");

        // The whole shipped schema applied the pre-migrator way, 0003 included, so the generated column
        // and both indexes already exist when the runner re-runs the script over them.
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames)
        {
            await using var command = dataSource.CreateCommand(PostgresExperienceRecordSchema.GetScript(scriptName));
            await command.ExecuteNonQueryAsync();
        }

        var store = new PostgresExperienceRecordStore(dataSource);
        var tenant = NewTenant();
        var record = Full(Scope(tenant));
        await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        // Every statement in 0003 is IF NOT EXISTS, so this is a no-op rather than a duplicate-column error.
        var result = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, result.AppliedScripts);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, await JournaledAsync(dataSource, ShippedPrefix));

        // And the hand-applied column still indexes the row that was written through it.
        var search = new PostgresExperienceCandidateSource(dataSource);
        var found = await search.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(record.Scope, "refund ticket", [ExperienceStatus.Validated], 0d),
            CancellationToken.None);
        Assert.Equal(record.ExperienceId, Assert.Single(found.Candidates).Record.ExperienceId);
    }

    [Fact]
    public async Task A_record_written_before_the_search_script_is_indexed_when_it_is_applied()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("search_backfill");

        // The state an existing deployment is in: 0001 and 0002 applied, rows written, 0003 not yet run.
        foreach (var scriptName in new[]
        {
            PostgresExperienceRecordSchema.InitialScriptName,
            PostgresExperienceRecordSchema.LifecycleEventsScriptName,
        })
        {
            await using var command = dataSource.CreateCommand(PostgresExperienceRecordSchema.GetScript(scriptName));
            await command.ExecuteNonQueryAsync();
        }

        var store = new PostgresExperienceRecordStore(dataSource);
        var tenant = NewTenant();
        var record = Full(Scope(tenant));
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        // The generated column is computed for every existing row as the table is rewritten, so records
        // that predate the search are searchable without a backfill step of their own.
        var search = new PostgresExperienceCandidateSource(dataSource);
        var found = await search.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(record.Scope, "refund ticket", [ExperienceStatus.Validated], 0d),
            CancellationToken.None);

        var candidate = Assert.Single(found.Candidates);
        Assert.Equal(record.ExperienceId, candidate.Record.ExperienceId);
        Assert.Equal(Canonical(record), Canonical(candidate.Record));
    }

    [Fact]
    public async Task Concurrent_runs_both_succeed_and_journal_each_script_exactly_once()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("concurrent");

        // Hold the migrator's lock from outside so the first run is provably blocked, then start the second
        // run and wait until it is blocked too. Both are in flight, contending, before either can proceed.
        await using var holder = await dataSource.OpenConnectionAsync();
        await AdvisoryLockAsync(holder, "pg_advisory_lock");

        var first = ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        await WaitForAdvisoryLockWaiterAsync(dataSource, expected: 1);
        var second = ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        await WaitForAdvisoryLockWaiterAsync(dataSource, expected: 2);

        await AdvisoryLockAsync(holder, "pg_advisory_unlock");
        var results = await Task.WhenAll(first, second);

        // Serialized by the advisory lock: one run applies every script, the other finds the journal current.
        var applied = results.Select(r => r.AppliedScripts).OrderBy(names => names.Count).ToList();
        Assert.Empty(applied[0]);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, applied[1]);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, await JournaledAsync(dataSource, ShippedPrefix));
        Assert.Equal(0, await AdvisoryLockCountAsync(dataSource));
    }

    [Fact]
    public async Task Second_script_applies_on_top_of_an_already_journaled_one_and_keeps_its_dollar_quoting()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("secondscript");

        // A prefix that reaches only 0001 leaves 0002 pending while 0001 is journaled under its full
        // resource name, which is what the next run matches on.
        var firstRun = await ExperienceSchemaMigrator.MigrateAsync(
            dataSource, typeof(ExperienceSchemaMigratorTests).Assembly, ExtraPrefix + "0001", CancellationToken.None);

        Assert.Equal(["_marker.sql"], firstRun.AppliedScripts);
        Assert.Equal(["0001_marker.sql"], await JournaledAsync(dataSource, ExtraPrefix));

        var secondRun = await ExperienceSchemaMigrator.MigrateAsync(
            dataSource, typeof(ExperienceSchemaMigratorTests).Assembly, ExtraPrefix, CancellationToken.None);

        // Only the pending script runs, and its $body$ block reached PostgreSQL unsubstituted.
        Assert.Equal(["0002_dollar_quoted.sql"], secondRun.AppliedScripts);
        Assert.Equal(["0001_marker.sql", "0002_dollar_quoted.sql"], await JournaledAsync(dataSource, ExtraPrefix));
        Assert.True(await TableExistsAsync(dataSource, "extra_dollar_quoted"));
    }

    [Fact]
    public async Task Failing_script_throws_naming_the_script_keeps_earlier_scripts_and_releases_the_lock()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("failing");

        var ex = await Assert.ThrowsAsync<ExperienceStoreException>(() => ExperienceSchemaMigrator.MigrateAsync(
            dataSource, typeof(ExperienceSchemaMigratorTests).Assembly, FailingPrefix, CancellationToken.None));

        Assert.Contains("0002_broken.sql", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("table_that_does_not_exist", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);

        // The script before the failure stays applied and journaled; the failing one rolled back.
        Assert.Equal(["0001_marker.sql"], await JournaledAsync(dataSource, FailingPrefix));
        Assert.True(await TableExistsAsync(dataSource, "migration_marker"));
        Assert.Equal(0, await AdvisoryLockCountAsync(dataSource));

        // Lock released, so the next run gets in and does its work.
        var recovered = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, recovered.AppliedScripts);
    }

    [Fact]
    public async Task Token_cancelled_before_the_call_throws_OperationCanceledException_unwrapped()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("precancelled");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExperienceSchemaMigrator.MigrateAsync(dataSource, cts.Token));

        Assert.IsNotType<ExperienceStoreException>(ex);
        Assert.False(await SchemaExistsAsync(dataSource));
    }

    [Fact]
    public async Task Token_cancelled_while_waiting_for_the_lock_throws_OperationCanceledException_and_leaves_no_lock()
    {
        await using var dataSource = await _fixture.CreateDatabaseAsync("cancelwait");

        // Hold the migrator's advisory lock from outside, so the call blocks on the wait.
        await using var holder = await dataSource.OpenConnectionAsync();
        await AdvisoryLockAsync(holder, "pg_advisory_lock");

        using var cts = new CancellationTokenSource();
        var migrating = ExperienceSchemaMigrator.MigrateAsync(dataSource, cts.Token);
        await WaitForAdvisoryLockWaiterAsync(dataSource, expected: 1);
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migrating);
        Assert.IsNotType<ExperienceStoreException>(ex);
        Assert.False(await SchemaExistsAsync(dataSource));

        await AdvisoryLockAsync(holder, "pg_advisory_unlock");

        // The cancelled call left nothing held, so a later run takes the lock and completes.
        Assert.Equal(0, await AdvisoryLockCountAsync(dataSource));
        var result = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, result.AppliedScripts);
    }

    [Fact]
    public async Task Null_data_source_throws_ArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExperienceSchemaMigrator.MigrateAsync(null!, CancellationToken.None));
    }

    private static async Task<IReadOnlyList<string>> JournaledAsync(NpgsqlDataSource dataSource, string resourcePrefix)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT scriptname FROM agent_experience.schema_versions ORDER BY scriptname");
        await using var reader = await command.ExecuteReaderAsync();

        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            Assert.StartsWith(resourcePrefix, name, StringComparison.Ordinal);
            names.Add(name[resourcePrefix.Length..]);
        }

        return names;
    }

    private static async Task<IReadOnlyList<DateTime>> JournalAppliedAtAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT applied FROM agent_experience.schema_versions ORDER BY scriptname");
        await using var reader = await command.ExecuteReaderAsync();

        var applied = new List<DateTime>();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetDateTime(0));
        }

        return applied;
    }

    private static async Task<int> AdvisoryLockCountAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' " +
            "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> SchemaExistsAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT to_regnamespace('agent_experience') IS NOT NULL");
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlDataSource dataSource, string tableName)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT to_regclass('agent_experience.' || @table) IS NOT NULL");
        command.Parameters.Add(new NpgsqlParameter<string>("table", tableName));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Takes or releases the migrator's advisory lock from outside, on a caller-owned connection.</summary>
    private static async Task AdvisoryLockAsync(NpgsqlConnection connection, string function)
    {
        await using var command = new NpgsqlCommand($"SELECT {function}(@key)", connection);
        command.Parameters.Add(new NpgsqlParameter<long>("key", ExperienceSchemaMigrator.AdvisoryLockKey));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Waits until <paramref name="expected"/> connections are actually blocked on the advisory lock.</summary>
    private static async Task WaitForAdvisoryLockWaiterAsync(NpgsqlDataSource dataSource, int expected)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted " +
                "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");
            if ((long)(await command.ExecuteScalarAsync())! >= expected)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Fewer than {expected} migration runs blocked on the advisory lock.");
    }
}
