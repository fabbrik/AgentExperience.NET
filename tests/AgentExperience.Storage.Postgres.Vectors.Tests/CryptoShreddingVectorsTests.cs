using AgentExperience.Core.Indexing;
using AgentExperience.Core.KeyManagement;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Story 6.4 on the vector channel: a sealed record is embedded from its opened summary -- exactly the summary
/// its plaintext twin produces -- and a record whose key was destroyed is never scanned, embedded or returned
/// again, even before its tombstone is written. Each test brings its own key store, so it proves the same
/// thing in either suite mode.
/// </summary>
[Collection(VectorsCollection.Name)]
public sealed class CryptoShreddingVectorsTests(VectorsFixture fixture)
{
    private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    [Fact]
    public async Task A_sealed_record_is_embedded_from_the_same_summary_as_its_plaintext_twin_and_a_shredded_one_never_again()
    {
        var keyStore = new EnvelopeExperienceKeyStore(LocalExperienceKeyEncryptionKey.Generate("kek-1"), new InMemoryExperienceWrappedKeyRepository());
        var encryption = new ExperienceEncryption(keyStore);
        var scope = new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1");
        var auth = new AuthorizationContext(scope.TenantId, "host-principal", ["experience:write"], Stamp);
        var sealedStore = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);
        var plainStore = new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var index = new PostgresExperienceEmbeddingIndex(fixture.DataSource, encryption: encryption);
        var generator = new TopicEmbeddingGenerator();
        var indexing = new ExperienceIndexingService(index, generator);

