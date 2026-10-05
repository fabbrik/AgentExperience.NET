using System.Collections.Concurrent;
using System.Diagnostics;
using AgentExperience.Core.KeyManagement;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 16.3: the retrieval reads -- the text channel and the eligibility re-read (<c>GetManyAsync</c>) --
/// snapshot their rows, close the data reader, commit and release the connection, and fetch every sealed row's key in one
/// <see cref="IExperienceKeyStore.GetKeysAsync"/> call. Each test brings its own key store, so it proves the
/// same thing whichever mode the rest of the suite runs in.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresBatchedKeyUnwrapTests(PostgresFixture fixture)
{
    private const string TaskText = "refund ticket";

    [Fact]
    public async Task Fifty_sealed_candidates_cost_one_batch_call_that_unwraps_concurrently()
    {
        var kek = new SlowKek(LocalExperienceKeyEncryptionKey.Generate("kek-1"), TimeSpan.FromMilliseconds(20));
        var repository = new InMemoryExperienceWrappedKeyRepository();
        var probe = new ProbingKeyStore(new EnvelopeExperienceKeyStore(kek, repository));
        var encryption = new ExperienceEncryption(probe);
        var (auth, scope, records) = await SeedAsync(encryption, 50);
        var search = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: encryption);
        probe.Reset();

        var result = await search.SearchAsync(auth, Query(scope), CancellationToken.None);

        Assert.Equal(50, result.Candidates.Count);
        Assert.Equal(records.Select(r => r.ExperienceId).Order(), result.Candidates.Select(c => c.Record.ExperienceId).Order());
        Assert.Equal(1, probe.BatchCalls);
        Assert.Equal(0, probe.SingleCalls);
        Assert.Equal(50, probe.Asked.Count);
        Assert.Equal(50, kek.Unwraps);

        // The envelope store unwraps concurrently, never more than its default bound at once.
        Assert.InRange(kek.MaxInFlight, 2, EnvelopeExperienceKeyStore.DefaultMaxConcurrentKeyLookups);
    }

    [Fact]
    public async Task Every_key_a_successful_read_obtained_is_disposed_once_it_has_decoded()
    {
        var probe = new ProbingKeyStore(Envelope());
        var encryption = new ExperienceEncryption(probe);
        var (auth, scope, records) = await SeedAsync(encryption, 4);
        var search = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: encryption);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);

        Assert.Equal(4, (await search.SearchAsync(auth, Query(scope), CancellationToken.None)).Candidates.Count);
        var many = await store.GetManyAsync(auth, scope, records.Select(r => r.ExperienceId).ToArray(), new ExperienceReadOptions(), CancellationToken.None);
        Assert.All(many.Results, r => Assert.Equal(ExperienceStoreOutcome.Found, r.Outcome));

        Assert.Equal(8, probe.Issued.Count);
        Assert.All(probe.Issued, key => Assert.Throws<ObjectDisposedException>(() => key.Span.Length));
    }

    [Theory]
    [InlineData(BatchShape.Short)]
    [InlineData(BatchShape.Long)]
    [InlineData(BatchShape.Null)]
    public async Task A_batch_answer_of_the_wrong_length_is_refused_and_every_key_in_it_is_disposed(BatchShape shape)
    {
        var probe = new ProbingKeyStore(Envelope());
        var encryption = new ExperienceEncryption(probe);
        var (auth, scope, records) = await SeedAsync(encryption, 3);
        probe.Shape = shape;
        var search = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: encryption);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);

        var searchFailure = await Assert.ThrowsAsync<ExperienceStoreException>(() => search.SearchAsync(auth, Query(scope), CancellationToken.None));
        Assert.Contains("different number of results", searchFailure.Message, StringComparison.Ordinal);
        var readFailure = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => store.GetManyAsync(auth, scope, records.Select(r => r.ExperienceId).ToArray(), new ExperienceReadOptions(), CancellationToken.None));
        Assert.Contains("different number of results", readFailure.Message, StringComparison.Ordinal);

        Assert.Equal(shape switch { BatchShape.Short => 4, BatchShape.Long => 8, _ => 0 }, probe.Issued.Count);
        Assert.All(probe.Issued, key => Assert.Throws<ObjectDisposedException>(() => key.Span.Length));
    }

    [Fact]
    public async Task A_key_store_without_the_batch_override_returns_exactly_what_the_envelope_store_returns()
    {
        var keyStore = Envelope();
        var envelope = new ExperienceEncryption(keyStore);
        var legacy = new ExperienceEncryption(new SingleKeyOnlyStore(keyStore));
        var (auth, scope, records) = await SeedAsync(envelope, 5);
        var plaintext = Record(scope);
        await new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext).CreateAsync(auth, plaintext, CancellationToken.None);
        var ids = records.Select(r => r.ExperienceId).Append(plaintext.ExperienceId).Append(Guid.NewGuid()).ToArray();

        var expectedSearch = await new PostgresExperienceCandidateSource(fixture.DataSource, encryption: envelope).SearchAsync(auth, Query(scope), CancellationToken.None);
        var actualSearch = await new PostgresExperienceCandidateSource(fixture.DataSource, encryption: legacy).SearchAsync(auth, Query(scope), CancellationToken.None);
        var expectedMany = await new PostgresExperienceRecordStore(fixture.DataSource, encryption: envelope).GetManyAsync(auth, scope, ids, new ExperienceReadOptions(), CancellationToken.None);
        var actualMany = await new PostgresExperienceRecordStore(fixture.DataSource, encryption: legacy).GetManyAsync(auth, scope, ids, new ExperienceReadOptions(), CancellationToken.None);

        Assert.Equal(6, expectedSearch.Candidates.Count);
        Assert.Equal(
            expectedSearch.Candidates.Select(c => (Canonical(c.Record), c.Relevance, c.SharedByGrant)),
            actualSearch.Candidates.Select(c => (Canonical(c.Record), c.Relevance, c.SharedByGrant)));
        Assert.Equal(
            expectedMany.Results.Select(r => (r.Outcome, r.Record is null ? null : Canonical(r.Record))),
            actualMany.Results.Select(r => (r.Outcome, r.Record is null ? null : Canonical(r.Record))));
    }

    [Fact]
    public async Task Plaintext_sealed_and_destroyed_rows_decode_as_before_and_plaintext_rows_never_reach_the_key_store()
    {
        var keyStore = Envelope();
        var probe = new ProbingKeyStore(keyStore);
        var encryption = new ExperienceEncryption(probe);
        var (auth, scope, records) = await SeedAsync(encryption, 2);
        var (live, destroyed) = (records[0], records[1]);
        var plaintext = Record(scope);
        await new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext).CreateAsync(auth, plaintext, CancellationToken.None);
        await keyStore.DestroyKeyAsync(new ExperienceKeyReference(destroyed.ExperienceId, scope), CancellationToken.None);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);
        var search = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: encryption);
        probe.Reset();

        var found = await search.SearchAsync(auth, Query(scope), CancellationToken.None);

        Assert.Equal(
            new[] { live.ExperienceId, plaintext.ExperienceId }.Order(),
            found.Candidates.Select(c => c.Record.ExperienceId).Order());
        Assert.Equal(live.TaskId, found.Candidates.Single(c => c.Record.ExperienceId == live.ExperienceId).Record.TaskId);
        Assert.Equal((1, 0), (probe.BatchCalls, probe.SingleCalls));
        Assert.Equal(new[] { live.ExperienceId, destroyed.ExperienceId }.Order(), probe.Asked.Select(r => r.ExperienceId).Order());

        probe.Reset();
        var many = await store.GetManyAsync(
            auth, scope, [plaintext.ExperienceId, destroyed.ExperienceId, live.ExperienceId, live.ExperienceId], new ExperienceReadOptions(), CancellationToken.None);

        Assert.Equal(
            [ExperienceStoreOutcome.Found, ExperienceStoreOutcome.Deleted, ExperienceStoreOutcome.Found, ExperienceStoreOutcome.Found],
            many.Results.Select(r => r.Outcome));
        Assert.Equal(1, probe.BatchCalls);
        Assert.Equal(new[] { live.ExperienceId, destroyed.ExperienceId }.Order(), probe.Asked.Select(r => r.ExperienceId).Order());
        Assert.Equal(Canonical(plaintext), Canonical(many.Results[0].Record!));
        Assert.Equal(Canonical((await store.GetAsync(auth, scope, live.ExperienceId, CancellationToken.None)).Record!), Canonical(many.Results[2].Record!));

        // A batch with no sealed row never calls the key store at all.
        probe.Reset();
        Assert.Equal(ExperienceStoreOutcome.Found, Assert.Single((await store.GetManyAsync(auth, scope, [plaintext.ExperienceId], new ExperienceReadOptions(), CancellationToken.None)).Results).Outcome);
        Assert.Equal((0, 0), (probe.BatchCalls, probe.SingleCalls));
    }

    [Fact]
    public async Task A_sealed_row_whose_key_the_store_never_held_fails_as_before_and_every_obtained_key_is_disposed()
    {
        var probe = new ProbingKeyStore(Envelope());
        var encryption = new ExperienceEncryption(probe);
        var (auth, scope, records) = await SeedAsync(encryption, 4);
        probe.ForceNotFound = records[2].ExperienceId;
        var search = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: encryption);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);

        var searchFailure = await Assert.ThrowsAsync<ExperienceStoreException>(() => search.SearchAsync(auth, Query(scope), CancellationToken.None));
        Assert.Contains("has no key in the configured key store", searchFailure.Message, StringComparison.Ordinal);
        var readFailure = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => store.GetManyAsync(auth, scope, records.Select(r => r.ExperienceId).ToArray(), new ExperienceReadOptions(), CancellationToken.None));
        Assert.Contains("has no key in the configured key store", readFailure.Message, StringComparison.Ordinal);

        Assert.Equal(6, probe.Issued.Count);
        Assert.All(probe.Issued, key => Assert.Throws<ObjectDisposedException>(() => key.Span.Length));
    }

    [Fact]
    public async Task A_key_store_that_throws_mid_batch_fails_the_read_as_before()
    {
        var probe = new ProbingKeyStore(Envelope());
        var encryption = new ExperienceEncryption(probe);
        var (auth, scope, records) = await SeedAsync(encryption, 3);
        probe.Throw = true;
        var search = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: encryption);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);

        var searchFailure = await Assert.ThrowsAsync<ExperienceStoreException>(() => search.SearchAsync(auth, Query(scope), CancellationToken.None));
        Assert.IsType<InvalidOperationException>(searchFailure.InnerException);
        var readFailure = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => store.GetManyAsync(auth, scope, records.Select(r => r.ExperienceId).ToArray(), new ExperienceReadOptions(), CancellationToken.None));
        Assert.IsType<InvalidOperationException>(readFailure.InnerException);

        // The single read keeps its own per-row path and fails the same way.
        var single = await Assert.ThrowsAsync<ExperienceStoreException>(() => store.GetAsync(auth, scope, records[0].ExperienceId, CancellationToken.None));
        Assert.IsType<InvalidOperationException>(single.InnerException);
    }

    [Fact]
    public async Task The_data_reader_is_closed_before_the_key_store_is_called()
    {
        using var tracing = new CommandTracing();
        var probe = new ProbingKeyStore(Envelope()) { ReaderProbe = tracing.OpenCommandsInCurrentTrace };
        var encryption = new ExperienceEncryption(probe);
        var (auth, scope, records) = await SeedAsync(encryption, 3);
        var search = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: encryption);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);

        using (tracing.StartTrace())
        {
            // The control: the single read still asks per row while its reader is open, and the probe sees it.
            probe.Reset();
            Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(auth, scope, records[0].ExperienceId, CancellationToken.None)).Outcome);
            Assert.Equal([1], probe.OpenCommandsAtCall);
        }

        using (tracing.StartTrace())
        {
            probe.Reset();
            Assert.Equal(3, (await search.SearchAsync(auth, Query(scope), CancellationToken.None)).Candidates.Count);
            Assert.Equal([0], probe.OpenCommandsAtCall);
        }

        using (tracing.StartTrace())
        {
            probe.Reset();
            var many = await store.GetManyAsync(auth, scope, records.Select(r => r.ExperienceId).ToArray(), new ExperienceReadOptions(), CancellationToken.None);
            Assert.All(many.Results, r => Assert.Equal(ExperienceStoreOutcome.Found, r.Outcome));
            Assert.Equal([0], probe.OpenCommandsAtCall);
        }
    }

    [Fact]
    public async Task A_key_destroyed_after_a_read_makes_the_next_read_treat_the_row_as_erased()
    {
        var keyStore = Envelope();
        var encryption = new ExperienceEncryption(keyStore);
        var (auth, scope, records) = await SeedAsync(encryption, 3);
        var search = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: encryption);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);
        var ids = records.Select(r => r.ExperienceId).ToArray();
        Assert.Equal(3, (await search.SearchAsync(auth, Query(scope), CancellationToken.None)).Candidates.Count);
        Assert.All((await store.GetManyAsync(auth, scope, ids, new ExperienceReadOptions(), CancellationToken.None)).Results, r => Assert.Equal(ExperienceStoreOutcome.Found, r.Outcome));

        await keyStore.DestroyKeyAsync(new ExperienceKeyReference(records[1].ExperienceId, scope), CancellationToken.None);

        Assert.DoesNotContain(
            (await search.SearchAsync(auth, Query(scope), CancellationToken.None)).Candidates,
            c => c.Record.ExperienceId == records[1].ExperienceId);
        Assert.Equal(
            [ExperienceStoreOutcome.Found, ExperienceStoreOutcome.Deleted, ExperienceStoreOutcome.Found],
            (await store.GetManyAsync(auth, scope, ids, new ExperienceReadOptions(), CancellationToken.None)).Results.Select(r => r.Outcome));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>How <see cref="ProbingKeyStore"/> mangles a batch answer.</summary>
    public enum BatchShape
    {
        /// <summary>Returned as the inner store answered.</summary>
        AsIs,

        /// <summary>One result short.</summary>
        Short,

        /// <summary>One surplus active key appended.</summary>
        Long,

        /// <summary>A null list.</summary>
        Null,
    }

    private static EnvelopeExperienceKeyStore Envelope() =>
        new(LocalExperienceKeyEncryptionKey.Generate("kek-1"), new InMemoryExperienceWrappedKeyRepository());

    private async Task<(AuthorizationContext Auth, Scope Scope, ExperienceRecord[] Records)> SeedAsync(ExperienceEncryption encryption, int count)
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: encryption);
        var records = Enumerable.Range(0, count).Select(_ => Record(scope)).ToArray();
        foreach (var record in records)
        {
            Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        }

        return (auth, scope, records);
    }

    private static ExperienceRecord Record(Scope scope) =>
        Minimal(scope, status: ExperienceStatus.Validated) with
        {
            TaskId = "task-" + Guid.NewGuid().ToString("N")[..8],
            TaskSummary = "Resolve the refund ticket for the customer",
            ReuseConfidence = 0.5,
        };

    private static ExperienceCandidateQuery Query(Scope scope) =>
        new(scope, TaskText, [ExperienceStatus.Validated], 0, Limit: 100);

    /// <summary>
    /// A key store wrapper that counts and probes its calls, and can make a reference answer NotFound or the
    /// batch throw. Its batch goes to the inner store's own <see cref="IExperienceKeyStore.GetKeysAsync"/>.
    /// </summary>
    private sealed class ProbingKeyStore(IExperienceKeyStore inner) : IExperienceKeyStore
    {
        private int _batchCalls;
        private int _singleCalls;

        public int BatchCalls => Volatile.Read(ref _batchCalls);

        public int SingleCalls => Volatile.Read(ref _singleCalls);

        public ConcurrentQueue<ExperienceKeyReference> Asked { get; private set; } = new();

        public ConcurrentQueue<int> OpenCommandsAtCall { get; private set; } = new();

        public ConcurrentQueue<ExperienceDataKey> Issued { get; } = new();

        public BatchShape Shape { get; set; }

        public Guid? ForceNotFound { get; set; }

        public bool Throw { get; set; }

        public Func<int>? ReaderProbe { get; init; }

        public void Reset()
        {
            _batchCalls = 0;
            _singleCalls = 0;
            Asked = new();
            OpenCommandsAtCall = new();
        }

        public ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.CreateKeyAsync(reference, cancellationToken);

        public async ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _singleCalls);
            Probe();
            Asked.Enqueue(reference);
            if (Throw)
            {
                throw new InvalidOperationException("The key store is unreachable.");
            }

            return Track(reference, await inner.GetKeyAsync(reference, cancellationToken));
        }

        public async ValueTask<IReadOnlyList<ExperienceKeyLookup>> GetKeysAsync(
            IReadOnlyList<ExperienceKeyReference> references,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _batchCalls);
            Probe();
            foreach (var reference in references)
            {
                Asked.Enqueue(reference);
            }

            if (Throw)
            {
                throw new InvalidOperationException("The key store is unreachable.");
            }

            var lookups = await inner.GetKeysAsync(references, cancellationToken);
            switch (Shape)
            {
                case BatchShape.Null:
                    foreach (var lookup in lookups)
                    {
                        lookup.Key?.Dispose();
                    }

                    return null!;
                case BatchShape.Short:
                    lookups[^1].Key?.Dispose();
                    return lookups.Take(lookups.Count - 1).Select((lookup, i) => Track(references[i], lookup)).ToArray();
                case BatchShape.Long:
                    var surplus = new ExperienceDataKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(ExperienceDataKey.SizeInBytes));
                    Issued.Enqueue(surplus);
                    return lookups.Select((lookup, i) => Track(references[i], lookup)).Append(ExperienceKeyLookup.Active(surplus)).ToArray();
                default:
                    return lookups.Select((lookup, i) => Track(references[i], lookup)).ToArray();
            }
        }

        public ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.DestroyKeyAsync(reference, cancellationToken);

        private void Probe()
        {
            if (ReaderProbe is { } probe)
            {
                OpenCommandsAtCall.Enqueue(probe());
            }
        }

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

    /// <summary>A key store written before story 16.3: it has only the single-key members, so it gets the port's default batch.</summary>
    private sealed class SingleKeyOnlyStore(IExperienceKeyStore inner) : IExperienceKeyStore
    {
        public ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.CreateKeyAsync(reference, cancellationToken);

        public ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.GetKeyAsync(reference, cancellationToken);

        public ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.DestroyKeyAsync(reference, cancellationToken);
    }

    /// <summary>A KEK with per-unwrap latency, standing in for a remote KMS, that records how many unwraps overlapped.</summary>
    private sealed class SlowKek(IExperienceKeyEncryptionKey inner, TimeSpan latency) : IExperienceKeyEncryptionKey
    {
        private int _unwraps;
        private int _inFlight;
        private int _maxInFlight;

        public int Unwraps => Volatile.Read(ref _unwraps);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public string CurrentKeyId => inner.CurrentKeyId;

        public ValueTask<ExperienceWrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.WrapAsync(dataKey, reference, cancellationToken);

        public async ValueTask<byte[]> UnwrapAsync(ExperienceWrappedKey wrappedKey, ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _unwraps);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(latency, cancellationToken);
                return await inner.UnwrapAsync(wrappedKey, reference, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>
    /// Watches Npgsql's command activities. A command's activity stays open until its data reader is closed,
    /// so a key-store call that sees one open in its own trace was made while a reader held the connection.
    /// </summary>
    private sealed class CommandTracing : IDisposable
    {
        private const string SourceName = "AgentExperience.Tests.BatchedKeyUnwrap";
        private static readonly ActivitySource Source = new(SourceName);
        private readonly ConcurrentDictionary<Activity, byte> _open = new();
        private readonly ActivityListener _listener;

        public CommandTracing()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is "Npgsql" or SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity =>
                {
                    if (activity.Source.Name == "Npgsql")
                    {
                        _open[activity] = 0;
                    }
                },
                ActivityStopped = activity => _open.TryRemove(activity, out _),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity StartTrace() => Source.StartActivity("batched-key-unwrap-test", ActivityKind.Internal)!;

        public int OpenCommandsInCurrentTrace()
        {
            var trace = Activity.Current?.TraceId;
            return _open.Keys.Count(activity => activity.TraceId == trace);
        }

        public void Dispose() => _listener.Dispose();
    }
}
