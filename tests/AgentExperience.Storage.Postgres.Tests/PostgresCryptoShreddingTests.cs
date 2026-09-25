using System.Text;
using AgentExperience.Core.KeyManagement;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 6.4, KL-2: crypto-shredding against a real PostgreSQL. Every test here builds its own
/// <see cref="ExperienceEncryption"/> over its own key store, so it proves the same thing whichever mode the
/// rest of the suite runs in.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCryptoShreddingTests
{
    /// <summary>A phrase that lives only in an attempt's result -- a field no search vector indexes.</summary>
    private const string UnindexedMarker = "marsupial-ledger-8841";

    /// <summary>A word in the lesson -- indexed, so its stem is the residual the README names.</summary>
    private const string IndexedWord = "quokka";

    private readonly PostgresFixture _fixture;

    public PostgresCryptoShreddingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------ what is stored

    [Fact]
    public async Task A_sealed_record_reads_back_whole_and_stores_none_of_its_text_in_the_clear()
    {
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var plaintext = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var sealedRecord = Marked(Full(Scope(tenant)));
        var plainRecord = sealedRecord with { ExperienceId = Guid.NewGuid() };

        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, sealedRecord, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Created, (await plaintext.CreateAsync(auth, plainRecord, CancellationToken.None)).Outcome);

        // Read back exactly as a plaintext record of the same content reads back.
        var read = (await store.GetAsync(auth, sealedRecord.Scope, sealedRecord.ExperienceId, CancellationToken.None)).Record!;
        var readPlain = (await store.GetAsync(auth, plainRecord.Scope, plainRecord.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(Canonical(readPlain with { ExperienceId = read.ExperienceId }), Canonical(read));
        Assert.Equal(sealedRecord.TaskId, read.TaskId);

        // The row: the sealed shape, and not one of the record's texts anywhere in it.
        var row = await RowTextAsync(sealedRecord.ExperienceId);
        Assert.Equal(2, await ScalarAsync<int>("SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = @id", sealedRecord.ExperienceId));
        Assert.Equal("(sealed)", await ScalarAsync<string>("SELECT task_id FROM agent_experience.experience_records WHERE experience_id = @id", sealedRecord.ExperienceId));
        foreach (var text in new[] { UnindexedMarker, sealedRecord.TaskSummary!, sealedRecord.Reflection!.Lesson, "us-east", "worker-01" })
        {
            Assert.DoesNotContain(text, row, StringComparison.Ordinal);
        }

        // The exact residual: the sealed search vector holds the task ID, summary and lesson as lexemes with
        // positions -- an identifier-like task ID survives whole as one lexeme, words as their stems.
        Assert.Contains($"'{IndexedWord}':", row, StringComparison.Ordinal);
        Assert.Contains($"'{sealedRecord.TaskId}':", row, StringComparison.Ordinal);
        Assert.Contains("'resolv':", row, StringComparison.Ordinal);

        // ...and the plaintext twin, for contrast, holds all of it.
        var plainRow = await RowTextAsync(plainRecord.ExperienceId);
        Assert.Contains(UnindexedMarker, plainRow, StringComparison.Ordinal);

        // The sealed vector is exactly the twin's generated one -- same lexemes, same positions -- so the two rank
        // alike; and erasure clears it from the live row.
        Assert.True(await ScalarAsync<bool>(
            "SELECT s.search_vector_sealed = p.search_vector FROM agent_experience.experience_records s, agent_experience.experience_records p " +
            "WHERE s.experience_id = @id AND p.experience_id = @twin",
            sealedRecord.ExperienceId,
            ("twin", plainRecord.ExperienceId)));
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, sealedRecord.Scope, sealedRecord.ExperienceId, CancellationToken.None)).Outcome);
        Assert.True(await ScalarAsync<bool>(
            "SELECT search_vector_sealed IS NULL FROM agent_experience.experience_records WHERE experience_id = @id", sealedRecord.ExperienceId));
        Assert.DoesNotContain($"'{IndexedWord}':", await RowTextAsync(sealedRecord.ExperienceId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sealed_rows_placeholder_is_never_what_a_search_matches()
    {
        // The generated search_vector of a sealed row holds the placeholder's lexeme; a search for it finds nothing.
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Marked(Minimal(Scope(tenant), status: ExperienceStatus.Validated)) with { ReuseConfidence = 0.5 };
        await store.CreateAsync(auth, record, CancellationToken.None);
        var search = new PostgresExperienceCandidateSource(_fixture.DataSource, encryption: keys.Encryption);
        Assert.Single((await search.SearchAsync(auth, Query(record.Scope), CancellationToken.None)).Candidates);

        Assert.Empty((await search.SearchAsync(auth, new ExperienceCandidateQuery(record.Scope, "sealed", [ExperienceStatus.Validated], 0), CancellationToken.None)).Candidates);
        Assert.True(await ScalarAsync<bool>(
            "SELECT search_vector @@ to_tsquery('english', 'sealed') FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));
    }

    [Fact]
    public async Task The_sealed_shape_checks_refuse_a_malformed_row_even_from_the_owner()
    {
        var store = new Keys().Store(_fixture.DataSource);
        var plaintext = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var sealedRecord = Minimal(Scope(tenant));
        var plainRecord = Minimal(Scope(tenant));
        await store.CreateAsync(auth, sealedRecord, CancellationToken.None);
        await plaintext.CreateAsync(auth, plainRecord, CancellationToken.None);

        foreach (var (sql, id) in new[]
        {
            ("UPDATE agent_experience.experience_records SET task_id = 'in-the-clear' WHERE experience_id = @id", sealedRecord.ExperienceId),
            ("UPDATE agent_experience.experience_records SET payload = '{\"sealed\": \"not-a-seal\"}' WHERE experience_id = @id", sealedRecord.ExperienceId),
            ("UPDATE agent_experience.experience_records SET payload = payload || '{\"extra\": 1}' WHERE experience_id = @id", sealedRecord.ExperienceId),
            ("UPDATE agent_experience.experience_records SET payload = '{}' WHERE experience_id = @id", sealedRecord.ExperienceId),
            ("UPDATE agent_experience.experience_records SET search_vector_sealed = NULL WHERE experience_id = @id", sealedRecord.ExperienceId),
            ("UPDATE agent_experience.experience_records SET search_vector_sealed = to_tsvector('english', 'x') WHERE experience_id = @id", plainRecord.ExperienceId),
            ("INSERT INTO agent_experience.reuse_feedback_exposures (feedback_id, experience_id, ordinal, attributed, evidence_id, rationale_sealed) " +
                "VALUES (gen_random_uuid(), @id, 99, false, NULL, 'in the clear')", plainRecord.ExperienceId),
        })
        {
            await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
            var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        }
    }

    // ------------------------------------------------------------------ the property itself

    [Fact]
    public async Task After_erasure_neither_a_pre_erasure_dump_nor_the_dead_heap_tuple_can_be_opened_with_anything_still_reachable()
    {
        await using var owner = await IsolatedDatabaseAsync("shreddump");
        var keys = new Keys();
        var store = keys.Store(owner.DataSource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Marked(Minimal(Scope(tenant)));
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);

        var sealedValue = await ScalarAsync<string>(
            owner.DataSource, "SELECT payload ->> 'sealed' FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId);
        Assert.StartsWith(SealedText.Prefix, sealedValue, StringComparison.Ordinal);

        // A backup taken before the erasure: a real pg_dump of the table. It holds the ciphertext, never the text.
        var dump = await _fixture.DumpAsync(owner.Database, "agent_experience.experience_records");
        Assert.Contains(sealedValue, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(UnindexedMarker, dump, StringComparison.Ordinal);

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);

        // The live row is a tombstone; the dead tuple the UPDATE left behind still physically holds the old
        // ciphertext -- read straight off the heap pages, before any VACUUM.
        var heap = await RawHeapAsync(owner.Database);
        Assert.Contains(sealedValue, heap, StringComparison.Ordinal);
        Assert.DoesNotContain(UnindexedMarker, heap, StringComparison.Ordinal);

        // Both copies are the same ciphertext, and nothing still reachable opens it: the record's own key is
        // destroyed, and no other key the store holds -- tried under this record's associated data -- opens it.
        Assert.Equal(ExperienceKeyLookup.Destroyed, await keys.KeyStore.GetKeyAsync(new(record.ExperienceId, record.Scope), CancellationToken.None));
        Assert.Equal(ExperienceKeyLookup.Destroyed, await keys.KeyStore.CreateKeyAsync(new(record.ExperienceId, record.Scope), CancellationToken.None));
        var survivors = await keys.Repository.ListNotWrappedUnderAsync("no-such-kek", 10_000, CancellationToken.None);
        var associatedData = SealedText.AssociatedData(SealedText.PayloadColumn, new(record.ExperienceId, record.Scope), Guid.Empty);
        foreach (var survivor in survivors)
        {
            using var key = (await keys.KeyStore.GetKeyAsync(survivor.Reference, CancellationToken.None)).Key!;
            Assert.Throws<ExperienceStoreException>(() => SealedText.Open(key.Span, associatedData, sealedValue));
        }
    }

    [Fact]
    public async Task In_plaintext_mode_the_dead_heap_tuple_and_a_pre_erasure_dump_still_hold_the_erased_text()
    {
        // The residual plaintext mode keeps, stated as a test so the README's claim about it is checked too.
        await using var owner = await IsolatedDatabaseAsync("plaindump");
        var store = new PostgresExperienceRecordStore(owner.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Marked(Minimal(Scope(tenant)));
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);

        var dump = await _fixture.DumpAsync(owner.Database, "agent_experience.experience_records");
        Assert.Contains(UnindexedMarker, dump, StringComparison.Ordinal);

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Contains(UnindexedMarker, await RawHeapAsync(owner.Database), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the consistency story

    [Fact]
    public async Task A_key_store_that_cannot_destroy_leaves_the_record_live_readable_and_keyed()
    {
        var keys = new Keys();
        var faulty = new FaultyKeyStore(keys.KeyStore) { FailBeforeDestroy = true };
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: new ExperienceEncryption(faulty));
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await store.CreateAsync(auth, record, CancellationToken.None);

        var failed = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => store.DeleteAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None));
        Assert.IsType<InvalidOperationException>(failed.InnerException);

        // Nothing erased: not a tombstone, still readable, and its key still alive.
        Assert.True(await ScalarAsync<bool>("SELECT deleted_at IS NULL FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));
        Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceKeyStatus.Active, (await keys.KeyStore.GetKeyAsync(new(record.ExperienceId, record.Scope), CancellationToken.None)).Status);

        // A sweep meeting the same failure keeps its count and stops, rather than reporting a record erased.
        var sweep = await Assert.ThrowsAsync<ExperienceRetentionSweepInterruptedException>(
            () => store.SweepExpiredAsync(auth, record.Scope, TimeSpan.FromSeconds(1), 10, CancellationToken.None));
        Assert.Equal(0, sweep.Partial.DeletedCount);

        // Healed, the sweep erases through the same path and destroys the key.
        faulty.FailBeforeDestroy = false;
        Assert.Equal(1, (await store.SweepExpiredAsync(auth, record.Scope, TimeSpan.FromSeconds(1), 10, CancellationToken.None)).DeletedCount);
        Assert.Equal(ExperienceKeyLookup.Destroyed, await keys.KeyStore.GetKeyAsync(new(record.ExperienceId, record.Scope), CancellationToken.None));
    }

    [Fact]
    public async Task A_process_without_encryption_cannot_erase_a_sealed_record_and_leave_its_key_behind()
    {
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var plaintext = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await store.CreateAsync(auth, record, CancellationToken.None);

        // 0016's guard refuses the tombstone: the transaction did not say it destroys the key.
        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => plaintext.DeleteAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
        await Assert.ThrowsAsync<ExperienceRetentionSweepInterruptedException>(
            () => plaintext.SweepExpiredAsync(auth, record.Scope, TimeSpan.FromSeconds(1), 10, CancellationToken.None));
        Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceKeyStatus.Active, (await keys.KeyStore.GetKeyAsync(new(record.ExperienceId, record.Scope), CancellationToken.None)).Status);

        // A tombstone made by hand with the marker set (by the owner, around the library) still has its key; the
        // next encrypted delete finds it already a tombstone and destroys the key anyway.
        await using (var connection = await _fixture.OwnerDataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var marker = new NpgsqlCommand("SET LOCAL agent_experience.erasure_destroys_key = 'on'", connection, transaction))
            {
                await marker.ExecuteNonQueryAsync();
            }

            await using (var purge = new NpgsqlCommand(
                "SELECT purge_outcome FROM agent_experience.purge_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())",
                connection,
                transaction))
            {
                purge.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
                purge.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
                Assert.Equal("Deleted", (string?)await purge.ExecuteScalarAsync());
            }

            await transaction.CommitAsync();
        }

        Assert.Equal(ExperienceKeyStatus.Active, (await keys.KeyStore.GetKeyAsync(new(record.ExperienceId, record.Scope), CancellationToken.None)).Status);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceKeyLookup.Destroyed, await keys.KeyStore.GetKeyAsync(new(record.ExperienceId, record.Scope), CancellationToken.None));
    }

    [Fact]
    public async Task Text_in_the_sealed_format_is_refused_on_write_in_both_modes()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        foreach (var store in new[] { new Keys().Store(_fixture.DataSource), new PostgresExperienceRecordStore(_fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext) })
        {
            var record = Minimal(scope);
            await store.CreateAsync(auth, record, CancellationToken.None);
            var commit = await store.CommitLifecycleEventAsync(
                auth, scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0, reason: SealedText.Prefix + "AAAA"), CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Invalid, commit.Outcome);
            Assert.Contains(commit.Errors, error => error.Path == "Reason");
        }

        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        var grant = await grants.CreateAsync(
            auth, new GrantAdministration("admin", ColumnTime), GrantRequest(Guid.NewGuid(), scope) with { Reason = SealedText.Prefix + "x" }, CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Invalid, grant.Outcome);

        var ledger = new PostgresExperienceReuseFeedbackStore(_fixture.DataSource);
        foreach (var rationale in new[] { SealedText.Prefix + "x", "(sealed)" })
        {
            var feedback = await ledger.RecordAsync(auth, HumanFeedback(scope, [Guid.NewGuid()], rationale), CancellationToken.None);
            Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Invalid, feedback.Outcome);
            Assert.Contains(feedback.Errors, error => error.Path == "Rationale");
        }
    }

    [Fact]
    public async Task The_upgrade_job_finishes_a_delete_that_destroyed_a_plaintext_records_key_and_did_not_commit()
    {
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var plaintext = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await plaintext.CreateAsync(auth, record, CancellationToken.None);
        await keys.KeyStore.DestroyKeyAsync(new(record.ExperienceId, record.Scope), CancellationToken.None);

        var result = await store.SealPlaintextRecordsAsync(auth, record.Scope, 10, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal((0, false), (result.SealedCount, result.MoreRemain));
        Assert.True(await ScalarAsync<bool>(
            "SELECT deleted_at IS NOT NULL FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_destroyed_key_whose_tombstone_never_committed_reads_as_erased_everywhere_until_a_retry_completes_it()
    {
        var keys = new Keys();
        var faulty = new FaultyKeyStore(keys.KeyStore) { FailAfterDestroy = true };
        var encryption = new ExperienceEncryption(faulty);
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: encryption);
        var search = new PostgresExperienceCandidateSource(_fixture.DataSource, encryption: encryption);
        var grants = new PostgresExperienceGrantStore(_fixture.DataSource, encryption: encryption);
        var ledger = new PostgresExperienceReuseFeedbackStore(_fixture.DataSource, encryption: encryption);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = Marked(Minimal(scope, status: ExperienceStatus.Validated)) with { ReuseConfidence = 0.5 };
        await store.CreateAsync(auth, record, CancellationToken.None);
        Assert.Single((await search.SearchAsync(auth, Query(scope), CancellationToken.None)).Candidates);
        var administration = new GrantAdministration("admin", ColumnTime);
        var request = GrantRequest(record.ExperienceId, scope);
        var grant = (await grants.CreateAsync(auth, administration, request, CancellationToken.None)).Grant!;
        Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(auth, request.RecipientScope, record.ExperienceId, CancellationToken.None)).Outcome);

        // The key is destroyed and then the erasure fails before its commit: the crash window.
        await Assert.ThrowsAsync<ExperienceStoreException>(() => store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None));
        faulty.FailAfterDestroy = false;
        Assert.True(await ScalarAsync<bool>("SELECT deleted_at IS NULL FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));

        // It never looks live again: every read answers as for a tombstone...
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
        var many = await store.GetManyAsync(auth, scope, [record.ExperienceId], new ExperienceReadOptions(), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, Assert.Single(many.Results).Outcome);
        Assert.Empty((await store.QueryAsync(auth, new ExperienceRecordQuery(scope), CancellationToken.None)).Records);
        Assert.Empty((await search.SearchAsync(auth, Query(scope), CancellationToken.None)).Candidates);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);

        // Through its still-live grant it is simply not there -- not even a tombstone -- and the grant store
        // answers for its grants exactly as it would for a tombstone's.
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await store.GetAsync(auth, request.RecipientScope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceGrantOutcome.NotFound, (await grants.ListAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceGrantOutcome.NotFound, (await grants.GetHistoryAsync(auth, scope, grant.GrantId, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceGrantOutcome.NotFound,
            (await grants.RevokeAsync(auth, administration, new ExperienceGrantRevocation(grant.GrantId, scope, "too late"), CancellationToken.None)).Outcome);

        // ...and every write is refused, because the key store will not mint a fresh key for it.
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await store.CommitLifecycleEventAsync(auth, scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Conflict, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceGrantOutcome.NotFound,
            (await grants.CreateAsync(auth, new GrantAdministration("admin", ColumnTime), GrantRequest(record.ExperienceId, scope), CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceReuseFeedbackStoreOutcome.Invalid,
            (await ledger.RecordAsync(auth, HumanFeedback(scope, [record.ExperienceId], "it applied"), CancellationToken.None)).Outcome);

        // A retry completes the tombstone.
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.True(await ScalarAsync<bool>("SELECT deleted_at IS NOT NULL FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));
    }

    [Fact]
    public async Task A_refused_erasure_destroys_no_key()
    {
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await store.CreateAsync(auth, record, CancellationToken.None);
        var reference = new ExperienceKeyReference(record.ExperienceId, record.Scope);

        Assert.Equal(ExperienceStoreOutcome.StaleRevision, (await store.DeleteAsync(auth, record.Scope, record.ExperienceId, expectedRevision: 7, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await store.DeleteAsync(auth, Scope(tenant, team: "other"), record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceKeyStatus.Active, (await keys.KeyStore.GetKeyAsync(reference, CancellationToken.None)).Status);

        // A role the host did not give EXECUTE on the purge: the database refuses before any key is touched.
        var role = await _fixture.CreateLoginRoleAsync("shrednoexec");
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            _fixture.OwnerDataSource, new ExperienceApplicationRoleOptions(role), CancellationToken.None);
        await using var source = NpgsqlDataSource.Create(_fixture.ConnectionString(PostgresFixture.StoreDatabase, role));
        var unprivileged = keys.Store(source);
        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => unprivileged.DeleteAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
        Assert.Equal(ExperienceKeyStatus.Active, (await keys.KeyStore.GetKeyAsync(reference, CancellationToken.None)).Status);
        Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_sealed_record_whose_key_the_key_store_never_held_is_a_configuration_failure_not_an_erasure()
    {
        var store = new Keys().Store(_fixture.DataSource);
        var wrongKeyStore = new Keys().Store(_fixture.DataSource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await store.CreateAsync(auth, record, CancellationToken.None);

        await Assert.ThrowsAsync<ExperienceStoreException>(() => wrongKeyStore.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None));
        await Assert.ThrowsAsync<ExperienceStoreException>(() => wrongKeyStore.QueryAsync(auth, new ExperienceRecordQuery(record.Scope), CancellationToken.None));
    }

    [Fact]
    public async Task A_plaintext_mode_component_meeting_a_sealed_row_fails_loudly_instead_of_misreading_it()
    {
        var store = new Keys().Store(_fixture.DataSource);
        var plaintext = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await store.CreateAsync(auth, record, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ExperienceStoreException>(() => plaintext.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None));
        Assert.Contains("ExperienceEncryption", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ authentication

    [Fact]
    public async Task Tampered_ciphertext_is_rejected_by_its_authentication_tag_on_every_read()
    {
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await store.CreateAsync(auth, record, CancellationToken.None);
        await store.CommitLifecycleEventAsync(auth, record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);

        // One flipped bit in the payload's ciphertext.
        var stored = await ScalarAsync<string>("SELECT payload ->> 'sealed' FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId);
        await OwnerExecuteAsync(
            "UPDATE agent_experience.experience_records SET payload = jsonb_build_object('sealed', @value::text) WHERE experience_id = @id",
            record.ExperienceId,
            ("value", FlipOneBit(stored)));

        var get = await Assert.ThrowsAsync<ExperienceStoreException>(() => store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None));
        Assert.Contains("failed authentication", get.Message, StringComparison.Ordinal);
        Assert.Null(get.InnerException);
        await Assert.ThrowsAsync<ExperienceStoreException>(() => store.QueryAsync(auth, new ExperienceRecordQuery(record.Scope), CancellationToken.None));

        // And in an append-only ledger: the owner turns the guard off for one statement, as only an owner can.
        var reason = await ScalarAsync<string>("SELECT reason FROM agent_experience.lifecycle_events WHERE experience_id = @id", record.ExperienceId);
        Assert.StartsWith(SealedText.Prefix, reason, StringComparison.Ordinal);
        await OwnerExecuteUnguardedAsync(
            "lifecycle_events",
            "UPDATE agent_experience.lifecycle_events SET reason = @value WHERE experience_id = @id",
            record.ExperienceId,
            ("value", FlipOneBit(reason)));
        await Assert.ThrowsAsync<ExperienceStoreException>(() => store.GetFirstHistoryPageAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None));
    }

    [Fact]
    public async Task Sealed_text_moved_to_another_record_row_or_scope_is_refused_rather_than_read_there()
    {
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var a = Minimal(Scope(tenant));
        var b = Minimal(Scope(tenant));
        await store.CreateAsync(auth, a, CancellationToken.None);
        await store.CreateAsync(auth, b, CancellationToken.None);

        // A's payload copied onto B: B's key cannot open it, and neither could A's -- it is bound to A's ID.
        await OwnerExecuteAsync(
            "UPDATE agent_experience.experience_records SET payload = (SELECT payload FROM agent_experience.experience_records WHERE experience_id = @other) WHERE experience_id = @id",
            b.ExperienceId,
            ("other", a.ExperienceId));
        await Assert.ThrowsAsync<ExperienceStoreException>(() => store.GetAsync(auth, b.Scope, b.ExperienceId, CancellationToken.None));

        // A moved to another scope in place. Even a key store that ignored scope and handed back A's own key
        // could not open it there: the scope is in the associated data, so the tag refuses it.
        var c0 = Minimal(Scope(tenant));
        await store.CreateAsync(auth, c0, CancellationToken.None);
        await OwnerExecuteAsync(
            "UPDATE agent_experience.experience_records SET team_id = 'moved' WHERE experience_id = @id", c0.ExperienceId);
        var movedScope = Scope(tenant, team: "moved");
        var scopeBlind = new PostgresExperienceRecordStore(
            _fixture.DataSource, encryption: new ExperienceEncryption(new ScopeBlindKeyStore(keys.KeyStore, c0.Scope)));
        var moved = await Assert.ThrowsAsync<ExperienceStoreException>(() => scopeBlind.GetAsync(auth, movedScope, c0.ExperienceId, CancellationToken.None));
        Assert.Contains("failed authentication", moved.Message, StringComparison.Ordinal);

        // Two events of one record, their reasons swapped: each is bound to its own event ID.
        var c = Minimal(Scope(tenant));
        await store.CreateAsync(auth, c, CancellationToken.None);
        await store.CommitLifecycleEventAsync(auth, c.Scope, Event(c.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0, reason: "first"), CancellationToken.None);
        await store.CommitLifecycleEventAsync(auth, c.Scope, Event(c.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Stale, 1, reason: "second"), CancellationToken.None);
        await OwnerExecuteUnguardedAsync(
            "lifecycle_events",
            "UPDATE agent_experience.lifecycle_events e SET reason = s.reason FROM agent_experience.lifecycle_events s " +
            "WHERE e.experience_id = @id AND s.experience_id = @id AND e.applied_revision = 1 AND s.applied_revision = 2",
            c.ExperienceId);
        await Assert.ThrowsAsync<ExperienceStoreException>(() => store.GetFirstHistoryPageAsync(auth, c.Scope, c.ExperienceId, CancellationToken.None));
    }

    [Fact]
    public void The_associated_data_binds_every_value_to_its_column_its_row_its_record_and_each_scope_field()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(ExperienceDataKey.SizeInBytes);
        var scope = new Scope("t", "a", "p", "team", "agent", "user");
        var reference = new ExperienceKeyReference(Guid.NewGuid(), scope);
        var row = Guid.NewGuid();
        var sealedValue = SealedText.Seal(key, SealedText.AssociatedData(SealedText.EventReasonColumn, reference, row), "the reason");

        Assert.Equal("the reason", SealedText.Open(key, SealedText.AssociatedData(SealedText.EventReasonColumn, reference, row), sealedValue));

        foreach (var elsewhere in new[]
        {
            SealedText.AssociatedData(SealedText.GrantReasonColumn, reference, row),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference, Guid.NewGuid()),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { ExperienceId = Guid.NewGuid() }, row),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { Scope = scope with { TenantId = "t2" } }, row),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { Scope = scope with { ApplicationId = "a2" } }, row),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { Scope = scope with { ProjectId = "p2" } }, row),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { Scope = scope with { TeamId = null } }, row),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { Scope = scope with { AgentId = "" } }, row),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { Scope = scope with { UserId = "user2" } }, row),
            // Null is not empty, and the fields are length-prefixed, not concatenated.
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { Scope = scope with { TeamId = "" } }, row),
            SealedText.AssociatedData(SealedText.EventReasonColumn, reference with { Scope = scope with { ProjectId = "pt", TeamId = "eam" } }, row),
        })
        {
            Assert.Throws<ExperienceStoreException>(() => SealedText.Open(key, elsewhere, sealedValue));
        }

        // Two seals of one value never share a ciphertext: every value gets a fresh nonce.
        Assert.NotEqual(
            sealedValue,
            SealedText.Seal(key, SealedText.AssociatedData(SealedText.EventReasonColumn, reference, row), "the reason"));
    }

    // ------------------------------------------------------------------ rotation

    [Fact]
    public async Task Rotating_and_retiring_the_key_encryption_key_keeps_every_sealed_record_readable()
    {
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var records = Enumerable.Range(0, 3).Select(_ => Marked(Minimal(Scope(tenant)))).ToArray();
        foreach (var record in records)
        {
            await store.CreateAsync(auth, record, CancellationToken.None);
        }

        keys.Kek.AddKey("kek-2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), makeCurrent: true);
        while ((await keys.KeyStore.RewrapAsync(batchSize: 2, CancellationToken.None)).MoreRemain)
        {
        }

        keys.Kek.RetireKey("kek-1");

        foreach (var record in records)
        {
            var read = await store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
            Assert.Equal(record.TaskId, read.Record!.TaskId);
        }

        // New records are wrapped under the new KEK straight away.
        var fresh = Minimal(Scope(tenant));
        await store.CreateAsync(auth, fresh, CancellationToken.None);
        Assert.Equal("kek-2", (await keys.Repository.GetAsync(new(fresh.ExperienceId, fresh.Scope), CancellationToken.None))!.WrappedKey!.KeyEncryptionKeyId);
    }

    // ------------------------------------------------------------------ the ledgers

    [Fact]
    public async Task Event_reasons_evidence_detail_and_grant_reasons_are_sealed_and_read_back_in_the_clear()
    {
        var keys = new Keys();
        var encryption = keys.Encryption;
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: encryption);
        var grants = new PostgresExperienceGrantStore(_fixture.DataSource, encryption: encryption);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = Minimal(scope, status: ExperienceStatus.Validated) with { ReuseConfidence = 2d / 3d, SupportingValidations = 1 };
        await store.CreateAsync(auth, record, CancellationToken.None);

        var evidenceId = Guid.NewGuid();
        var commit = await store.CommitLifecycleEventAsync(
            auth,
            scope,
            Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0, reason: $"reinforced by the {IndexedWord} run") with
            {
                Confidence = new ConfidenceUpdate(
                    evidenceId, ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Machine, Guid.NewGuid(), Guid.NewGuid(), null,
                    "v1", 2d / 3d, 3d / 4d, 1, 2, 0, 0, Detail: UnindexedMarker),
            },
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, commit.Outcome);

        var administration = new GrantAdministration("admin", ColumnTime);
        var grant = (await grants.CreateAsync(auth, administration, GrantRequest(record.ExperienceId, scope), CancellationToken.None)).Grant!;
        Assert.Equal("shared for the incident review", grant.Reason);
        var revoked = await grants.RevokeAsync(auth, administration, new ExperienceGrantRevocation(grant.GrantId, scope, "review closed"), CancellationToken.None);
        Assert.Equal("review closed", revoked.Grant!.RevocationReason);

        // Nothing of any of it in the clear.
        foreach (var (sql, column) in new[]
        {
            ("SELECT reason FROM agent_experience.lifecycle_events WHERE experience_id = @id", "event reason"),
            ("SELECT confidence_detail FROM agent_experience.lifecycle_events WHERE experience_id = @id", "event detail"),
            ("SELECT detail FROM agent_experience.confidence_evidence WHERE experience_id = @id", "evidence detail"),
            ("SELECT reason FROM agent_experience.experience_grants WHERE experience_id = @id", "grant reason"),
            ("SELECT revocation_reason FROM agent_experience.experience_grants WHERE experience_id = @id", "revocation reason"),
            ("SELECT string_agg(reason, ',') FROM agent_experience.experience_grant_events WHERE experience_id = @id", "grant event reasons"),
        })
        {
            var stored = await ScalarAsync<string>(sql, record.ExperienceId);
            Assert.True(stored.StartsWith(SealedText.Prefix, StringComparison.Ordinal), $"{column} is stored in the clear.");
        }

        // Read back whole through the ports.
        var history = await store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        var stamped = Assert.Single(history.Events).Event;
        Assert.Equal($"reinforced by the {IndexedWord} run", stamped.Reason);
        Assert.Equal(UnindexedMarker, stamped.Confidence!.Detail);
        var listed = Assert.Single((await grants.ListAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Grants);
        Assert.Equal(("shared for the incident review", "review closed"), (listed.Reason, listed.RevocationReason));
        var trail = await grants.GetHistoryAsync(auth, scope, grant.GrantId, CancellationToken.None);
        Assert.Equal(["shared for the incident review", "review closed"], trail.Events.Select(e => e.Reason));

        // A replay of the evidence opens the stored detail to compare it, and so does a replay of the event.
        var replay = await store.CommitLifecycleEventAsync(
            auth,
            scope,
            Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0, eventId: stamped.EventId, reason: $"reinforced by the {IndexedWord} run") with
            {
                Confidence = stamped.Confidence,
            },
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, replay.Outcome);

        // Erasure makes all of it unreadable at once: one key.
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceKeyLookup.Destroyed, await keys.KeyStore.GetKeyAsync(new(record.ExperienceId, scope), CancellationToken.None));
    }

    [Fact]
    public async Task A_feedback_rationale_is_sealed_once_per_exposed_record_and_lasts_exactly_as_long_as_they_do()
    {
        var keys = new Keys();
        var store = keys.Store(_fixture.DataSource);
        var ledger = new PostgresExperienceReuseFeedbackStore(_fixture.DataSource, encryption: keys.Encryption);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var first = Minimal(scope);
        var second = Minimal(scope);
        await store.CreateAsync(auth, first, CancellationToken.None);
        await store.CreateAsync(auth, second, CancellationToken.None);

        var submission = HumanFeedback(scope, [first.ExperienceId, second.ExperienceId], $"the {UnindexedMarker} fix applied");
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Recorded, (await ledger.RecordAsync(auth, submission, CancellationToken.None)).Outcome);

        Assert.Equal("(sealed)", await ScalarAsync<string>("SELECT rationale FROM agent_experience.reuse_feedback WHERE feedback_id = @id", submission.FeedbackId));
        Assert.Equal(2L, await ScalarAsync<long>(
            "SELECT count(*) FROM agent_experience.reuse_feedback_exposures WHERE feedback_id = @id AND rationale_sealed LIKE 'aexp-sealed:v1:%'",
            submission.FeedbackId));

        // A replay opens a sealed copy to compare, and hands the rationale back in the clear.
        var replay = await ledger.RecordAsync(auth, submission, CancellationToken.None);
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, replay.Outcome);
        Assert.Equal(submission.Rationale, replay.Feedback!.Rationale);
        Assert.Equal(
            ExperienceReuseFeedbackStoreOutcome.Conflict,
            (await ledger.RecordAsync(auth, submission with { Rationale = "something else" }, CancellationToken.None)).Outcome);

        // One record erased: its copy is unreadable, the other still opens.
        await store.DeleteAsync(auth, scope, first.ExperienceId, CancellationToken.None);
        Assert.Equal(submission.Rationale, (await ledger.RecordAsync(auth, submission, CancellationToken.None)).Feedback!.Rationale);

        // Both erased: the submission itself is gone, exactly as in plaintext mode.
        await store.DeleteAsync(auth, scope, second.ExperienceId, CancellationToken.None);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agent_experience.reuse_feedback WHERE feedback_id = @id", submission.FeedbackId));
    }

    // ------------------------------------------------------------------ the upgrade

    [Fact]
    public async Task The_upgrade_job_seals_plaintext_records_in_bounded_resumable_batches_and_changes_nothing_they_answer()
    {
        var keys = new Keys();
        var encrypted = keys.Store(_fixture.DataSource);
        var plaintext = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var search = new PostgresExperienceCandidateSource(_fixture.DataSource, encryption: keys.Encryption);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var root = Scope(tenant);
        var records = new List<ExperienceRecord>();
        for (var n = 0; n < 5; n++)
        {
            var record = Marked(Minimal(n % 2 == 0 ? root : Scope(tenant, team: "team-1"), status: ExperienceStatus.Validated, createdAt: ColumnTime.AddMinutes(n)))
                with { ReuseConfidence = 0.5 };
            records.Add(record);
            await plaintext.CreateAsync(auth, record, CancellationToken.None);
        }

        await plaintext.CommitLifecycleEventAsync(auth, root, Event(records[0].ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0, reason: "legacy reason"), CancellationToken.None);

        var before = await AnswersAsync();

        // Exact reaches only the root's records; Subtree reaches the team's too. Two per call.
        var exact = await encrypted.SealPlaintextRecordsAsync(auth, root, 2, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal((ExperienceStoreOutcome.Committed, 2, true), (exact.Outcome, exact.SealedCount, exact.MoreRemain));

        // Oldest first: the root's two oldest are sealed, its newest and the team's records are not yet.
        var versions = await Task.WhenAll(records.Select(r => ScalarAsync<int>(
            "SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = @id", r.ExperienceId)));
        Assert.Equal(new[] { 2, 1, 2, 1, 1 }, versions);
        var passes = new List<ExperienceSealingResult>();
        do
        {
            passes.Add(await encrypted.SealPlaintextRecordsAsync(auth, root, 2, ScopeMatch.Subtree, CancellationToken.None));
        }
        while (passes[^1].MoreRemain);

        Assert.Equal([2, 1], passes.Select(p => p.SealedCount));
        Assert.Equal(0, (await encrypted.SealPlaintextRecordsAsync(auth, root, 2, ScopeMatch.Subtree, CancellationToken.None)).SealedCount);
        foreach (var record in records)
        {
            Assert.Equal(2, await ScalarAsync<int>("SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));
            Assert.DoesNotContain(UnindexedMarker, await RowTextAsync(record.ExperienceId), StringComparison.Ordinal);
        }

        // Every answer is the same as before: the records, their revisions, their search ranks, their history.
        Assert.Equal(before, await AnswersAsync());
        Assert.Equal("legacy reason", Assert.Single((await encrypted.GetFirstHistoryPageAsync(auth, root, records[0].ExperienceId, CancellationToken.None)).Events).Event.Reason);

        // A plaintext-mode store can no longer read them, and says so.
        await Assert.ThrowsAsync<ExperienceStoreException>(() => plaintext.GetAsync(auth, root, records[0].ExperienceId, CancellationToken.None));

        async Task<string> AnswersAsync()
        {
            var answers = new StringBuilder();
            foreach (var record in records)
            {
                answers.Append(Canonical((await encrypted.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Record!)).Append('\n');
            }

            foreach (var scope in new[] { root, Scope(tenant, team: "team-1") })
            {
                foreach (var candidate in (await search.SearchAsync(auth, Query(scope), CancellationToken.None)).Candidates.OrderBy(c => c.Record.ExperienceId))
                {
                    answers.Append(candidate.Record.ExperienceId).Append(' ').Append(candidate.Relevance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                }
            }

            return answers.ToString();
        }
    }

    [Fact]
    public async Task The_upgrade_job_is_authorized_needs_encryption_and_a_role_without_the_opt_in_cannot_run_it()
    {
        var keys = new Keys();
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var plaintext = new PostgresExperienceRecordStore(_fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var record = Minimal(scope);
        await plaintext.CreateAsync(auth, record, CancellationToken.None);

        var invalid = await plaintext.SealPlaintextRecordsAsync(auth, scope, 10, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, invalid.Outcome);
        Assert.Contains(invalid.Errors, error => error.Path == "Encryption");

        var encrypted = keys.Store(_fixture.DataSource);
        Assert.Equal(ExperienceStoreOutcome.Denied, (await encrypted.SealPlaintextRecordsAsync(Authorize(NewTenant()), scope, 10, ScopeMatch.Exact, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Invalid, (await encrypted.SealPlaintextRecordsAsync(auth, scope, 0, ScopeMatch.Exact, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Invalid, (await encrypted.SealPlaintextRecordsAsync(auth, scope, 10, (ScopeMatch)7, CancellationToken.None)).Outcome);

        var role = await _fixture.CreateLoginRoleAsync("shrednoseal");
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            _fixture.OwnerDataSource, new ExperienceApplicationRoleOptions(role), CancellationToken.None);
        await using var source = NpgsqlDataSource.Create(_fixture.ConnectionString(PostgresFixture.StoreDatabase, role));
        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => keys.Store(source).SealPlaintextRecordsAsync(auth, scope, 10, ScopeMatch.Exact, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
        Assert.Equal(1, await ScalarAsync<int>("SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = @id", record.ExperienceId));

        // The function itself admits nothing but a well-formed seal of a live plaintext row at its revision.
        await using var connection = await _fixture.OwnerDataSource.OpenConnectionAsync();
        foreach (var (revision, payload, expected) in new[]
        {
            (0L, """{"sealed": "not-sealed"}""", "Invalid"),
            (0L, """{"sealed": "aexp-sealed:v1:AAAA", "extra": 1}""", "Invalid"),
            (5L, """{"sealed": "aexp-sealed:v1:AAAA"}""", "StaleRevision"),
        })
        {
            await using var call = new NpgsqlCommand(
                "SELECT agent_experience.seal_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, @revision, @payload::jsonb)",
                connection);
            call.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            call.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
            call.Parameters.Add(new NpgsqlParameter<long>("revision", revision));
            call.Parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Text) { TypedValue = payload });
            Assert.Equal(expected, (string?)await call.ExecuteScalarAsync());
        }
    }

    // ------------------------------------------------------------------ wiring

    [Fact]
    public void The_DI_extensions_give_every_component_the_registered_encryption()
    {
        var keyStore = new Keys().KeyStore;
        foreach (var byDataSource in new[] { false, true })
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, _fixture.DataSource);
            DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresEncryption(services, keyStore);
            if (byDataSource)
            {
                DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresStore(services, _fixture.DataSource);
                DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresCandidateSource(services, _fixture.DataSource);
                DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresGrantStore(services, _fixture.DataSource);
                DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresReuseFeedbackStore(services, _fixture.DataSource);
            }
            else
            {
                DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresStore(services);
                DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresCandidateSource(services);
                DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresGrantStore(services);
                DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresReuseFeedbackStore(services);
            }

            using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
            var registered = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ExperienceEncryption>(provider);
            Assert.Same(keyStore, registered.KeyStore);
            foreach (var component in new object[]
            {
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IExperienceRecordStore>(provider),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IExperienceCandidateSource>(provider),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IExperienceGrantStore>(provider),
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IExperienceReuseFeedbackStore>(provider),
            })
            {
                Assert.Same(registered, EncryptionOf(component));
            }
        }
    }

    [Fact]
    public async Task Default_constructed_grant_and_feedback_stores_seal_exactly_when_the_suite_runs_encrypted()
    {
        // Pins that the encrypted CI leg really exercises these two stores sealed, and the plaintext leg plaintext.
        var store = new PostgresExperienceRecordStore(_fixture.DataSource);
        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        var ledger = new PostgresExperienceReuseFeedbackStore(_fixture.DataSource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));
        await store.CreateAsync(auth, record, CancellationToken.None);
        await grants.CreateAsync(auth, new GrantAdministration("admin", ColumnTime), GrantRequest(record.ExperienceId, record.Scope), CancellationToken.None);
        var feedback = HumanFeedback(record.Scope, [record.ExperienceId], "it applied");
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Recorded, (await ledger.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        var reason = await ScalarAsync<string>("SELECT reason FROM agent_experience.experience_grants WHERE experience_id = @id", record.ExperienceId);
        var rationale = await ScalarAsync<string>("SELECT rationale FROM agent_experience.reuse_feedback WHERE feedback_id = @id", feedback.FeedbackId);
        Assert.Equal(EncryptionMode.IsOn, reason.StartsWith(SealedText.Prefix, StringComparison.Ordinal));
        Assert.Equal(EncryptionMode.IsOn ? "(sealed)" : "it applied", rationale);
    }

    private static ExperienceEncryption? EncryptionOf(object component) =>
        (ExperienceEncryption?)component.GetType()
            .GetField("_encryption", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(component);

    // ------------------------------------------------------------------ helpers

    private static ExperienceRecord Marked(ExperienceRecord record) => record with
    {
        TaskId = "task-" + Guid.NewGuid().ToString("N")[..8],
        TaskSummary = "Resolve the refund ticket",
        Attempts = [new Attempt(Guid.NewGuid(), 0, PayloadTime, TimeSpan.FromSeconds(1), [], UnindexedMarker, null)],
        Reflection = new Reflection(
            Guid.NewGuid(), record.SourceRunId, $"Wait for the {IndexedWord} lock to clear before retrying.", [], [], [], [], null, [],
            record.Outcome.Status, record.CompletionScore, "v1", "tests", PayloadTime),
    };

    private static ExperienceCandidateQuery Query(Scope scope) =>
        new(scope, $"{IndexedWord} lock refund", [ExperienceStatus.Validated], 0);

    private static ExperienceGrantRequest GrantRequest(Guid experienceId, Scope owner) => new(
        Guid.NewGuid(),
        experienceId,
        owner,
        owner with { TeamId = "recipients" },
        "shared for the incident review",
        DateTimeOffset.UtcNow.AddHours(1),
        ExperienceGrantDisclosure.LessonOnly);

    private static RecordedExperienceReuseFeedback HumanFeedback(Scope scope, IReadOnlyList<Guid> exposed, string rationale) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        scope,
        TaskVerificationStatus.Verified,
        ExperienceReuseBenefit.Unknown,
        ExperienceReuseBenefit.Improved,
        ReuseAttributionSource.HumanAssessment,
        ReviewerIdentity: "reviewer-1",
        EvaluatorId: null,
        VerificationRoundId: null,
        AssessmentId: Guid.NewGuid(),
        Rationale: rationale,
        EvidenceIds: [],
        AttributedAt: ColumnTime,
        new ReuseMeasure("task-success", 1),
        TrialLabel: null,
        ColumnTime,
        [.. exposed.Select(id => new ExperienceReuseExposure(id, Attributed: true, EvidenceId: Guid.NewGuid()))]);

    private static string FlipOneBit(string sealedValue)
    {
        var blob = Convert.FromBase64String(sealedValue[SealedText.Prefix.Length..]);
        blob[blob.Length / 2] ^= 0x01;
        return SealedText.Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>Everything the row holds, as the database renders it -- every column, generated ones included.</summary>
    private Task<string> RowTextAsync(Guid experienceId) =>
        ScalarAsync<string>("SELECT r::text FROM agent_experience.experience_records r WHERE r.experience_id = @id", experienceId);

    private Task<T> ScalarAsync<T>(string sql, Guid experienceId) => ScalarAsync<T>(_fixture.OwnerDataSource, sql, experienceId);

    private async Task<T> ScalarAsync<T>(string sql, Guid experienceId, (string Name, Guid Value) extra)
    {
        await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(extra.Name, extra.Value));
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql, Guid experienceId)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task OwnerExecuteAsync(string sql, Guid experienceId, params (string Name, object Value)[] extra)
    {
        await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        foreach (var (name, value) in extra)
        {
            command.Parameters.Add(new NpgsqlParameter { ParameterName = name, Value = value });
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs one statement against an append-only ledger with its guard switched off, in one transaction, as
    /// the owner -- the tampering only the owner (or a superuser) can do, which is what authentication exists
    /// to catch. The guard is back on before anything else can see the table.
    /// </summary>
    private async Task OwnerExecuteUnguardedAsync(string table, string sql, Guid experienceId, params (string Name, object Value)[] extra)
    {
        await using var connection = await _fixture.OwnerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var guards = new List<string>();
        await using (var list = new NpgsqlCommand(
            $"SELECT tgname::text FROM pg_trigger WHERE tgrelid = 'agent_experience.{table}'::regclass AND NOT tgisinternal AND tgenabled = 'A'",
            connection,
            transaction))
        {
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                guards.Add(reader.GetString(0));
            }
        }

        Assert.NotEmpty(guards);
        foreach (var guard in guards)
        {
            await using var off = new NpgsqlCommand($"ALTER TABLE agent_experience.{table} DISABLE TRIGGER \"{guard}\"", connection, transaction);
            await off.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
            foreach (var (name, value) in extra)
            {
                command.Parameters.Add(new NpgsqlParameter { ParameterName = name, Value = value });
            }

            Assert.True(await command.ExecuteNonQueryAsync() > 0);
        }

        foreach (var guard in guards)
        {
            await using var on = new NpgsqlCommand($"ALTER TABLE agent_experience.{table} ENABLE ALWAYS TRIGGER \"{guard}\"", connection, transaction);
            await on.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// A fresh database owned by the fixture's owner role and migrated by it, with autovacuum off on the
    /// record table so nothing reclaims a dead tuple while a test is reading it, and <c>pageinspect</c> for the
    /// raw heap read.
    /// </summary>
    private async Task<IsolatedDatabase> IsolatedDatabaseAsync(string purpose)
    {
        var name = await _fixture.CreateDatabaseNameAsync(purpose, PostgresFixture.OwnerRoleName);
        var source = NpgsqlDataSource.Create(_fixture.ConnectionString(name, PostgresFixture.OwnerRoleName));
        await ExperienceSchemaMigrator.MigrateAsync(source, CancellationToken.None);
        await using (var command = source.CreateCommand("ALTER TABLE agent_experience.experience_records SET (autovacuum_enabled = false)"))
        {
            await command.ExecuteNonQueryAsync();
        }

        await _fixture.ExecuteAsSuperuserAsync("CREATE EXTENSION IF NOT EXISTS pageinspect", name);
        return new IsolatedDatabase(name, source);
    }

    /// <summary>Every heap page of the record table and of its TOAST table, raw, as Latin-1 text for searching.</summary>
    private async Task<string> RawHeapAsync(string database)
    {
        await using var source = NpgsqlDataSource.Create(_fixture.ConnectionString(database, username: null));
        var pages = new StringBuilder();
        foreach (var relation in new[]
        {
            "agent_experience.experience_records",
            await ScalarTextAsync(source, "SELECT reltoastrelid::regclass::text FROM pg_class WHERE oid = 'agent_experience.experience_records'::regclass"),
        })
        {
            await using var command = source.CreateCommand(
                "SELECT get_raw_page(@relation, 'main', b::int) FROM generate_series(0, pg_relation_size(@relation::regclass) / current_setting('block_size')::int - 1) b");
            command.Parameters.Add(new NpgsqlParameter<string>("relation", NpgsqlDbType.Text) { TypedValue = relation });
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pages.Append(Encoding.Latin1.GetString(reader.GetFieldValue<byte[]>(0)));
            }
        }

        return pages.ToString();
    }

    private static async Task<string> ScalarTextAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed record IsolatedDatabase(string Database, NpgsqlDataSource DataSource) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }

    /// <summary>One deployment's worth of keys: the reference envelope store over an in-process KEK.</summary>
    private sealed class Keys
    {
        public Keys()
        {
            Kek = LocalExperienceKeyEncryptionKey.Generate("kek-1");
            Repository = new InMemoryExperienceWrappedKeyRepository();
            KeyStore = new EnvelopeExperienceKeyStore(Kek, Repository);
            Encryption = new ExperienceEncryption(KeyStore);
        }

        public LocalExperienceKeyEncryptionKey Kek { get; }

        public InMemoryExperienceWrappedKeyRepository Repository { get; }

        public EnvelopeExperienceKeyStore KeyStore { get; }

        public ExperienceEncryption Encryption { get; }

        public PostgresExperienceRecordStore Store(NpgsqlDataSource source) => new(source, encryption: Encryption);
    }

    /// <summary>A key store that answers every scope with one scope's key: what a broken key store would do.</summary>
    private sealed class ScopeBlindKeyStore(IExperienceKeyStore inner, Scope realScope) : IExperienceKeyStore
    {
        public ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.CreateKeyAsync(reference with { Scope = realScope }, cancellationToken);

        public ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.GetKeyAsync(reference with { Scope = realScope }, cancellationToken);

        public ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.DestroyKeyAsync(reference with { Scope = realScope }, cancellationToken);
    }

    /// <summary>A key store whose destruction fails before, or after, it actually destroys the key.</summary>
    private sealed class FaultyKeyStore(IExperienceKeyStore inner) : IExperienceKeyStore
    {
        public bool FailBeforeDestroy { get; set; }

        public bool FailAfterDestroy { get; set; }

        public ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.CreateKeyAsync(reference, cancellationToken);

        public ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.GetKeyAsync(reference, cancellationToken);

        public async ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            if (FailBeforeDestroy)
            {
                throw new InvalidOperationException("The key store is unreachable.");
            }

            await inner.DestroyKeyAsync(reference, cancellationToken);
            if (FailAfterDestroy)
            {
                throw new InvalidOperationException("The process died right after the key was destroyed.");
            }
        }
    }
}
