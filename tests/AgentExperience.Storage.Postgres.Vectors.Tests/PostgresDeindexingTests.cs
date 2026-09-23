using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Retrieval;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Story 3.2's de-indexing hook against the real stack: a record that leaves eligibility loses its
/// stored vector, the removal is scoped and idempotent, and a removal that cannot happen never fails
/// the transition that asked for it.
/// </summary>
[Collection(VectorsCollection.Name)]
public class PostgresDeindexingTests(VectorsFixture fixture)
{
    private NpgsqlDataSource DataSource => fixture.DataSource;

    [Theory]
    [InlineData(ExperienceStatus.Contested)]
    [InlineData(ExperienceStatus.Stale)]
    [InlineData(ExperienceStatus.Revoked)]
    public async Task Leaving_eligibility_removes_the_record_s_vector_and_the_vector_channel_stops_returning_it(
        ExperienceStatus exit)
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var lifecycle = new ExperienceLifecycleService(world.Store, world.Indexing);

        var id = await world.AddRecordAsync("deploy-rollback", "Roll back a bad deploy", "Drain traffic first.");
        Assert.Equal(ExperienceIndexingOutcome.Indexed, (await world.Indexing.IndexAsync(world.Authorization, world.Scope, id)).Outcome);
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));

        var result = await lifecycle.CommitAsync(
            world.Authorization,
            Transition(world, id, ExperienceStatus.Validated, exit, 0),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.Removed, result.Deindexing!.Outcome);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(id));

        // The canonical record is untouched apart from the transition itself: only derived data went.
        var stored = (await world.Store.GetAsync(world.Authorization, world.Scope, id, CancellationToken.None)).Record!;
        Assert.Equal(exit, stored.Status);
        Assert.Equal(1, stored.Revision);

        // And neither channel returns it any more -- the text channel by status, the vector channel
        // because there is no longer a row for it to match.
        var retrieved = await world.Retrieval().RetrieveAsync(
            new RetrieveExperienceRequest(world.Authorization, world.Scope, "Roll back a bad deploy"),
            CancellationToken.None);
        Assert.DoesNotContain(retrieved.Records, r => r.Record.ExperienceId == id);
    }

    [Fact]
    public async Task A_supersession_removes_the_superseded_vector_and_leaves_the_replacement_indexed()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var lifecycle = new ExperienceLifecycleService(world.Store, world.Indexing);

        var old = await world.AddRecordAsync("cache-warmup", "Warm the cache serially", "Serial warmup is safe.");
        var replacement = await world.AddRecordAsync("cache-warmup", "Warm the cache in parallel", "Parallel warmup is faster.");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, old);
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, replacement);

        var result = await lifecycle.CommitAsync(
            world.Authorization,
            Transition(world, old, ExperienceStatus.Validated, ExperienceStatus.Superseded, 0, replacement),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(replacement, result.Event!.ReplacementExperienceId);
        Assert.Equal(ExperienceDeindexingOutcome.Removed, result.Deindexing!.Outcome);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(old));
        Assert.Equal(1L, await world.CountEmbeddingsAsync(replacement));
    }

    [Fact]
    public async Task A_transition_that_keeps_the_record_eligible_keeps_its_vector()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var lifecycle = new ExperienceLifecycleService(world.Store, world.Indexing);

        var id = await world.AddRecordAsync("index-rebuild", "Rebuild the search index", "Rebuild off-peak.");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        var result = await lifecycle.CommitAsync(
            world.Authorization,
            Transition(world, id, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Null(result.Deindexing);
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));
    }

    [Fact]
    public async Task A_never_indexed_record_leaves_eligibility_without_the_removal_being_a_failure()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var lifecycle = new ExperienceLifecycleService(world.Store, world.Indexing);

        var id = await world.AddRecordAsync("never-embedded", "Never embedded", null);

        var result = await lifecycle.CommitAsync(
            world.Authorization,
            Transition(world, id, ExperienceStatus.Validated, ExperienceStatus.Stale, 0),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.NotIndexed, result.Deindexing!.Outcome);
        Assert.Null(result.Deindexing.Failure);
    }

    [Fact]
    public async Task Removal_is_idempotent_scoped_and_validated()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var other = await TestWorld.CreateAsync(DataSource);

        var id = await world.AddRecordAsync("retry-backoff", "Back off exponentially", "Cap the backoff.");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        // Another scope cannot remove this vector, and learns nothing from trying.
        var foreign = await other.Index.RemoveAsync(other.Authorization, other.Scope, id, CancellationToken.None);
        Assert.Equal(ExperienceIndexRemoveOutcome.NotIndexed, foreign.Outcome);
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));

        // A scope outside the host authorization is refused before any statement runs.
        var denied = await world.Index.RemoveAsync(other.Authorization, world.Scope, id, CancellationToken.None);
        Assert.Equal(ExperienceIndexRemoveOutcome.Denied, denied.Outcome);
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));

        var removed = await world.Index.RemoveAsync(world.Authorization, world.Scope, id, CancellationToken.None);
        var again = await world.Index.RemoveAsync(world.Authorization, world.Scope, id, CancellationToken.None);
        Assert.Equal(ExperienceIndexRemoveOutcome.Removed, removed.Outcome);
        Assert.Equal(ExperienceIndexRemoveOutcome.NotIndexed, again.Outcome);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(id));

        var malformed = await world.Index.RemoveAsync(world.Authorization, world.Scope, Guid.Empty, CancellationToken.None);
        Assert.Equal(ExperienceIndexRemoveOutcome.Invalid, malformed.Outcome);
        Assert.Equal("ExperienceId", Assert.Single(malformed.Errors).Path);
    }

    [Fact]
    public async Task A_deleted_record_takes_its_vector_with_it_and_removing_it_afterwards_is_a_no_op()
    {
        var world = await TestWorld.CreateAsync(DataSource);

        var id = await world.AddRecordAsync("orphaned", "Orphaned row", "Nothing left.");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));

        // 0004's ON DELETE CASCADE means an embedding can never outlive the record it describes, so
        // de-indexing never has to clean up after a deleted record -- and saying so afterwards is free.
        await world.DeleteRecordAsync(id);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(id));

        var removed = await world.Index.RemoveAsync(world.Authorization, world.Scope, id, CancellationToken.None);
        Assert.Equal(ExperienceIndexRemoveOutcome.NotIndexed, removed.Outcome);
    }

    [Fact]
    public async Task A_removal_against_an_unreachable_index_never_fails_the_transition()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var id = await world.AddRecordAsync("outage", "Index is down", "Nothing to see.");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        await using var unreachable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=nobody;Password=nothing;Database=none;Timeout=3;Pooling=false");
        var lifecycle = new ExperienceLifecycleService(
            world.Store,
            new ExperienceIndexingService(new PostgresExperienceEmbeddingIndex(unreachable), world.Generator));

        var result = await lifecycle.CommitAsync(
            world.Authorization,
            Transition(world, id, ExperienceStatus.Validated, ExperienceStatus.Contested, 0),
            CancellationToken.None);

        // The transition is durable; only the derived removal failed, and it says so as retryable.
        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.Failed, result.Deindexing!.Outcome);
        Assert.True(result.Deindexing.IsRetryable);
        Assert.Equal(
            ExperienceStatus.Contested,
            (await world.Store.GetAsync(world.Authorization, world.Scope, id, CancellationToken.None)).Record!.Status);

        // The vector really is still there, so a later pass has something to remove.
        Assert.Equal(1L, await world.CountEmbeddingsAsync(id));
        Assert.Equal(
            ExperienceDeindexingOutcome.Removed,
            (await world.Indexing.RemoveAsync(world.Authorization, world.Scope, id, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Erasing_a_record_removes_its_embedding_and_the_tombstone_can_never_be_indexed_again()
    {
        // Story 4.5's eighth erasure step, which lives in the base package's purge function and has to
        // reach a table the base package must not depend on. Here the vectors schema *is* applied, so
        // the to_regclass guard finds it and the embedding goes with the record's payload.
        var world = await TestWorld.CreateAsync(DataSource);

        var erased = await world.AddRecordAsync("token-refresh", "Refresh an expired token", "Refresh before expiry.");
        var kept = await world.AddRecordAsync("cache-stampede", "Avoid a cache stampede", "Lock the refill.");

        Assert.Equal(ExperienceIndexingOutcome.Indexed, (await world.Indexing.IndexAsync(world.Authorization, world.Scope, erased)).Outcome);
        Assert.Equal(ExperienceIndexingOutcome.Indexed, (await world.Indexing.IndexAsync(world.Authorization, world.Scope, kept)).Outcome);

        var deleted = await world.Store.DeleteAsync(world.Authorization, world.Scope, erased, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(erased));
        Assert.Equal(1L, await world.CountEmbeddingsAsync(kept));

        // A write that was already in flight when the erasure landed is Missing, never Stale: there is
        // no revision of an erased record that could ever be indexed, so there is nothing to retry.
        var late = await world.Indexing.IndexAsync(world.Authorization, world.Scope, erased);
        Assert.Equal(ExperienceIndexingOutcome.Missing, late.Outcome);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(erased));

        // A re-index pass does not offer it either: the scan reads the summary and the lesson, and a
        // tombstone has neither.
        var reindexed = await world.Indexing.ReindexAsync(
            world.Authorization, new ReindexExperienceRequest(world.Scope, null, Limit: 50), CancellationToken.None);
        Assert.DoesNotContain(erased, reindexed.Records.Select(result => result.ExperienceId));
        Assert.Contains(kept, reindexed.Records.Select(result => result.ExperienceId));

        // And neither retrieval channel returns it.
        var retrieved = await world.Retrieval().RetrieveAsync(
            new RetrieveExperienceRequest(world.Authorization, world.Scope, "Refresh an expired token"),
            CancellationToken.None);
        Assert.DoesNotContain(retrieved.Records, r => r.Record.ExperienceId == erased);
    }

    [Fact]
    public async Task A_write_naming_the_tombstone_s_own_revision_is_missing_rather_than_stale()
    {
        // The eligibility filters this table's reads already carry would hide a tombstone whatever the
        // erasure predicates said -- a tombstone's status is a literal no ExperienceStatus names -- so a
        // test that only searched would pass with the tombstone checks removed entirely. This one goes
        // through the two statements that have no status filter at all: the conditional write, and the
        // probe that explains why it wrote nothing. A write at the tombstone's *own* revision is the one
        // a stale-revision guard cannot refuse on its own.
        var world = await TestWorld.CreateAsync(DataSource);

        var id = await world.AddRecordAsync("token-refresh", "Refresh an expired token", "Refresh before expiry.");
        var deleted = await world.Store.DeleteAsync(world.Authorization, world.Scope, id, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);

        var written = await world.Index.WriteAsync(
            world.Authorization,
            new ExperienceIndexWrite(
                world.Scope,
                id,
                new ExperienceEmbeddingDescriptor("topic-embed-v1", 4, "hash", deleted.Revision),
                TopicEmbeddingGenerator.VectorFor("Refresh an expired token")),
            CancellationToken.None);

        // Missing, and specifically not Stale: Stale would name a revision to retry against, and there is
        // no revision of an erased record that could ever be indexed.
        Assert.Equal(ExperienceIndexOutcome.Missing, written.Outcome);
        Assert.Equal(0, written.CurrentRevision);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(id));
    }

    [Fact]
    public async Task An_embedding_written_while_a_record_is_being_erased_loses_instead_of_surviving_the_purge()
    {
        // The worst of the three concurrent-writer paths, because the row that survived would be a
        // searchable derivative of exactly the summary and lesson the erasure was asked to destroy. The
        // foreign key's own FOR KEY SHARE parks this writer against the purge and then releases it
        // straight onto the tombstone; only a locking clause in the write's own SELECT makes it re-check.
        var world = await TestWorld.CreateAsync(DataSource);

        var id = await world.AddRecordAsync("token-refresh", "Refresh an expired token", "Refresh before expiry.");

        await using var purging = await DataSource.OpenConnectionAsync();
        await using var transaction = await purging.BeginTransactionAsync();

        await using (var purge = new NpgsqlCommand(
            "SELECT purge_outcome FROM agent_experience.purge_experience_record(" +
            "@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())",
            purging,
            transaction))
        {
            purge.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
            purge.Parameters.Add(new NpgsqlParameter("tenant", world.Scope.TenantId));
            Assert.Equal("Deleted", await purge.ExecuteScalarAsync());
        }

        // Issued while the purge holds the record row, so it parks rather than deciding against a
        // snapshot the purge is about to invalidate.
        var writing = world.Index.WriteAsync(
            world.Authorization,
            new ExperienceIndexWrite(
                world.Scope,
                id,
                new ExperienceEmbeddingDescriptor("topic-embed-v1", 4, "hash", 0),
                TopicEmbeddingGenerator.VectorFor("Refresh an expired token")),
            CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await transaction.CommitAsync();

        Assert.Equal(ExperienceIndexOutcome.Missing, (await writing).Outcome);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(id));
    }

    private static CommitLifecycleTransitionRequest Transition(
        TestWorld world,
        Guid experienceId,
        ExperienceStatus prior,
        ExperienceStatus current,
        long expectedRevision,
        Guid? replacement = null) => new(
            EventId: Guid.NewGuid(),
            ExperienceId: experienceId,
            Scope: world.Scope,
            PriorStatus: prior,
            CurrentStatus: current,
            Reason: $"moved to {current}",
            Producer: "tests",
            OccurredAt: new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero),
            ExpectedRevision: expectedRevision,
            ReplacementExperienceId: replacement);
}
