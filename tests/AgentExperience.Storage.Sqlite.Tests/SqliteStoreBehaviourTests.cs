using AgentExperience.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Sqlite.Tests;

/// <summary>
/// Story 28.7: what only a file-backed store can show -- durability across store instances, compare-and-set across two
/// connections on one file, erasure reaching the full-text index, stemming, and an idempotent, journaled migration.
/// </summary>
public sealed class SqliteStoreBehaviourTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task A_record_written_by_one_store_is_read_by_another_over_the_same_file()
    {
        var tenant = NewTenant();
        var record = FullRecord(Scope(tenant));

        var writer = new SqliteExperienceRecordStore(_database.ConnectionString);
        Assert.Equal(ExperienceStoreOutcome.Created, (await writer.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        using (var pooled = new SqliteConnection(writer.Database.ConnectionString))
        {
            SqliteConnection.ClearPool(pooled);
        }

        var reader = new SqliteExperienceRecordStore(_database.ConnectionString);
        var read = await reader.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Equal(Json(record), Json(read.Record));
    }

    [Fact]
    public async Task Commits_racing_from_one_revision_over_two_connections_have_exactly_one_winner()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        var first = new SqliteExperienceRecordStore(_database.ConnectionString);
        var second = new SqliteExperienceRecordStore(_database.ConnectionString);
        await first.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var racers = Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => (i % 2 == 0 ? first : second).CommitLifecycleEventAsync(
                Authorize(tenant),
                record.Scope,
                Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, expectedRevision: 0),
                CancellationToken.None)))
            .ToArray();
        var results = await Task.WhenAll(racers);

        Assert.Single(results, result => result.Outcome == ExperienceStoreOutcome.Committed);
        Assert.All(
            results.Where(result => result.Outcome != ExperienceStoreOutcome.Committed),
            result => Assert.Equal(ExperienceStoreOutcome.StaleRevision, result.Outcome));
        var history = await second.GetFirstHistoryPageAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(1, history.Revision);
        Assert.Single(history.Events);
    }

    [Fact]
    public async Task Erasure_leaves_a_tombstone_and_removes_the_record_s_text_from_the_full_text_index()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var store = new SqliteExperienceRecordStore(_database.ConnectionString);
        var source = new SqliteExperienceCandidateSource(store);
        var record = Record(scope, ExperienceStatus.Validated, summary: "rotate the zanzibar credentials", lesson: "zanzibar keys expire hourly", confidence: 0.9);
        await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var query = new ExperienceCandidateQuery(scope, "zanzibar credentials", [ExperienceStatus.Validated], 0) { MinimumMatchedTerms = 1 };
        Assert.Single((await source.SearchAsync(Authorize(tenant), query, CancellationToken.None)).Candidates);

        var deleted = await store.DeleteAsync(Authorize(tenant), scope, record.ExperienceId, CancellationToken.None);
        var again = await store.DeleteAsync(Authorize(tenant), scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);
        Assert.Equal(1, deleted.Revision);
        Assert.Equal(ExperienceStoreOutcome.Deleted, again.Outcome);
        Assert.Empty((await source.SearchAsync(Authorize(tenant), query, CancellationToken.None)).Candidates);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.GetAsync(Authorize(tenant), scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.NotFound,
            (await store.GetAsync(Authorize(tenant), Scope(tenant, "project-2"), record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Conflict, (await store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await store.CommitLifecycleEventAsync(
                Authorize(tenant),
                scope,
                Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, expectedRevision: 1),
                CancellationToken.None)).Outcome);

        // Nothing of the text is left in the index or the record row.
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT count(*) FROM experience_search WHERE experience_search MATCH 'zanzibar') "
            + "+ (SELECT count(*) FROM experience_records WHERE payload LIKE '%zanzibar%' OR task_id IS NOT NULL)";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Erasing_a_record_in_another_scope_is_NotFound_and_erases_nothing()
    {
        var tenant = NewTenant();
        var store = new SqliteExperienceRecordStore(_database.ConnectionString);
        var record = Record(Scope(tenant));
        await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var deleted = await store.DeleteAsync(Authorize(tenant), Scope(tenant, "project-2"), record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.NotFound, deleted.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_full_text_index_stems_so_a_plural_matches_its_singular()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var store = new SqliteExperienceRecordStore(_database.ConnectionString);
        var record = Record(scope, ExperienceStatus.Validated, summary: "reconcile the invoice totals", confidence: 1);
        await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var found = await new SqliteExperienceCandidateSource(store).SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(scope, "invoices", [ExperienceStatus.Validated], 0),
            CancellationToken.None);

        Assert.Equal(record.ExperienceId, Assert.Single(found.Candidates).Record.ExperienceId);
    }

    [Fact]
    public async Task Migrating_again_applies_nothing_and_the_journal_holds_every_version_once()
    {
        var applied = await ExperienceSqliteSchema.MigrateAsync(_database.Path);
        var journal = await ExperienceSqliteSchema.AppliedVersionsAsync(_database.Path);

        Assert.Equal(0, applied);
        Assert.Equal(Enumerable.Range(1, ExperienceSqliteSchema.CurrentVersion), journal);
    }

    [Fact]
    public async Task Migrating_concurrently_applies_each_script_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentexperience-sqlite-{Guid.NewGuid():N}.db");
        try
        {
            var applied = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => ExperienceSqliteSchema.MigrateAsync(path))));

            Assert.Equal(ExperienceSqliteSchema.CurrentVersion, applied.Sum());
            Assert.Equal(Enumerable.Range(1, ExperienceSqliteSchema.CurrentVersion), await ExperienceSqliteSchema.AppliedVersionsAsync(path));
        }
        finally
        {
            SqliteTestDatabase.Delete(path);
        }
    }

    [Fact]
    public async Task The_file_is_in_write_ahead_log_mode_after_migration()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";

        Assert.Equal("wal", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task An_unmigrated_database_fails_with_a_store_exception_that_carries_no_content()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentexperience-sqlite-{Guid.NewGuid():N}.db");
        try
        {
            var tenant = NewTenant();
            var store = new SqliteExperienceRecordStore(ExperienceSqliteSchema.ConnectionStringFor(path));
            var record = Record(Scope(tenant), summary: "secret summary text");

            var thrown = await Assert.ThrowsAsync<ExperienceStoreException>(() => store.CreateAsync(Authorize(tenant), record, CancellationToken.None));

            Assert.DoesNotContain("secret", thrown.Message, StringComparison.Ordinal);
            Assert.IsType<SqliteException>(thrown.InnerException);
        }
        finally
        {
            SqliteTestDatabase.Delete(path);
        }
    }

    [Fact]
    public void A_connection_string_naming_no_file_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new SqliteExperienceRecordStore("Mode=Memory"));
        Assert.Throws<ArgumentException>(() => new SqliteExperienceRecordStore(" "));
    }
}
