using AgentExperience.Core.Indexing;
using AgentExperience.Core.KeyManagement;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Story 17.5: with crypto-shredding on, an embedding's content hash is stored keyed under the record's key, never in
/// the clear, and the re-index scan translates it back so Core's "same model, same text" skip still works. Plaintext
/// mode is unchanged. Each test brings its own key store (or forces plaintext), so it proves the same thing in either
/// suite mode.
/// </summary>
[Collection(VectorsCollection.Name)]
public sealed class KeyedContentHashVectorsTests(VectorsFixture fixture)
{
    private const string Model = "topic-embed-v1";

    private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    [Fact]
    public async Task An_encrypted_write_stores_the_hash_keyed_under_the_record_key_and_never_the_plain_hash()
    {
        var world = World();
        var record = await world.CreateSealedAsync("refund-ticket");

        Assert.Equal(1, (await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope))).Indexed);

        var plain = PlainHash(record);
        var stored = await ReadStoredHashAsync(record.ExperienceId);
        Assert.StartsWith(KeyedContentHash.Prefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(plain, stored, StringComparison.Ordinal);
        Assert.Equal(0L, await CountRowsContainingAsync(record.ExperienceId, plain));

        using var key = await world.Encryption.ForReadAsync(record.ExperienceId, world.Scope, CancellationToken.None);
        Assert.Equal(KeyedContentHash.Key(key!, plain), stored);
        Assert.True(KeyedContentHash.Matches(key!, plain, stored));
    }

    [Fact]
    public async Task An_unchanged_record_is_skipped_without_a_provider_call_and_the_scan_reports_the_plain_hash_with_one_key_batch()
    {
        var world = World();
        var sealedRecord = await world.CreateSealedAsync("refund-ticket");
        var plaintext = await world.CreatePlaintextAsync("refund-plain");
        Assert.Equal(2, (await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope))).Indexed);
        var calls = world.Generator.Requests.Count;

        world.Keys.Reset();
        var scan = await world.Index.ScanAsync(world.Auth, new ExperienceIndexScan(world.Scope, Model, [ExperienceStatus.Validated], 0), CancellationToken.None);
        var stored = scan.Targets.ToDictionary(target => target.ExperienceId, target => target.Stored!.ContentHash);
        Assert.Equal(PlainHash(sealedRecord), stored[sealedRecord.ExperienceId]);
        Assert.Equal(PlainHash(plaintext), stored[plaintext.ExperienceId]);

        // Both rows' keys -- the sealed record's and the keyed hash's on the plaintext row -- in one call.
        Assert.Equal((1, 0), (world.Keys.BatchCalls, world.Keys.SingleCalls));

        var again = await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope));
        Assert.Equal(ExperienceReindexOutcome.Completed, again.Outcome);
        Assert.Equal((0, 2), (again.Indexed, again.Skipped));
        Assert.Equal(calls, world.Generator.Requests.Count);
    }

    [Fact]
    public async Task A_changed_summary_is_re_embedded_and_keyed_again()
    {
        var world = World();
        var record = await world.CreatePlaintextAsync("refund-ticket");
        await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope));
        var before = await ReadStoredHashAsync(record.ExperienceId);

        await using (var rewrite = fixture.OwnerDataSource.CreateCommand(
            "UPDATE agent_experience.experience_records " +
            "SET payload = jsonb_set(payload, '{reflection,lesson}', to_jsonb('Confirm the ledger entry afterwards'::text)) WHERE experience_id = @id"))
        {
            rewrite.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            Assert.Equal(1, await rewrite.ExecuteNonQueryAsync());
        }

        var calls = world.Generator.Requests.Count;
        var pass = await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope));
        Assert.Equal(1, pass.Indexed);
        Assert.True(world.Generator.Requests.Count > calls);

        var after = await ReadStoredHashAsync(record.ExperienceId);
        Assert.StartsWith(KeyedContentHash.Prefix, after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
        Assert.Equal(1, (await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope))).Skipped);
    }

    [Fact]
    public async Task A_hash_stored_in_the_clear_before_the_upgrade_is_re_embedded_once_then_keyed_and_skipped()
    {
        var world = World();
        var record = await world.CreateSealedAsync("refund-ticket");
        var plain = PlainHash(record);

        // As an earlier release, or a process in plaintext mode, stored it.
        var legacy = new PostgresExperienceEmbeddingIndex(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var written = await legacy.WriteAsync(
            world.Auth,
            new ExperienceIndexWrite(world.Scope, record.ExperienceId, new ExperienceEmbeddingDescriptor(Model, world.Generator.Dimension, plain, 0), TopicEmbeddingGenerator.VectorFor("refund")),
            CancellationToken.None);
        Assert.Equal(ExperienceIndexOutcome.Written, written.Outcome);
        Assert.Equal(plain, await ReadStoredHashAsync(record.ExperienceId));

        var scan = await world.Index.ScanAsync(world.Auth, new ExperienceIndexScan(world.Scope, Model, [ExperienceStatus.Validated], 0), CancellationToken.None);
        Assert.Equal(KeyedContentHash.Unconfirmed, Assert.Single(scan.Targets).Stored!.ContentHash);

        var first = await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope));
        Assert.Equal((1, 0), (first.Indexed, first.Skipped));
        Assert.StartsWith(KeyedContentHash.Prefix, await ReadStoredHashAsync(record.ExperienceId), StringComparison.Ordinal);

        var second = await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope));
        Assert.Equal((0, 1), (second.Indexed, second.Skipped));
    }

    [Fact]
    public async Task After_erasure_the_stored_hash_cannot_be_recomputed_from_a_guessed_summary_without_the_key()
    {
        var world = World();
        var record = await world.CreateSealedAsync("refund-ticket");
        await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope));
        var stored = await ReadStoredHashAsync(record.ExperienceId);

        // The crash window of an erasure: the key is gone, the row (as in any older copy) is still there.
        await world.KeyStore.DestroyKeyAsync(new ExperienceKeyReference(record.ExperienceId, world.Scope), CancellationToken.None);

        // Someone holding the copy guesses the summary exactly: neither the plain hash nor a keyed one under any key
        // they hold matches what is stored.
        var guess = PlainHash(record);
        Assert.NotEqual(guess, stored);
        var other = new ExperienceEncryption(new EnvelopeExperienceKeyStore(LocalExperienceKeyEncryptionKey.Generate("kek-other"), new InMemoryExperienceWrappedKeyRepository()));
        using (var otherKey = await other.ForWriteAsync(record.ExperienceId, world.Scope, CancellationToken.None))
        {
            Assert.NotEqual(stored, KeyedContentHash.Key(otherKey!, guess));
            Assert.False(KeyedContentHash.Matches(otherKey!, guess, stored));
        }

        // And the row is treated as erased: not scanned, not written again.
        var scan = await world.Index.ScanAsync(world.Auth, new ExperienceIndexScan(world.Scope, Model, [ExperienceStatus.Validated], 0), CancellationToken.None);
        Assert.Empty(scan.Targets);
        Assert.Equal(record.ExperienceId, scan.LastExaminedId);
        var write = await world.Index.WriteAsync(
            world.Auth,
            new ExperienceIndexWrite(world.Scope, record.ExperienceId, new ExperienceEmbeddingDescriptor(Model, world.Generator.Dimension, guess, 0), TopicEmbeddingGenerator.VectorFor("refund")),
            CancellationToken.None);
        Assert.Equal(ExperienceIndexOutcome.Missing, write.Outcome);
        Assert.Equal(stored, await ReadStoredHashAsync(record.ExperienceId));
    }

    [Fact]
    public async Task Plaintext_mode_stores_and_reports_the_plain_hash_exactly_as_before()
    {
        var scope = new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1");
        var auth = new AuthorizationContext(scope.TenantId, "host-principal", ["experience:write"], Stamp);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var index = new PostgresExperienceEmbeddingIndex(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var generator = new TopicEmbeddingGenerator();
        var indexing = new ExperienceIndexingService(index, generator);
        var record = Record(scope, "refund-ticket");
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);

        Assert.Equal(1, (await indexing.ReindexAsync(auth, new ReindexExperienceRequest(scope))).Indexed);

        var plain = PlainHash(record);
        Assert.Equal(plain, await ReadStoredHashAsync(record.ExperienceId));
        var scan = await index.ScanAsync(auth, new ExperienceIndexScan(scope, Model, [ExperienceStatus.Validated], 0), CancellationToken.None);
        Assert.Equal(plain, Assert.Single(scan.Targets).Stored!.ContentHash);
        Assert.Equal(1, (await indexing.ReindexAsync(auth, new ReindexExperienceRequest(scope))).Skipped);
    }

    [Fact]
    public void The_keyed_hash_has_a_known_answer_and_only_that_answer_matches()
    {
        // Computed independently: HKDF-SHA256 (no salt, info "aexp:embedding-content-hash:v1") of the data key 00..1f,
        // then HMAC-SHA256 of the UTF-8 plain hash under it.
        var plain = ExperienceEmbeddingDescriptor.ComputeContentHash("model", "summary");
        Assert.Equal("ec29a170c3a8767ff1486b8ea0a5e2f525fc509d84bc35944ed2d0992f65728e", plain);
        var dataKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        using var key = new RecordKey(new ExperienceKeyReference(Guid.NewGuid(), new Scope("t", "a", "p")), new ExperienceDataKey(dataKey));

        const string expected = "keyed:iKrEpGbw9pB9ljPjqM0UNLHFJkpi3WByd2mmXRhZqpA=";
        Assert.Equal(expected, KeyedContentHash.Key(key, plain));
        Assert.True(KeyedContentHash.Matches(key, plain, expected));

        Assert.False(KeyedContentHash.Matches(key, plain, "keyed:not base64!"));
        Assert.False(KeyedContentHash.Matches(key, plain, "keyed:" + Convert.ToBase64String(new byte[16])));
        Assert.False(KeyedContentHash.Matches(key, plain, expected[..^4] + "AAA="));
        Assert.False(KeyedContentHash.Matches(key, plain, KeyedContentHash.Unconfirmed));
        Assert.False(KeyedContentHash.Matches(key, plain, plain));
    }

    [Fact]
    public async Task A_plaintext_record_whose_key_is_destroyed_is_not_scanned_and_its_text_reaches_no_provider()
    {
        var world = World();
        var record = await world.CreatePlaintextAsync("refund-ticket");
        Assert.Equal(1, (await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope))).Indexed);
        await world.RewriteLessonAsync(fixture, record.ExperienceId, "Confirm the ledger entry afterwards");

        // The crash window of an erasure: the key is gone, the plaintext row and its embedding are still there.
        await world.KeyStore.DestroyKeyAsync(new ExperienceKeyReference(record.ExperienceId, world.Scope), CancellationToken.None);

        var scan = await world.Index.ScanAsync(world.Auth, new ExperienceIndexScan(world.Scope, Model, [ExperienceStatus.Validated], 0), CancellationToken.None);
        Assert.Empty(scan.Targets);
        Assert.Equal(record.ExperienceId, scan.LastExaminedId);

        var calls = world.Generator.Requests.Count;
        var pass = await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope));
        Assert.Equal(0, pass.Indexed);
        Assert.Equal(calls, world.Generator.Requests.Count);
    }

    [Fact]
    public async Task A_plaintext_record_with_a_keyed_hash_whose_key_the_store_never_held_does_not_fail_the_scan()
    {
        var world = World();
        var record = await world.CreatePlaintextAsync("refund-ticket");

        // Keyed by another deployment's key store: this one has never held the record's key.
        var foreign = new PostgresExperienceEmbeddingIndex(
            fixture.DataSource,
            encryption: new ExperienceEncryption(new EnvelopeExperienceKeyStore(LocalExperienceKeyEncryptionKey.Generate("kek-foreign"), new InMemoryExperienceWrappedKeyRepository())));
        Assert.Equal(1, (await new ExperienceIndexingService(foreign, world.Generator).ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope))).Indexed);
        Assert.StartsWith(KeyedContentHash.Prefix, await ReadStoredHashAsync(record.ExperienceId), StringComparison.Ordinal);

        var scan = await world.Index.ScanAsync(world.Auth, new ExperienceIndexScan(world.Scope, Model, [ExperienceStatus.Validated], 0), CancellationToken.None);
        Assert.Equal(KeyedContentHash.Unconfirmed, Assert.Single(scan.Targets).Stored!.ContentHash);
    }

    [Fact]
    public async Task A_write_for_an_unknown_id_or_another_scope_creates_no_key()
    {
        var world = World();
        var record = await world.CreatePlaintextAsync("refund-ticket");
        var sibling = world.Scope with { ProjectId = "project-2" };
        var descriptor = new ExperienceEmbeddingDescriptor(Model, world.Generator.Dimension, PlainHash(record), 0);
        var unknown = Guid.NewGuid();

        foreach (var (scope, id) in new[] { (world.Scope, unknown), (sibling, record.ExperienceId) })
        {
            var write = await world.Index.WriteAsync(world.Auth, new ExperienceIndexWrite(scope, id, descriptor, TopicEmbeddingGenerator.VectorFor("refund")), CancellationToken.None);
            Assert.Equal(ExperienceIndexOutcome.Missing, write.Outcome);
            var lookup = await world.KeyStore.GetKeyAsync(new ExperienceKeyReference(id, scope), CancellationToken.None);
            Assert.Equal(ExperienceKeyStatus.NotFound, lookup.Status);
        }

        Assert.Equal(0, world.Keys.CreateCalls);
        Assert.Equal(ExperienceKeyStatus.NotFound, (await world.KeyStore.GetKeyAsync(new ExperienceKeyReference(record.ExperienceId, world.Scope), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_write_for_a_plaintext_record_creates_exactly_one_key_the_record_stays_plaintext_and_erasure_destroys_it()
    {
        var world = World();
        var record = await world.CreatePlaintextAsync("refund-ticket");
        var reference = new ExperienceKeyReference(record.ExperienceId, world.Scope);
        Assert.Equal(ExperienceKeyStatus.NotFound, (await world.KeyStore.GetKeyAsync(reference, CancellationToken.None)).Status);

        Assert.Equal(1, (await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope))).Indexed);
        Assert.Equal(1, (await world.Indexing.ReindexAsync(world.Auth, new ReindexExperienceRequest(world.Scope))).Skipped);

        Assert.Equal(1, world.Keys.CreateCalls);
        var held = await world.KeyStore.GetKeyAsync(reference, CancellationToken.None);
        held.Key?.Dispose();
        Assert.Equal(ExperienceKeyStatus.Active, held.Status);
        Assert.StartsWith(KeyedContentHash.Prefix, await ReadStoredHashAsync(record.ExperienceId), StringComparison.Ordinal);

        await using (var version = fixture.OwnerDataSource.CreateCommand(
            "SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = @id"))
        {
            version.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            Assert.Equal(1, (int)(await version.ExecuteScalarAsync())!);
        }

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await world.SealedStore.DeleteAsync(world.Auth, world.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceKeyStatus.Destroyed, (await world.KeyStore.GetKeyAsync(reference, CancellationToken.None)).Status);
    }

    private static string PlainHash(ExperienceRecord record) =>
        ExperienceEmbeddingDescriptor.ComputeContentHash(Model, ExperienceRetrievalSummary.For(record.TaskId, record.TaskSummary, record.Reflection?.Lesson));

    private async Task<string> ReadStoredHashAsync(Guid experienceId)
    {
        await using var command = fixture.OwnerDataSource.CreateCommand(
            "SELECT content_hash FROM agent_experience.experience_embeddings WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>How many embedding rows of the record hold <paramref name="text"/> anywhere in their text form.</summary>
    private async Task<long> CountRowsContainingAsync(Guid experienceId, string text)
    {
        await using var command = fixture.OwnerDataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.experience_embeddings e WHERE e.experience_id = @id AND strpos(e::text, @text) > 0");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        command.Parameters.Add(new NpgsqlParameter<string>("text", NpgsqlDbType.Text) { TypedValue = text });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private EncryptedWorld World() => new(fixture);

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

    /// <summary>One isolated tenant in encrypted mode, over its own key store, with lookups counted.</summary>
    private sealed class EncryptedWorld
    {
        private readonly PostgresExperienceRecordStore _plainStore;

        public EncryptedWorld(VectorsFixture fixture)
        {
            KeyStore = new EnvelopeExperienceKeyStore(LocalExperienceKeyEncryptionKey.Generate("kek-1"), new InMemoryExperienceWrappedKeyRepository());
            Keys = new CountingKeyStore(KeyStore);
            Encryption = new ExperienceEncryption(Keys);
            Scope = new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1");
            Auth = new AuthorizationContext(Scope.TenantId, "host-principal", ["experience:write"], Stamp);
            SealedStore = new PostgresExperienceRecordStore(fixture.DataSource, encryption: Encryption);
            _plainStore = new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
            Index = new PostgresExperienceEmbeddingIndex(fixture.DataSource, encryption: Encryption);
            Indexing = new ExperienceIndexingService(Index, Generator);
        }

        public EnvelopeExperienceKeyStore KeyStore { get; }

        /// <summary>The record store in this world's encrypted mode: it seals what it writes, and destroys keys on erasure.</summary>
        public PostgresExperienceRecordStore SealedStore { get; }

        public CountingKeyStore Keys { get; }

        public ExperienceEncryption Encryption { get; }

        public Scope Scope { get; }

        public AuthorizationContext Auth { get; }

        public TopicEmbeddingGenerator Generator { get; } = new();

        public PostgresExperienceEmbeddingIndex Index { get; }

        public ExperienceIndexingService Indexing { get; }

        public Task<ExperienceRecord> CreateSealedAsync(string taskId) => CreateAsync(SealedStore, taskId);

        public Task<ExperienceRecord> CreatePlaintextAsync(string taskId) => CreateAsync(_plainStore, taskId);

        /// <summary>Rewrites a plaintext record's lesson in place, as the owner.</summary>
        public async Task RewriteLessonAsync(VectorsFixture fixture, Guid experienceId, string lesson)
        {
            await using var rewrite = fixture.OwnerDataSource.CreateCommand(
                "UPDATE agent_experience.experience_records " +
                "SET payload = jsonb_set(payload, '{reflection,lesson}', to_jsonb(@lesson::text)) WHERE experience_id = @id");
            rewrite.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
            rewrite.Parameters.Add(new NpgsqlParameter<string>("lesson", NpgsqlDbType.Text) { TypedValue = lesson });
            Assert.Equal(1, await rewrite.ExecuteNonQueryAsync());
        }

        private async Task<ExperienceRecord> CreateAsync(PostgresExperienceRecordStore store, string taskId)
        {
            var record = Record(Scope, taskId);
            Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Auth, record, CancellationToken.None)).Outcome);
            return record;
        }
    }

    /// <summary>Counts single and batch key lookups.</summary>
    private sealed class CountingKeyStore(IExperienceKeyStore inner) : IExperienceKeyStore
    {
        private int _batchCalls;
        private int _singleCalls;
        private int _createCalls;

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public int BatchCalls => Volatile.Read(ref _batchCalls);

        public int SingleCalls => Volatile.Read(ref _singleCalls);

        public void Reset()
        {
            Volatile.Write(ref _batchCalls, 0);
            Volatile.Write(ref _singleCalls, 0);
        }

        public ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCalls);
            return inner.CreateKeyAsync(reference, cancellationToken);
        }

        public ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _singleCalls);
            return inner.GetKeyAsync(reference, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ExperienceKeyLookup>> GetKeysAsync(
            IReadOnlyList<ExperienceKeyReference> references,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _batchCalls);
            return inner.GetKeysAsync(references, cancellationToken);
        }

        public ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.DestroyKeyAsync(reference, cancellationToken);
    }
}
