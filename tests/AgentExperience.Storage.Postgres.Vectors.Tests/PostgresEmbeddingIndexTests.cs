using AgentExperience.Core.Indexing;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Container-backed coverage of <see cref="PostgresExperienceEmbeddingIndex"/> against a real
/// PostgreSQL 16 + pgvector: ingestion after a commit, the conditional write's stale and deleted
/// cases, re-index idempotence, and the scope isolation both retrieval channels have to share. Every
/// embedding is produced by a deterministic in-test generator -- no model, no network, no credentials.
/// </summary>
[Collection(VectorsCollection.Name)]
public class PostgresEmbeddingIndexTests(VectorsFixture fixture)
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    private NpgsqlDataSource DataSource => fixture.DataSource;

    // ---------------------------------------------------------------- the schema itself

    [Fact]
    public async Task The_migration_creates_the_vector_extension_and_the_embedding_table()
    {
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname = 'vector'"));

        var columns = await ColumnsAsync("experience_embeddings");
        Assert.Equal(
            [
                "agent_id", "application_id", "content_hash", "created_at", "dimension", "embedding",
                "experience_id", "model_id", "project_id", "source_revision", "team_id", "tenant_id",
                "updated_at", "user_id",
            ],
            columns.Keys.Order(StringComparer.Ordinal));

        // Unconstrained on purpose: the dimension belongs to whichever model a host configured.
        Assert.Equal("USER-DEFINED", columns["embedding"]);
        Assert.Null(await ScalarAsync<int?>(
            "SELECT atttypmod FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'agent_experience' AND c.relname = 'experience_embeddings' AND a.attname = 'embedding' " +
            "AND a.atttypmod <> -1"));
    }

    [Fact]
    public async Task The_out_of_band_HNSW_index_is_created_explicitly_and_creating_it_twice_is_a_no_op()
    {
        const int Dimension = 5;
        var name = ExperienceVectorIndexMaintenance.IndexNameFor(Dimension);

        try
        {
            await ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(fixture.OwnerDataSource, Dimension);
            await ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(fixture.OwnerDataSource, Dimension);

            var definition = await ScalarAsync<string>(
                $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'agent_experience' AND indexname = '{name}'");

            Assert.NotNull(definition);
            Assert.Contains("hnsw", definition, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("vector_cosine_ops", definition, StringComparison.Ordinal);
            // Partial on the dimension, which is what makes the cast in the indexed expression safe on
            // a table that may hold several widths at once.
            Assert.Contains("WHERE (dimension = 5)", definition, StringComparison.Ordinal);
        }
        finally
        {
            await ExperienceVectorIndexMaintenance.DropHnswIndexAsync(fixture.OwnerDataSource, Dimension);
        }

        Assert.Equal(
            0L,
            await ScalarAsync<long>($"SELECT count(*) FROM pg_indexes WHERE schemaname = 'agent_experience' AND indexname = '{name}'"));
    }

    [Fact]
    public async Task The_search_actually_uses_the_out_of_band_HNSW_index()
    {
        // Creating the index is not the claim -- the search *using* it is. The search's ORDER BY and
        // the index's expression have to match exactly, and nothing but the planner can confirm that.
        var world = await WorldAsync();
        var dimension = world.Generator.Dimension;
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        await ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(fixture.OwnerDataSource, dimension);
        try
        {
            var plan = await world.ExplainSearchAsync(TopicEmbeddingGenerator.VectorFor("refund stuck on a lock"));
            Assert.Contains(ExperienceVectorIndexMaintenance.IndexNameFor(dimension), plan, StringComparison.Ordinal);
            // And the scope predicate on the embeddings row is what keeps a foreign scope out of the
            // HNSW walk itself rather than only out of the joined result.
            Assert.Contains("experience_embeddings e", plan, StringComparison.Ordinal);
        }
        finally
        {
            await ExperienceVectorIndexMaintenance.DropHnswIndexAsync(fixture.OwnerDataSource, dimension);
        }

        // Dropped, the same search is still correct -- pgvector simply scans exactly.
        var afterDrop = await world.Index.SearchAsync(
            world.Authorization,
            VectorQuery(world, TopicEmbeddingGenerator.VectorFor("refund stuck on a lock")),
            CancellationToken.None);

        Assert.Equal(ExperienceVectorSearchOutcome.Found, afterDrop.Outcome);
        Assert.Equal(id, Assert.Single(afterDrop.Candidates).Record.ExperienceId);
    }

    // ---------------------------------------------------------------- matrix: index after commit

    [Fact]
    public async Task Indexing_a_committed_record_stores_the_vector_with_its_model_dimension_hash_and_revision()
    {
        var world = await WorldAsync();
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock before retrying");

        var result = await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        Assert.Equal(ExperienceIndexingOutcome.Indexed, result.Outcome);

        var stored = await world.ReadEmbeddingAsync(id);
        Assert.Equal("topic-embed-v1", stored.ModelId);
        Assert.Equal(4, stored.Dimension);
        Assert.Equal(0, stored.SourceRevision);
        Assert.Equal(
            ExperienceEmbeddingDescriptor.ComputeContentHash(
                "topic-embed-v1",
                ExperienceRetrievalSummary.For("refund-ticket", "Resolve a refund ticket", "Release the lock before retrying")),
            stored.ContentHash);

        // The scope columns are copied from the record row inside the conditional write, never from
        // caller input, so they always agree with the record they describe.
        Assert.Equal(world.Scope.TenantId, stored.TenantId);
        Assert.Equal(world.Scope.ProjectId, stored.ProjectId);
    }

    // ---------------------------------------------------------------- matrix: stale write

    [Fact]
    public async Task A_write_whose_revision_has_moved_is_rejected_and_the_stored_vector_still_names_the_old_revision()
    {
        var world = await WorldAsync();
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await world.BumpRevisionAsync(id, 1);

        Assert.Equal(ExperienceIndexingOutcome.Indexed, (await world.Indexing.IndexAsync(world.Authorization, world.Scope, id)).Outcome);
        Assert.Equal(1, (await world.ReadEmbeddingAsync(id)).SourceRevision);

        // The record moves to revision 2, and an in-flight write computed from revision 1 tries to land.
        await world.BumpRevisionAsync(id, 2);

        var stale = await world.Index.WriteAsync(
            world.Authorization,
            new ExperienceIndexWrite(
                world.Scope,
                id,
                new ExperienceEmbeddingDescriptor("topic-embed-v1", 4, "a-different-hash", 1),
                TopicEmbeddingGenerator.VectorFor("something else entirely")),
            CancellationToken.None);

        Assert.Equal(ExperienceIndexOutcome.Stale, stale.Outcome);
        Assert.Equal(2, stale.CurrentRevision);

        var unchanged = await world.ReadEmbeddingAsync(id);
        Assert.Equal(1, unchanged.SourceRevision);
        Assert.NotEqual("a-different-hash", unchanged.ContentHash);
    }

    [Fact]
    public async Task A_write_from_an_older_revision_can_never_overwrite_one_already_stored_from_a_newer_one()
    {
        // The upsert's second line of defence, for two writes racing rather than one arriving late: the
        // record is at revision 1, an older in-flight write computed from revision 0 is still valid by
        // the INSERT's own predicate only if the record were still at 0 -- so this drives the guard by
        // storing from the newer revision first and then replaying the older write at the same revision.
        var world = await WorldAsync();
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");

        var newer = new ExperienceEmbeddingDescriptor("topic-embed-v1", 4, "newer-hash", 0);
        Assert.Equal(
            ExperienceIndexOutcome.Written,
            (await world.Index.WriteAsync(
                world.Authorization,
                new ExperienceIndexWrite(world.Scope, id, newer, TopicEmbeddingGenerator.VectorFor("newer")),
                CancellationToken.None)).Outcome);

        // Force the stored row to claim a source revision beyond the record's own, which is exactly what
        // a write that landed from a newer revision leaves behind for a slower racer to find.
        await world.ForceStoredSourceRevisionAsync(id, 5);

        var loser = await world.Index.WriteAsync(
            world.Authorization,
            new ExperienceIndexWrite(
                world.Scope,
                id,
                new ExperienceEmbeddingDescriptor("topic-embed-v1", 4, "older-hash", 0),
                TopicEmbeddingGenerator.VectorFor("older")),
            CancellationToken.None);

        Assert.Equal(ExperienceIndexOutcome.Stale, loser.Outcome);

        var stored = await world.ReadEmbeddingAsync(id);
        Assert.Equal("newer-hash", stored.ContentHash);
        Assert.Equal(5, stored.SourceRevision);
    }

    // ---------------------------------------------------------------- matrix: deleted record

    [Fact]
    public async Task A_record_deleted_before_the_write_lands_is_never_recreated()
    {
        var world = await WorldAsync();
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await world.DeleteRecordAsync(id);

        var missing = await world.Index.WriteAsync(
            world.Authorization,
            new ExperienceIndexWrite(
                world.Scope,
                id,
                new ExperienceEmbeddingDescriptor("topic-embed-v1", 4, "hash", 0),
                TopicEmbeddingGenerator.VectorFor("anything")),
            CancellationToken.None);

        Assert.Equal(ExperienceIndexOutcome.Missing, missing.Outcome);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(id));
    }

    [Fact]
    public async Task Deleting_a_record_takes_its_embedding_with_it()
    {
        var world = await WorldAsync();
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));

        await world.DeleteRecordAsync(id);

        // The foreign key cascades, so an embedding can never outlive the record it describes.
        Assert.Equal(0L, await world.CountEmbeddingsAsync(id));
    }

    // ---------------------------------------------------------------- matrix: reindex

    [Fact]
    public async Task Reindexing_unchanged_records_calls_no_provider_and_rewrites_nothing()
    {
        var world = await WorldAsync();
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");

        var first = await world.Indexing.ReindexAsync(world.Authorization, new ReindexExperienceRequest(world.Scope));
        Assert.Equal(1, first.Indexed);
        var writtenAt = (await world.ReadEmbeddingAsync(id)).UpdatedAt;
        var callsAfterFirst = world.Generator.Requests.Count;

        var second = await world.Indexing.ReindexAsync(world.Authorization, new ReindexExperienceRequest(world.Scope));

        Assert.Equal(ExperienceReindexOutcome.Completed, second.Outcome);
        Assert.Equal(1, second.Examined);
        Assert.Equal(0, second.Indexed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(callsAfterFirst, world.Generator.Requests.Count);
        Assert.Equal(writtenAt, (await world.ReadEmbeddingAsync(id)).UpdatedAt);
    }

    [Fact]
    public async Task Reindexing_a_changed_summary_rewrites_it_once_and_the_repeat_is_idempotent()
    {
        var world = await WorldAsync();
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");

        await world.Indexing.ReindexAsync(world.Authorization, new ReindexExperienceRequest(world.Scope));
        var firstHash = (await world.ReadEmbeddingAsync(id)).ContentHash;

        await world.RewriteLessonAsync(id, "Release the lock and confirm the ledger entry afterwards");

        var rewritten = await world.Indexing.ReindexAsync(world.Authorization, new ReindexExperienceRequest(world.Scope));
        Assert.Equal(1, rewritten.Indexed);
        Assert.NotEqual(firstHash, (await world.ReadEmbeddingAsync(id)).ContentHash);

        var repeat = await world.Indexing.ReindexAsync(world.Authorization, new ReindexExperienceRequest(world.Scope));
        Assert.Equal(0, repeat.Indexed);
        Assert.Equal(1, repeat.Skipped);
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));
    }

    [Fact]
    public async Task A_scope_larger_than_one_page_is_walked_to_the_end_by_the_cursor()
    {
        // Without a cursor every pass re-reads the same first page, so a scope larger than the limit is
        // never fully indexed however often the pass runs.
        var world = await WorldAsync();
        var ids = new List<Guid>();
        for (var n = 0; n < 5; n++)
        {
            ids.Add(await world.AddRecordAsync($"task-{n}", $"summary {n}", $"lesson {n}"));
        }

        var seen = new List<Guid>();
        Guid? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var pass = await world.Indexing.ReindexAsync(
                world.Authorization,
                new Core.Indexing.ReindexExperienceRequest(world.Scope, Limit: 2, StartAfterId: cursor));

            Assert.Equal(ExperienceReindexOutcome.Completed, pass.Outcome);
            seen.AddRange(pass.Records.Select(record => record.ExperienceId));

            if (pass.LastExaminedId is null)
            {
                // The documented way to know the scope is exhausted.
                Assert.Equal(0, pass.Examined);
                break;
            }

            cursor = pass.LastExaminedId;
        }

        Assert.Equal(ids.Count, seen.Distinct().Count());
        Assert.Equal(ids.Order(), seen.Order());
        Assert.All(ids, id => Assert.Equal(1L, world.CountEmbeddingsAsync(id).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task A_record_a_search_could_never_return_is_never_listed_and_never_embedded()
    {
        // The scan applies the search's own status and confidence predicates, so a quarantined or
        // low-confidence record's task summary and lesson never leave the database for a provider.
        var world = await WorldAsync();
        var eligible = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        var quarantined = await world.AddRecordAsync("secret-task", "Quarantined summary", "Quarantined lesson", status: ExperienceStatus.Quarantined);
        var lowConfidence = await world.AddRecordAsync("weak-task", "Low confidence summary", "Low confidence lesson", confidence: 0.1);

        var pass = await world.Indexing.ReindexAsync(world.Authorization, new Core.Indexing.ReindexExperienceRequest(world.Scope));

        Assert.Equal(1, pass.Examined);
        Assert.Equal([eligible], pass.Records.Select(record => record.ExperienceId));
        Assert.Equal(0L, await world.CountEmbeddingsAsync(quarantined));
        Assert.Equal(0L, await world.CountEmbeddingsAsync(lowConfidence));

        // And nothing about them was handed to the provider.
        Assert.DoesNotContain(world.Generator.Requests, text => text.Contains("Quarantined", StringComparison.Ordinal));
        Assert.DoesNotContain(world.Generator.Requests, text => text.Contains("Low confidence", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- scope and eligibility in SQL

    [Fact]
    public async Task A_vector_search_never_returns_a_record_from_another_scope_however_close_its_vector()
    {
        var mine = await WorldAsync();
        var theirs = await WorldAsync();

        var ours = await mine.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        var foreign = await theirs.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await mine.Indexing.IndexAsync(mine.Authorization, mine.Scope, ours);
        await theirs.Indexing.IndexAsync(theirs.Authorization, theirs.Scope, foreign);

        var results = await mine.Index.SearchAsync(
            mine.Authorization,
            new ExperienceVectorQuery(
                mine.Scope,
                "topic-embed-v1",
                TopicEmbeddingGenerator.VectorFor("refund stuck on a lock"),
                [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
                MinimumConfidence: 0.5,
                Limit: 50),
            CancellationToken.None);

        Assert.Equal(ExperienceVectorSearchOutcome.Found, results.Outcome);
        Assert.Equal([ours], results.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.All(results.Candidates, candidate => Assert.Equal(mine.Scope, candidate.Record.Scope));
        Assert.All(results.Candidates, candidate => Assert.InRange(candidate.Relevance, 0d, 1d));
    }

    [Fact]
    public async Task The_status_filter_and_the_confidence_floor_are_applied_in_SQL_on_the_vector_channel_too()
    {
        var world = await WorldAsync();
        var eligible = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        var quarantined = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock", status: ExperienceStatus.Quarantined);
        var lowConfidence = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock", confidence: 0.1);

        foreach (var id in new[] { eligible, quarantined, lowConfidence })
        {
            await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);
        }

        var results = await world.Index.SearchAsync(
            world.Authorization,
            new ExperienceVectorQuery(
                world.Scope,
                "topic-embed-v1",
                TopicEmbeddingGenerator.VectorFor("refund stuck on a lock"),
                [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
                MinimumConfidence: 0.5,
                Limit: 50),
            CancellationToken.None);

        Assert.Equal([eligible], results.Candidates.Select(candidate => candidate.Record.ExperienceId));
    }

    [Fact]
    public async Task A_scope_outside_the_authorization_is_denied_before_any_statement_runs()
    {
        var world = await WorldAsync();
        var elsewhere = new AuthorizationContext(world.Scope.TenantId, "p", [], Now, ProjectId: "elsewhere");

        var write = await world.Index.WriteAsync(
            elsewhere,
            new ExperienceIndexWrite(world.Scope, Guid.NewGuid(), new ExperienceEmbeddingDescriptor("m", 4, "h", 0), new float[4]),
            CancellationToken.None);
        var scan = await world.Index.ScanAsync(elsewhere, new ExperienceIndexScan(world.Scope, "m", [ExperienceStatus.Validated], 0.5), CancellationToken.None);
        var search = await world.Index.SearchAsync(
            elsewhere,
            new ExperienceVectorQuery(world.Scope, "m", new float[4], [ExperienceStatus.Validated], 0.5),
            CancellationToken.None);

        Assert.Equal(ExperienceIndexOutcome.Denied, write.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, scan.Outcome);
        Assert.Equal(ExperienceVectorSearchOutcome.Denied, search.Outcome);
    }

    [Fact]
    public async Task A_malformed_request_is_Invalid_with_every_field_path_and_no_database_call()
    {
        var index = new PostgresExperienceEmbeddingIndex(Unreachable()); // reaching it would throw

        var search = await index.SearchAsync(
            new AuthorizationContext("t", "p", [], Now),
            new ExperienceVectorQuery(new Scope("t", "app-1", " "), "  ", ReadOnlyMemory<float>.Empty, [], 1.5, 0),
            CancellationToken.None);

        Assert.Equal(ExperienceVectorSearchOutcome.Invalid, search.Outcome);
        Assert.Equal(
            ["Scope.ProjectId", "ModelId", "Vector", "EligibleStatuses", "MinimumConfidence", "Limit"],
            search.Errors.Select(error => error.Path));

        var write = await index.WriteAsync(
            new AuthorizationContext("t", "p", [], Now),
            new ExperienceIndexWrite(new Scope("t", "app-1", "project-1"), Guid.Empty, new ExperienceEmbeddingDescriptor(" ", 0, " ", -1), new[] { float.NaN }),
            CancellationToken.None);

        Assert.Equal(ExperienceIndexOutcome.Invalid, write.Outcome);
        Assert.Equal(
            ["ExperienceId", "Descriptor.ModelId", "Descriptor.Dimension", "Descriptor.ContentHash", "Descriptor.SourceRevision", "Vector"],
            write.Errors.Select(error => error.Path));
    }

    [Fact]
    public async Task Null_arguments_throw()
    {
        var index = new PostgresExperienceEmbeddingIndex(Unreachable());
        var authorization = new AuthorizationContext("t", "p", [], Now);

        Assert.Throws<ArgumentNullException>(() => new PostgresExperienceEmbeddingIndex(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.WriteAsync(null!, new ExperienceIndexWrite(new Scope("t", "a", "p"), Guid.NewGuid(), new ExperienceEmbeddingDescriptor("m", 1, "h", 0), new float[1]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.WriteAsync(authorization, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.ScanAsync(authorization, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.SearchAsync(authorization, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ExperienceVectorIndexMaintenance.EnsureHnswIndexAsync(null!, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceVectorIndexMaintenance.IndexNameFor(0));
    }

    [Fact]
    public async Task A_vector_search_that_discloses_a_borrowed_record_audits_it_and_names_the_grant()
    {
        // The vector channel returns ExperienceCandidate.Record read back in full, exactly as the text
        // channel does, so handing one across a scope boundary is a disclosure and is recorded.
        var world = await WorldAsync();
        var owner = world.Scope with { TeamId = "team-a" };
        var recipient = world.Scope with { TeamId = "team-b" };

        var borrowed = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock", scope: owner);
        var mine = await world.AddRecordAsync("refund-retry", "Retry a refund ticket", "Retry once", scope: recipient);
        await world.Indexing.IndexAsync(world.Authorization, owner, borrowed);
        await world.Indexing.IndexAsync(world.Authorization, recipient, mine);
        var grant = await world.GrantAsync(borrowed, owner, recipient);

        var failures = new List<ExperienceGrantAccessFailure>();
        var log = new PostgresExperienceGrantAccessLog(world.DataSource);
        var audited = new PostgresExperienceEmbeddingIndex(
            world.DataSource, onGrantsUnavailable: null, auditing: new ExperienceGrantAuditing(log, failures.Add));

        var query = new ExperienceVectorQuery(
            recipient,
            world.Generator.ModelId,
            TopicEmbeddingGenerator.VectorFor("refund stuck on a lock"),
            [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
            MinimumConfidence: 0.5,
            Limit: 50,
            CorrelationId: "corr-vector");

        var result = await audited.SearchAsync(world.Authorization, query, CancellationToken.None);

        Assert.Equal(ExperienceVectorSearchOutcome.Found, result.Outcome);
        Assert.Empty(failures);

        var shared = Assert.Single(result.Candidates, candidate => candidate.SharedByGrant);
        Assert.Equal(borrowed, shared.Record.ExperienceId);
        Assert.Equal(grant.GrantId, shared.PermittingGrantId);

        var rows = await log.QueryAsync(
            world.Authorization, new ExperienceGrantAccessQuery(owner), CancellationToken.None);

        var row = Assert.Single(rows.Accesses);
        Assert.Equal(borrowed, row.ExperienceId);
        Assert.Equal(grant.GrantId, row.GrantId);
        Assert.Equal(recipient, row.RecipientScope);
        Assert.Equal("corr-vector", row.CorrelationId);

        // The reader's own record needed no grant, so nothing claims it was disclosed.
        var minesTrail = await log.QueryAsync(
            world.Authorization, new ExperienceGrantAccessQuery(recipient), CancellationToken.None);
        Assert.Empty(minesTrail.Accesses);
    }

    [Fact]
    public async Task A_required_vector_search_whose_rows_cannot_be_written_returns_no_candidates()
    {
        var world = await WorldAsync();
        var owner = world.Scope with { TeamId = "team-a" };
        var recipient = world.Scope with { TeamId = "team-b" };

        var borrowed = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock", scope: owner);
        await world.Indexing.IndexAsync(world.Authorization, owner, borrowed);
        await world.GrantAsync(borrowed, owner, recipient);

        var failures = new List<ExperienceGrantAccessFailure>();
        var audited = new PostgresExperienceEmbeddingIndex(
            world.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(
                new FailingAccessLog(), failures.Add, ExperienceGrantAuditingMode.Required));

        var result = await audited.SearchAsync(
            world.Authorization,
            new ExperienceVectorQuery(
                recipient,
                world.Generator.ModelId,
                TopicEmbeddingGenerator.VectorFor("refund stuck on a lock"),
                [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
                MinimumConfidence: 0.5,
                Limit: 50),
            CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Equal(ExperienceGrantAuditingMode.Required, Assert.Single(failures).Mode);
    }

    private sealed class FailingAccessLog : IExperienceGrantAccessLog
    {
        public Task RecordAsync(IReadOnlyList<ExperienceGrantAccess> accesses, CancellationToken cancellationToken) =>
            Task.FromException(new ExperienceStoreException("the ledger is down."));

        public Task<ExperienceGrantAccessQueryResult> QueryAsync(
            AuthorizationContext authorization,
            ExperienceGrantAccessQuery query,
            CancellationToken cancellationToken) =>
            Task.FromException<ExperienceGrantAccessQueryResult>(new ExperienceStoreException("the ledger is down."));
    }

    // ---------------------------------------------------------------- helpers

    [Fact]
    public async Task A_recipient_whose_only_comparable_population_arrives_through_a_grant_is_told_which_mismatch_it_hit()
    {
        // Without the grant branch in the probe, an empty search would look like "nothing similar" --
        // silently, and wrongly, because the scope does hold something it simply cannot compare.
        var world = await WorldAsync();
        var owner = world.Scope with { TeamId = "team-a" };
        var recipient = world.Scope with { TeamId = "team-b" };

        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock", scope: owner);
        await world.Indexing.IndexAsync(world.Authorization, owner, id);
        await world.GrantAsync(id, owner, recipient);

        // The one embedding the recipient can reach is from another model.
        await world.RestampEmbeddingAsync(id, "some-other-model", world.Generator.Dimension);

        var query = new ExperienceVectorQuery(
            recipient,
            world.Generator.ModelId,
            TopicEmbeddingGenerator.VectorFor("refund stuck on a lock"),
            [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
            MinimumConfidence: 0.5,
            Limit: 50);

        var result = await world.Index.SearchAsync(world.Authorization, query, CancellationToken.None);

        Assert.Equal(ExperienceVectorSearchOutcome.ModelMismatch, result.Outcome);
        Assert.Empty(result.Candidates);

        // And a scope with nothing at all still reports an ordinary empty answer, not a mismatch.
        var stranger = await world.Index.SearchAsync(
            world.Authorization,
            query with { Scope = world.Scope with { TeamId = "team-c" } },
            CancellationToken.None);

        Assert.Equal(ExperienceVectorSearchOutcome.Found, stranger.Outcome);
        Assert.Empty(stranger.Candidates);
    }

    private static ExperienceVectorQuery VectorQuery(TestWorld world, ReadOnlyMemory<float> vector) => new(
        world.Scope,
        "topic-embed-v1",
        vector,
        [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
        MinimumConfidence: 0.5,
        Limit: 50);

    private static NpgsqlDataSource Unreachable() =>
        NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=nobody;Password=nothing;Database=none;Timeout=3;Pooling=false");

    private async Task<TestWorld> WorldAsync() => await TestWorld.CreateAsync(fixture);

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task<Dictionary<string, string>> ColumnsAsync(string table)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT column_name, data_type FROM information_schema.columns " +
            $"WHERE table_schema = 'agent_experience' AND table_name = '{table}'");

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = reader.GetString(1).ToUpperInvariant();
        }

        return columns;
    }
}
