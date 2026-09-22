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
