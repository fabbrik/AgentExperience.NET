using AgentExperience.Core.Retrieval;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// End-to-end hybrid retrieval over a real PostgreSQL 16 + pgvector: a record found by meaning rather
/// than by words, both channels merging into one ranked answer, and every documented fallback -- a
/// model mismatch, a dimension mismatch, and a provider that is down -- producing an explicit
/// text-only result that still carries the text candidates. All embeddings come from a deterministic
/// in-test generator, so none of this needs model credentials.
/// </summary>
[Collection(VectorsCollection.Name)]
public class HybridRetrievalIntegrationTests(VectorsFixture fixture)
{
    /// <summary>
    /// Task text that shares no word with the indexed record below, so the text channel cannot match
    /// it. "Chargeback" and "refund" are the same topic; "contention" and "lock" are the same topic.
    /// </summary>
    private const string SemanticTaskText = "chargeback contention";

    private NpgsqlDataSource DataSource => fixture.DataSource;

    [Fact]
    public async Task A_record_whose_words_do_not_overlap_the_task_text_is_still_found_through_the_vector_channel()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var id = await world.AddRecordAsync("billing-dispute", "Reimburse a blocked payment", "Release the stuck invoice");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        // The text channel on its own finds nothing: not one word is shared.
        var textOnly = await world.Retrieval(hybrid: false).RetrieveAsync(Request(world, SemanticTaskText));
        Assert.Equal(RetrievalOutcome.Completed, textOnly.Outcome);
        Assert.Empty(textOnly.Records);
        Assert.Equal(TextOnlyReason.NotConfigured, textOnly.VectorFallback!.Reason);

        // With the vector channel, the same request finds it.
        var hybrid = await world.Retrieval().RetrieveAsync(Request(world, SemanticTaskText));

