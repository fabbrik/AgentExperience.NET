using System.Collections.Concurrent;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.KeyManagement;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Story 16.3 on the vector channel: the search snapshots its rows, closes the reader, and fetches every sealed
/// row's key in one <see cref="IExperienceKeyStore.GetKeysAsync"/> call; plaintext rows never reach the key
/// store, a destroyed key still reads as erased, and a key the store never held still fails the read. Each test
/// brings its own key store, so it proves the same thing in either suite mode.
/// </summary>
[Collection(VectorsCollection.Name)]
public sealed class BatchedKeyUnwrapVectorsTests(VectorsFixture fixture)
{
    private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    [Fact]
    public async Task A_vector_search_fetches_every_sealed_key_in_one_batch_and_answers_exactly_as_before()
    {
        var keyStore = new EnvelopeExperienceKeyStore(LocalExperienceKeyEncryptionKey.Generate("kek-1"), new InMemoryExperienceWrappedKeyRepository());
        var probe = new CountingKeyStore(keyStore);
        var encryption = new ExperienceEncryption(probe);
        var scope = new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1");
        var auth = new AuthorizationContext(scope.TenantId, "host-principal", ["experience:write"], Stamp);
        var sealedStore = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);
        var plainStore = new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var index = new PostgresExperienceEmbeddingIndex(fixture.DataSource, encryption: encryption);
        var generator = new TopicEmbeddingGenerator();

        var sealedRecords = Enumerable.Range(0, 3).Select(i => Record(scope, $"refund-ticket-{i}")).ToArray();
        var plaintext = Record(scope, "refund-plain");
        var doomed = sealedRecords[2];
        foreach (var record in sealedRecords)
        {
            Assert.Equal(ExperienceStoreOutcome.Created, (await sealedStore.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        }

        Assert.Equal(ExperienceStoreOutcome.Created, (await plainStore.CreateAsync(auth, plaintext, CancellationToken.None)).Outcome);
        Assert.Equal(4, (await new ExperienceIndexingService(index, generator).ReindexAsync(auth, new ReindexExperienceRequest(scope))).Indexed);

        var query = new ExperienceVectorQuery(scope, generator.ModelId, TopicEmbeddingGenerator.VectorFor("refund"), [ExperienceStatus.Validated], 0);
        probe.Reset();
        var before = await index.SearchAsync(auth, query, CancellationToken.None);
        Assert.Equal(4, before.Candidates.Count);
        Assert.Equal((1, 0), (probe.BatchCalls, probe.SingleCalls));
        Assert.Equal(sealedRecords.Select(r => r.ExperienceId).Order(), probe.Asked.Select(r => r.ExperienceId).Order());
        Assert.Equal(
            sealedRecords.Select(r => r.TaskId).Append(plaintext.TaskId).Order(),
            before.Candidates.Select(c => c.Record.TaskId).Order());

        // Destroyed after a read: the next search treats the row as erased.
        await keyStore.DestroyKeyAsync(new ExperienceKeyReference(doomed.ExperienceId, scope), CancellationToken.None);
        probe.Reset();
        var after = await index.SearchAsync(auth, query, CancellationToken.None);
        Assert.Equal(ExperienceVectorSearchOutcome.Found, after.Outcome);
        Assert.Equal(3, after.Candidates.Count);
        Assert.DoesNotContain(after.Candidates, c => c.Record.ExperienceId == doomed.ExperienceId);
        Assert.Equal(1, probe.BatchCalls);

        // A key the store never held is still a configuration failure, never an erasure; obtained keys are disposed.
        probe.ForceNotFound = sealedRecords[0].ExperienceId;
        var failure = await Assert.ThrowsAsync<ExperienceStoreException>(() => index.SearchAsync(auth, query, CancellationToken.None));
        Assert.Contains("has no key in the configured key store", failure.Message, StringComparison.Ordinal);
        Assert.All(probe.Issued, key => Assert.Throws<ObjectDisposedException>(() => key.Span.Length));

        // A key store that throws fails the search as before.
        probe.ForceNotFound = null;
        probe.Throw = true;
        var thrown = await Assert.ThrowsAsync<ExperienceStoreException>(() => index.SearchAsync(auth, query, CancellationToken.None));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
    }

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

    /// <summary>Counts single and batch lookups, and can make one reference answer NotFound or every lookup throw.</summary>
    private sealed class CountingKeyStore(IExperienceKeyStore inner) : IExperienceKeyStore
    {
        private int _batchCalls;
        private int _singleCalls;

        public int BatchCalls => Volatile.Read(ref _batchCalls);

        public int SingleCalls => Volatile.Read(ref _singleCalls);

        public ConcurrentQueue<ExperienceKeyReference> Asked { get; private set; } = new();

        public ConcurrentQueue<ExperienceDataKey> Issued { get; } = new();

        public Guid? ForceNotFound { get; set; }

        public bool Throw { get; set; }

        public void Reset()
        {
            _batchCalls = 0;
            _singleCalls = 0;
            Asked = new();
        }

        public ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.CreateKeyAsync(reference, cancellationToken);

        public async ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _singleCalls);
            Asked.Enqueue(reference);
            return Throw
                ? throw new InvalidOperationException("The key store is unreachable.")
                : Track(reference, await inner.GetKeyAsync(reference, cancellationToken));
        }

        public async ValueTask<IReadOnlyList<ExperienceKeyLookup>> GetKeysAsync(
            IReadOnlyList<ExperienceKeyReference> references,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _batchCalls);
            foreach (var reference in references)
            {
                Asked.Enqueue(reference);
            }

            if (Throw)
            {
                throw new InvalidOperationException("The key store is unreachable.");
            }

            var lookups = await inner.GetKeysAsync(references, cancellationToken);
            return lookups.Select((lookup, i) => Track(references[i], lookup)).ToArray();
        }

        public ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.DestroyKeyAsync(reference, cancellationToken);

        private ExperienceKeyLookup Track(ExperienceKeyReference reference, ExperienceKeyLookup lookup)
        {
            if (reference.ExperienceId == ForceNotFound)
            {
                lookup.Key?.Dispose();
                return ExperienceKeyLookup.NotFound;
            }

            if (lookup.Key is { } key)
            {
                Issued.Enqueue(key);
            }

            return lookup;
        }
    }
}