        var sealedRecord = Record(scope, "refund-ticket");
        var twin = sealedRecord with { ExperienceId = Guid.NewGuid() };
        var doomed = Record(scope, "refund-dispute");
        Assert.Equal(ExperienceStoreOutcome.Created, (await sealedStore.CreateAsync(auth, sealedRecord, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Created, (await plainStore.CreateAsync(auth, twin, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Created, (await sealedStore.CreateAsync(auth, doomed, CancellationToken.None)).Outcome);

        var scan = await index.ScanAsync(auth, Scan(scope), CancellationToken.None);
        var targets = scan.Targets.ToDictionary(target => target.ExperienceId);
        Assert.Equal(3, targets.Count);
        Assert.Equal(targets[twin.ExperienceId].Summary, targets[sealedRecord.ExperienceId].Summary);
        Assert.Contains("refund-ticket", targets[sealedRecord.ExperienceId].Summary, StringComparison.Ordinal);

        Assert.Equal(3, (await indexing.ReindexAsync(auth, new ReindexExperienceRequest(scope))).Indexed);

        // The crash window: the key is gone, the tombstone is not written yet. The record is erased as far as
        // the vector channel is concerned too -- not scanned, not re-embedded, not returned -- and the scan's
        // cursor still moves past it.
        await keyStore.DestroyKeyAsync(new ExperienceKeyReference(doomed.ExperienceId, scope), CancellationToken.None);

        // A vector derived from its text cannot be written back either.
        var stored = targets[doomed.ExperienceId];
        var write = await index.WriteAsync(
            auth,
            new ExperienceIndexWrite(scope, doomed.ExperienceId, new ExperienceEmbeddingDescriptor(generator.ModelId, generator.Dimension, "hash", stored.SourceRevision), TopicEmbeddingGenerator.VectorFor("refund")),
            CancellationToken.None);
        Assert.Equal(ExperienceIndexOutcome.Missing, write.Outcome);

        var after = await index.ScanAsync(auth, Scan(scope), CancellationToken.None);
        Assert.DoesNotContain(after.Targets, target => target.ExperienceId == doomed.ExperienceId);
        Assert.Equal(2, after.Targets.Count);
        var last = await index.ScanAsync(auth, Scan(scope) with { ExperienceIds = [doomed.ExperienceId] }, CancellationToken.None);
        Assert.Empty(last.Targets);
        Assert.Equal(doomed.ExperienceId, last.LastExaminedId);

        var search = await index.SearchAsync(
            auth,
            new ExperienceVectorQuery(scope, generator.ModelId, TopicEmbeddingGenerator.VectorFor("refund"), [ExperienceStatus.Validated], 0),
            CancellationToken.None);
        Assert.Equal(ExperienceVectorSearchOutcome.Found, search.Outcome);
        Assert.DoesNotContain(search.Candidates, candidate => candidate.Record.ExperienceId == doomed.ExperienceId);
        Assert.Equal(2, search.Candidates.Count);
    }

    [Fact]
    public async Task An_excluding_vector_search_leaves_out_a_sealed_record_stored_without_its_authorship_flag_until_the_backfill_writes_it()
    {
        // Story 17.1: unknown authorship fails closed on the vector channel too, in the search and in its compatibility
        // probe, and the owner's backfill restores the deterministic record.
        var encryption = new ExperienceEncryption(new EnvelopeExperienceKeyStore(LocalExperienceKeyEncryptionKey.Generate("kek-1"), new InMemoryExperienceWrappedKeyRepository()));
        var scope = new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1");
        var auth = new AuthorizationContext(scope.TenantId, "host-principal", ["experience:write"], Stamp);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);
        var index = new PostgresExperienceEmbeddingIndex(fixture.DataSource, encryption: encryption);
        var generator = new TopicEmbeddingGenerator();
        var indexing = new ExperienceIndexingService(index, generator);

        var flagged = Record(scope, "refund-ticket");
        var unflagged = Record(scope, "refund-dispute");
        foreach (var record in new[] { flagged, unflagged })
        {
            Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        }

        Assert.Equal(2, (await indexing.ReindexAsync(auth, new ReindexExperienceRequest(scope))).Indexed);

        // As a row sealed before 0021, or by an instance on an earlier build: the flag is unknown.
        await using (var unset = fixture.OwnerDataSource.CreateCommand(
            $"UPDATE agent_experience.experience_records SET reflection_model_authored = NULL WHERE experience_id = '{unflagged.ExperienceId}'"))
        {
            Assert.Equal(1, await unset.ExecuteNonQueryAsync());
        }

        var query = new ExperienceVectorQuery(scope, generator.ModelId, TopicEmbeddingGenerator.VectorFor("refund"), [ExperienceStatus.Validated], 0);
        var excluding = await index.SearchAsync(auth, query with { ExcludeModelAuthored = true }, CancellationToken.None);
        var including = await index.SearchAsync(auth, query, CancellationToken.None);

        Assert.Equal([flagged.ExperienceId], excluding.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.Equal(new[] { flagged.ExperienceId, unflagged.ExperienceId }.Order(), including.Candidates.Select(candidate => candidate.Record.ExperienceId).Order());

        // The probe sees what the search saw: with only the unknown record's embedding at hand, no mismatch is reported.
        await using (var unsetBoth = fixture.OwnerDataSource.CreateCommand(
            $"UPDATE agent_experience.experience_records SET reflection_model_authored = NULL WHERE experience_id = '{flagged.ExperienceId}'"))
        {
            Assert.Equal(1, await unsetBoth.ExecuteNonQueryAsync());
        }

        var wider = new float[] { 1f, 0f, 0f, 0f, 0f };
        var probed = await index.SearchAsync(auth, query with { Vector = wider, ExcludeModelAuthored = true }, CancellationToken.None);
        Assert.Equal(ExperienceVectorSearchOutcome.Found, probed.Outcome);
        Assert.Empty(probed.Candidates);
        Assert.Equal(ExperienceVectorSearchOutcome.DimensionMismatch, (await index.SearchAsync(auth, query with { Vector = wider }, CancellationToken.None)).Outcome);

        var backfill = await new PostgresExperienceRecordStore(fixture.OwnerDataSource, encryption: encryption)
            .BackfillSealedAuthorshipAsync(auth, scope, 10, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, backfill.Outcome);
        Assert.Equal(2, backfill.SetCount);

        var restored = await index.SearchAsync(auth, query with { ExcludeModelAuthored = true }, CancellationToken.None);
        Assert.Equal(new[] { flagged.ExperienceId, unflagged.ExperienceId }.Order(), restored.Candidates.Select(candidate => candidate.Record.ExperienceId).Order());
    }

    private static ExperienceIndexScan Scan(Scope scope) =>
        new(scope, "topic-embed-v1", [ExperienceStatus.Validated], 0);

    private static ExperienceRecord Record(Scope scope, string taskId) => new(
        ExperienceId: Guid.NewGuid(),
        SourceRunId: Guid.NewGuid(),
        Scope: scope,
        TaskId: taskId,
        TaskSummary: "Resolve a refund ticket",
        Attempts: [],
        Outcome: new Outcome(TaskVerificationStatus.Verified, [], "checks passed", Stamp),
        CompletionScore: 1,
        Reflection: new Reflection(
            Guid.NewGuid(), Guid.NewGuid(), "Release the payment lock first", [], [], [], [], null, [],
            TaskVerificationStatus.Verified, 1, "v1", "tests", Stamp),
        Environment: new EnvironmentFingerprint("worker-01", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
        Provenance: new Provenance("tests", null, Stamp, null),
        Status: ExperienceStatus.Validated,
        ReuseConfidence: 0.8,
        SupportingValidations: 0,
        Contradictions: 0,
        Revision: 0,
        CreatedAt: Stamp,
        UpdatedAt: Stamp);
}