        Assert.Equal(RetrievalOutcome.Completed, hybrid.Outcome);
        Assert.False(hybrid.TextOnly);
        Assert.Equal(id, Assert.Single(hybrid.Records).Record.ExperienceId);
    }

    [Fact]
    public async Task Both_channels_merge_into_one_ranked_answer_with_each_record_appearing_once()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var byWords = await world.AddRecordAsync("refund-ticket", "Resolve a chargeback contention case", "Check the ledger");
        var byMeaning = await world.AddRecordAsync("billing-dispute", "Reimburse a blocked payment", "Release the stuck invoice");

        foreach (var id in new[] { byWords, byMeaning })
        {
            await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);
        }

        var result = await world.Retrieval().RetrieveAsync(Request(world, SemanticTaskText));

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Equal(
            new[] { byWords, byMeaning }.Order(),
            result.Records.Select(r => r.Record.ExperienceId).Order());
        Assert.Equal(result.Records.Count, result.Records.Select(r => r.Record.ExperienceId).Distinct().Count());

        // Still exactly five ranking axes, whichever channel found a record.
        Assert.All(result.Records, ranked => Assert.Equal(5, ranked.Components.Count));
        Assert.All(result.Records, ranked => Assert.All(ranked.Components, c => Assert.InRange(c.Value, 0d, 1d)));
    }

    [Fact]
    public async Task Stored_embeddings_from_another_model_give_a_text_only_result_with_no_comparison_attempted()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        // Everything stored in this scope now claims a model the query will not be produced by.
        await world.RestampEmbeddingAsync(id, "some-other-model", 4);

        var result = await world.Retrieval().RetrieveAsync(Request(world, "refund ticket"));

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.ModelMismatch, result.VectorFallback!.Reason);
        Assert.Null(result.Failure);

        // The text channel still answered.
        Assert.Equal(id, Assert.Single(result.Records).Record.ExperienceId);
    }

    [Fact]
    public async Task Stored_embeddings_of_another_dimension_give_a_text_only_result()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        // Same model, wrong width. The width predicate keeps the incompatible row out of the distance
        // expression entirely, so nothing raises and nothing is compared.
        await world.RestampEmbeddingAsync(id, "topic-embed-v1", 9);

        var result = await world.Retrieval().RetrieveAsync(Request(world, "refund ticket"));

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.DimensionMismatch, result.VectorFallback!.Reason);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task A_query_model_that_nothing_was_indexed_under_is_a_model_mismatch_rather_than_an_empty_match()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        var result = await world
            .Retrieval(new FixedEmbeddingGenerator { ModelId = "a-different-model", Dimension = 4 })
            .RetrieveAsync(Request(world, "refund ticket"));

        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.ModelMismatch, result.VectorFallback!.Reason);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task A_provider_that_is_down_gives_a_text_only_result_and_the_text_candidates_still_come_back()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");
        await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        var outage = new InvalidOperationException("provider unavailable");
        var result = await world
            .Retrieval(new UnavailableGenerator(outage))
            .RetrieveAsync(Request(world, "refund ticket"));

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.ProviderUnavailable, result.VectorFallback!.Reason);
        Assert.Same(outage, result.VectorFallback.Exception);
        Assert.Equal(id, Assert.Single(result.Records).Record.ExperienceId);
    }

    [Fact]
    public async Task A_record_whose_indexing_failed_is_still_committed_durable_and_text_searchable()
    {
        var world = await TestWorld.CreateAsync(DataSource, new TopicEmbeddingGenerator { Throws = new InvalidOperationException("provider down") });
        var id = await world.AddRecordAsync("refund-ticket", "Resolve a refund ticket", "Release the lock");

        var indexing = await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);

        Assert.Equal(Core.Indexing.ExperienceIndexingOutcome.ProviderFailed, indexing.Outcome);
        Assert.True(indexing.IsRetryable);
        Assert.Equal(0L, await world.CountEmbeddingsAsync(id));

        // The canonical record never depended on the provider: it is still stored, still readable, and
        // still found by the text channel.
        var stored = await world.Store.GetAsync(world.Authorization, world.Scope, id, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, stored.Outcome);

        var retrieved = await world.Retrieval().RetrieveAsync(Request(world, "refund ticket"));
        Assert.Equal(RetrievalOutcome.Completed, retrieved.Outcome);
        Assert.Equal(id, Assert.Single(retrieved.Records).Record.ExperienceId);
        Assert.True(retrieved.TextOnly);
        Assert.Equal(TextOnlyReason.ProviderUnavailable, retrieved.VectorFallback!.Reason);
    }

    [Fact]
    public async Task Nothing_matching_either_channel_is_a_completed_empty_result()
    {
        var world = await TestWorld.CreateAsync(DataSource);

        var result = await world.Retrieval().RetrieveAsync(Request(world, "nothing at all like anything stored"));

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Null(result.Failure);
        Assert.False(result.TextOnly);
    }

    [Fact]
    public async Task Hybrid_retrieval_never_crosses_a_scope_boundary()
    {
        var mine = await TestWorld.CreateAsync(DataSource);
        var theirs = await TestWorld.CreateAsync(DataSource);

        var foreign = await theirs.AddRecordAsync("billing-dispute", "Reimburse a blocked payment", "Release the stuck invoice");
        await theirs.Indexing.IndexAsync(theirs.Authorization, theirs.Scope, foreign);

        var result = await mine.Retrieval().RetrieveAsync(Request(mine, SemanticTaskText));

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task A_revoked_and_a_superseded_record_are_unreachable_through_both_channels_even_with_their_vectors_left_in_place()
    {
        // Story 4.3's security suite, matrix row 9, over the real database: leaving eligibility makes a
        // record unreachable through the text channel *and* the vector channel. The lifecycle service is
        // built with no indexing hook on purpose, so both vectors survive the transitions -- which is
        // what proves that the status predicate in SQL, not de-indexing hygiene, is the boundary.
        var world = await TestWorld.CreateAsync(DataSource);
        var lifecycle = new AgentExperience.Core.Lifecycle.ExperienceLifecycleService(world.Store);

        var revoked = await world.AddRecordAsync("billing-dispute", "Reimburse a blocked payment", "Release the stuck invoice");
        var superseded = await world.AddRecordAsync("billing-dispute", "Reimburse a blocked payment", "Retry the stuck invoice");
        var replacement = await world.AddRecordAsync("billing-dispute", "Reimburse a blocked payment", "Void and reissue the invoice");
        foreach (var id in new[] { revoked, superseded, replacement })
        {
            await world.Indexing.IndexAsync(world.Authorization, world.Scope, id);
        }

        const string WordsTask = "Reimburse a blocked payment";
        Assert.Equal(3, (await world.Retrieval().RetrieveAsync(Request(world, WordsTask))).Records.Count);
        Assert.Equal(3, (await world.Retrieval().RetrieveAsync(Request(world, SemanticTaskText))).Records.Count);

        Assert.Equal(
            AgentExperience.Core.Lifecycle.LifecycleTransitionOutcome.Committed,
            (await lifecycle.CommitAsync(world.Authorization, Transition(world, revoked, ExperienceStatus.Revoked, null), CancellationToken.None)).Outcome);
        Assert.Equal(
            AgentExperience.Core.Lifecycle.LifecycleTransitionOutcome.Committed,
            (await lifecycle.CommitAsync(world.Authorization, Transition(world, superseded, ExperienceStatus.Superseded, replacement), CancellationToken.None)).Outcome);

        // Both vectors are still stored...
        Assert.Equal(1L, await world.CountEmbeddingsAsync(revoked));
        Assert.Equal(1L, await world.CountEmbeddingsAsync(superseded));

        // ...and neither record is reachable by words, by meaning, or by both at once.
        var byWordsOnly = await world.Retrieval(hybrid: false).RetrieveAsync(Request(world, WordsTask));
        var byMeaningOnly = await world.Retrieval().RetrieveAsync(Request(world, SemanticTaskText));
        var byBoth = await world.Retrieval().RetrieveAsync(Request(world, WordsTask));

        foreach (var result in new[] { byWordsOnly, byMeaningOnly, byBoth })
        {
            Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
            Assert.Equal(replacement, Assert.Single(result.Records).Record.ExperienceId);
        }

        Assert.False(byMeaningOnly.TextOnly);
    }

    [Fact]
    public async Task The_registration_extensions_resolve_a_hybrid_retrieval_service_from_a_real_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton(DataSource);
        services.AddSingleton<IExperienceEmbeddingGenerator>(new TopicEmbeddingGenerator());
        Vectors.DependencyInjection.AgentExperiencePostgresVectorsServiceCollectionExtensions
            .AddAgentExperiencePostgresEmbeddingIndex(services);
        Postgres.DependencyInjection.AgentExperiencePostgresServiceCollectionExtensions
            .AddAgentExperiencePostgresCandidateSource(services);
        Core.DependencyInjection.AgentExperienceCoreServiceCollectionExtensions.AddAgentExperienceRetrieval(services);
        Core.DependencyInjection.AgentExperienceCoreServiceCollectionExtensions.AddAgentExperienceIndexing(services);

        await using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresExperienceEmbeddingIndex>(provider.GetRequiredService<IExperienceEmbeddingIndex>());
        Assert.NotNull(provider.GetRequiredService<Core.Indexing.ExperienceIndexingService>());
        Assert.True(provider.GetRequiredService<ExperienceRetrievalService>().HybridEnabled);
    }

    [Fact]
    public async Task A_record_shared_by_a_grant_is_retrieved_through_both_channels_until_the_grant_is_revoked()
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var owner = world.Scope with { TeamId = "team-a" };
        var recipient = world.Scope with { TeamId = "team-b" };

        // One record found only by meaning, one found only by words: between them they exercise the
        // vector channel's predicate and the text channel's, from the recipient's scope.
        var byMeaning = await world.AddRecordAsync("billing-dispute", "Reimburse a blocked payment", "Release the stuck invoice", scope: owner);
        var byWords = await world.AddRecordAsync("refund-ticket", "Resolve a chargeback contention case", "Check the ledger", scope: owner);
        var ungranted = await world.AddRecordAsync("billing-dispute-2", "Reimburse a blocked payment", "Release the stuck invoice", scope: owner);

        foreach (var id in new[] { byMeaning, byWords, ungranted })
        {
            await world.Indexing.IndexAsync(world.Authorization, owner, id);
        }

        var request = new RetrieveExperienceRequest(world.Authorization, recipient, SemanticTaskText);

        // Before any grant, the recipient's scope holds nothing, however similar the text or the vector.
        var before = await world.Retrieval().RetrieveAsync(request);
        Assert.Equal(RetrievalOutcome.Completed, before.Outcome);
        Assert.Empty(before.Records);

        var meaningGrant = await world.GrantAsync(byMeaning, owner, recipient);
        await world.GrantAsync(byWords, owner, recipient);

        var shared = await world.Retrieval().RetrieveAsync(request);

        Assert.Equal(RetrievalOutcome.Completed, shared.Outcome);
        Assert.False(shared.TextOnly);
        // Both granted records, and only those: the ungranted sibling in the same owner scope stays out
        // even though it is indexed, eligible, and semantically identical to one that was shared.
        Assert.Equal(
            new[] { byMeaning, byWords }.Order(),
            shared.Records.Select(ranked => ranked.Record.ExperienceId).Order());
        // A shared record arrives as its owner's, carrying the owner's scope rather than the reader's.
        Assert.All(shared.Records, ranked => Assert.Equal(owner, ranked.Record.Scope));

        await world.RevokeAsync(meaningGrant.GrantId, owner);

        var afterRevocation = await world.Retrieval().RetrieveAsync(request);
        Assert.Equal(RetrievalOutcome.Completed, afterRevocation.Outcome);
        Assert.Equal(byWords, Assert.Single(afterRevocation.Records).Record.ExperienceId);
    }

    [Theory]
    [InlineData(ExperienceGrantDisclosure.LessonOnly)]
    [InlineData(ExperienceGrantDisclosure.LessonAndApproach)]
    public async Task A_vector_delivery_records_the_grants_disclosure_level_and_retrieval_carries_none(ExperienceGrantDisclosure level)
    {
        var world = await TestWorld.CreateAsync(DataSource);
        var owner = world.Scope with { TeamId = "team-a" };
        var recipient = world.Scope with { TeamId = "team-b" };

        // Found only by meaning, so the vector channel is the one that delivers it.
        var byMeaning = await world.AddRecordAsync("billing-dispute", "Reimburse a blocked payment", "Release the stuck invoice", scope: owner);
        await world.Indexing.IndexAsync(world.Authorization, owner, byMeaning);
        var grant = await world.GrantAsync(byMeaning, owner, recipient, level);

        var failures = new List<ExperienceGrantAccessFailure>();
        var log = new PostgresExperienceGrantAccessLog(world.DataSource);
        var audited = new PostgresExperienceEmbeddingIndex(
            world.DataSource, onGrantsUnavailable: null, auditing: new ExperienceGrantAuditing(log, failures.Add));

        var result = await audited.SearchAsync(
            world.Authorization,
            new ExperienceVectorQuery(
                recipient,
                world.Generator.ModelId,
                TopicEmbeddingGenerator.VectorFor(SemanticTaskText),
                [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
                MinimumConfidence: 0d,
                Limit: 50),
            CancellationToken.None);

        Assert.Equal(ExperienceVectorSearchOutcome.Found, result.Outcome);
        Assert.Equal(byMeaning, Assert.Single(result.Candidates).Record.ExperienceId);
        Assert.Empty(failures);

        var row = Assert.Single((await log.QueryAsync(
            world.Authorization, new ExperienceGrantAccessQuery(owner, byMeaning), CancellationToken.None)).Accesses);
        Assert.Equal(grant.GrantId, row.GrantId);
        Assert.Equal(level, row.Disclosure);

        // Retrieval matches; it does not deliver. Like the grant ID, the level is filled in only by the
        // pre-injection re-read.
        var retrieved = await world.Retrieval().RetrieveAsync(new RetrieveExperienceRequest(world.Authorization, recipient, SemanticTaskText));
        var ranked = Assert.Single(retrieved.Records);
        Assert.True(ranked.SharedByGrant);
        Assert.Null(ranked.GrantDisclosure);
    }

    private static RetrieveExperienceRequest Request(TestWorld world, string taskText) =>
        new(world.Authorization, world.Scope, taskText);

    private static AgentExperience.Core.Lifecycle.CommitLifecycleTransitionRequest Transition(
        TestWorld world,
        Guid experienceId,
        ExperienceStatus current,
        Guid? replacement) => new(
            EventId: Guid.NewGuid(),
            ExperienceId: experienceId,
            Scope: world.Scope,
            PriorStatus: ExperienceStatus.Validated,
            CurrentStatus: current,
            Reason: $"moved to {current}",
            Producer: "tests",
            OccurredAt: new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero),
            ExpectedRevision: 0,
            ReplacementExperienceId: replacement);

    /// <summary>A generator that is always down, for the provider-outage fallback.</summary>
    private sealed class UnavailableGenerator(Exception failure) : IExperienceEmbeddingGenerator
    {
        public string ModelId => "topic-embed-v1";

        public int Dimension => 4;

        public Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken) => throw failure;
    }
}
