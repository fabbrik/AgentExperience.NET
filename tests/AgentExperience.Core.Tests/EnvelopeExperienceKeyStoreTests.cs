using System.Security.Cryptography;
using AgentExperience.Core.KeyManagement;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 6.4: the reference envelope key store, its in-process KEK and its in-memory repository. The
/// obligations <see cref="IExperienceKeyStore"/> states in prose -- get-or-create, destroyed is forever, a
/// reference is scoped, a wrapped key is bound to its record -- each have a test here, as do rotation and
/// re-wrap.
/// </summary>
public sealed class EnvelopeExperienceKeyStoreTests
{
    private static readonly Scope ScopeA = new("tenant-1", "app-1", "project-1", "team-a");
    private static readonly Scope ScopeB = new("tenant-1", "app-1", "project-1", "team-b");

    private readonly LocalExperienceKeyEncryptionKey _kek = LocalExperienceKeyEncryptionKey.Generate("kek-1");
    private readonly InMemoryExperienceWrappedKeyRepository _repository = new();
    private readonly EnvelopeExperienceKeyStore _store;

    public EnvelopeExperienceKeyStoreTests()
    {
        _store = new EnvelopeExperienceKeyStore(_kek, _repository);
    }

    [Fact]
    public async Task Create_is_get_or_create_and_returns_the_same_key_every_time()
    {
        var reference = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);

        var first = await _store.CreateKeyAsync(reference, CancellationToken.None);
        var second = await _store.CreateKeyAsync(reference, CancellationToken.None);
        var read = await _store.GetKeyAsync(reference, CancellationToken.None);

        Assert.Equal(ExperienceKeyStatus.Active, first.Status);
        Assert.Equal(first.Key!.Span.ToArray(), second.Key!.Span.ToArray());
        Assert.Equal(first.Key.Span.ToArray(), read.Key!.Span.ToArray());
        Assert.Equal(ExperienceDataKey.SizeInBytes, first.Key.Span.Length);
    }

    [Fact]
    public async Task Concurrent_creates_of_one_record_agree_on_one_key()
    {
        var reference = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);

        var keys = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(
            async () => (await _store.CreateKeyAsync(reference, CancellationToken.None)).Key!.Span.ToArray())));

        Assert.Single(keys.Select(Convert.ToBase64String).Distinct());
    }

    [Fact]
    public async Task A_destroyed_reference_is_destroyed_for_ever_and_is_never_given_a_fresh_key()
    {
        var reference = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        await _store.CreateKeyAsync(reference, CancellationToken.None);

        await _store.DestroyKeyAsync(reference, CancellationToken.None);
        await _store.DestroyKeyAsync(reference, CancellationToken.None);

        Assert.Equal(ExperienceKeyLookup.Destroyed, await _store.GetKeyAsync(reference, CancellationToken.None));
        Assert.Equal(ExperienceKeyLookup.Destroyed, await _store.CreateKeyAsync(reference, CancellationToken.None));

        // The repository keeps a marker with no key material at all.
        var entry = await _repository.GetAsync(reference, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.True(entry.IsDestroyed);
        Assert.Null(entry.WrappedKey);
    }

    [Fact]
    public async Task Destroying_a_reference_that_never_had_a_key_still_forbids_one()
    {
        var reference = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        Assert.Equal(ExperienceKeyLookup.NotFound, await _store.GetKeyAsync(reference, CancellationToken.None));

        await _store.DestroyKeyAsync(reference, CancellationToken.None);

        Assert.Equal(ExperienceKeyLookup.Destroyed, await _store.CreateKeyAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task A_reference_is_scoped_so_one_ID_in_two_scopes_has_two_keys_and_destroying_one_leaves_the_other()
    {
        var id = Guid.NewGuid();
        var a = await _store.CreateKeyAsync(new ExperienceKeyReference(id, ScopeA), CancellationToken.None);
        Assert.Equal(ExperienceKeyLookup.NotFound, await _store.GetKeyAsync(new ExperienceKeyReference(id, ScopeB), CancellationToken.None));

        var b = await _store.CreateKeyAsync(new ExperienceKeyReference(id, ScopeB), CancellationToken.None);
        Assert.NotEqual(a.Key!.Span.ToArray(), b.Key!.Span.ToArray());

        await _store.DestroyKeyAsync(new ExperienceKeyReference(id, ScopeA), CancellationToken.None);
        Assert.Equal(ExperienceKeyStatus.Active, (await _store.GetKeyAsync(new ExperienceKeyReference(id, ScopeB), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_wrapped_key_moved_to_another_records_entry_cannot_be_unwrapped()
    {
        var owner = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        await _store.CreateKeyAsync(owner, CancellationToken.None);
        var wrapped = (await _repository.GetAsync(owner, CancellationToken.None))!.WrappedKey!;

        foreach (var thief in new[]
        {
            new ExperienceKeyReference(Guid.NewGuid(), ScopeA),
            new ExperienceKeyReference(owner.ExperienceId, ScopeB),
            new ExperienceKeyReference(owner.ExperienceId, ScopeA with { TeamId = null }),
        })
        {
            await _repository.AddIfAbsentAsync(thief, wrapped, CancellationToken.None);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => _store.GetKeyAsync(thief, CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public async Task A_tampered_wrapped_key_is_refused()
    {
        var reference = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        await _store.CreateKeyAsync(reference, CancellationToken.None);
        var wrapped = (await _repository.GetAsync(reference, CancellationToken.None))!.WrappedKey!;

        var bytes = wrapped.Ciphertext.ToArray();
        bytes[^1] ^= 0x01;
        Assert.True(await _repository.ReplaceAsync(reference, wrapped, wrapped with { Ciphertext = bytes }, CancellationToken.None));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => _store.GetKeyAsync(reference, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rotation_rewraps_every_key_in_bounded_batches_and_the_old_KEK_can_then_be_retired()
    {
        var references = Enumerable.Range(0, 5).Select(_ => new ExperienceKeyReference(Guid.NewGuid(), ScopeA)).ToArray();
        var before = new Dictionary<ExperienceKeyReference, byte[]>();
        foreach (var reference in references)
        {
            before[reference] = (await _store.CreateKeyAsync(reference, CancellationToken.None)).Key!.Span.ToArray();
        }

        // One key destroyed before the rotation: it must stay destroyed through it.
        await _store.DestroyKeyAsync(references[0], CancellationToken.None);

        // A key-store backup taken before the rotation: every wrapped key still under kek-1.
        var backup = await _repository.ListNotWrappedUnderAsync("no-such-kek", 100, CancellationToken.None);
        Assert.Equal(4, backup.Count);

        _kek.AddKey("kek-2", RandomNumberGenerator.GetBytes(32), makeCurrent: true);

        var passes = 0;
        var rewrapped = 0;
        ExperienceKeyRewrapResult pass;
        do
        {
            pass = await _store.RewrapAsync(batchSize: 3, CancellationToken.None);
            rewrapped += pass.RewrappedCount;
            passes++;
        }
        while (pass.MoreRemain);

        Assert.Equal(4, rewrapped);
        Assert.Equal(2, passes);
        Assert.Empty(await _repository.ListNotWrappedUnderAsync("kek-2", 100, CancellationToken.None));

        _kek.RetireKey("kek-1");

        // Every surviving key is unchanged -- a rotation re-wraps, it never re-keys -- and the destroyed one
        // stayed destroyed.
        foreach (var reference in references.Skip(1))
        {
            Assert.Equal(before[reference], (await _store.GetKeyAsync(reference, CancellationToken.None)).Key!.Span.ToArray());
        }

        Assert.Equal(ExperienceKeyLookup.Destroyed, await _store.GetKeyAsync(references[0], CancellationToken.None));

        // The backup is now useless: nothing can unwrap a key wrapped under the retired KEK.
        foreach (var entry in backup)
        {
            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => _kek.UnwrapAsync(entry.WrappedKey!, entry.Reference, CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public async Task A_rewrap_never_writes_a_destroyed_key_back()
    {
        var reference = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        await _store.CreateKeyAsync(reference, CancellationToken.None);
        var read = (await _repository.GetAsync(reference, CancellationToken.None))!.WrappedKey!;

        await _store.DestroyKeyAsync(reference, CancellationToken.None);

        // The compare-and-replace a racing rotation would make, after the destruction landed.
        Assert.False(await _repository.ReplaceAsync(reference, read, read with { KeyEncryptionKeyId = "kek-2" }, CancellationToken.None));
        Assert.Equal(ExperienceKeyLookup.Destroyed, await _store.GetKeyAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task Arguments_are_checked()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.CreateKeyAsync(new ExperienceKeyReference(Guid.Empty, ScopeA), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.RewrapAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.RewrapAsync(EnvelopeExperienceKeyStore.MaxRewrapBatchSize + 1, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => new LocalExperienceKeyEncryptionKey("k", new byte[16]));
        Assert.Throws<ArgumentException>(() => new LocalExperienceKeyEncryptionKey(" ", new byte[32]));
        Assert.Throws<InvalidOperationException>(() => _kek.RetireKey("kek-1"));
        Assert.Throws<ArgumentException>(() => _kek.AddKey("kek-1", new byte[32], makeCurrent: false));
        Assert.Throws<ArgumentException>(() => new ExperienceDataKey(new byte[31]));
    }

    [Fact]
    public void A_disposed_data_key_is_zeroed_and_unusable()
    {
        var key = new ExperienceDataKey(RandomNumberGenerator.GetBytes(ExperienceDataKey.SizeInBytes));
        key.Dispose();
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.Span.Length);
    }

    // ------------------------------------------------------------------ batches (story 16.3)

    [Fact]
    public async Task A_batch_answers_each_reference_in_order_exactly_as_a_single_lookup_does()
    {
        var active = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        var destroyed = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        var missing = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        var other = new ExperienceKeyReference(Guid.NewGuid(), ScopeB);
        var created = await _store.CreateKeyAsync(active, CancellationToken.None);
        var otherKey = await _store.CreateKeyAsync(other, CancellationToken.None);
        await _store.CreateKeyAsync(destroyed, CancellationToken.None);
        await _store.DestroyKeyAsync(destroyed, CancellationToken.None);

        var batch = await _store.GetKeysAsync([active, destroyed, missing, other, active], CancellationToken.None);

        Assert.Equal(
            [ExperienceKeyStatus.Active, ExperienceKeyStatus.Destroyed, ExperienceKeyStatus.NotFound, ExperienceKeyStatus.Active, ExperienceKeyStatus.Active],
            batch.Select(lookup => lookup.Status));
        Assert.Equal(created.Key!.Span.ToArray(), batch[0].Key!.Span.ToArray());
        Assert.Equal(otherKey.Key!.Span.ToArray(), batch[3].Key!.Span.ToArray());
        Assert.Equal(created.Key.Span.ToArray(), batch[4].Key!.Span.ToArray());
        Assert.Null(batch[1].Key);
        Assert.Null(batch[2].Key);
        Assert.Empty(await _store.GetKeysAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task A_batch_never_serves_a_key_destroyed_since_an_earlier_batch()
    {
        var reference = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        await _store.CreateKeyAsync(reference, CancellationToken.None);
        Assert.Equal(ExperienceKeyStatus.Active, (await _store.GetKeysAsync([reference, reference], CancellationToken.None))[0].Status);

        await _store.DestroyKeyAsync(reference, CancellationToken.None);

        Assert.All(await _store.GetKeysAsync([reference, reference], CancellationToken.None), lookup => Assert.Equal(ExperienceKeyLookup.Destroyed, lookup));
    }

    [Fact]
    public async Task A_batch_unwraps_concurrently_within_the_configured_bound()
    {
        var kek = new SlowKek(_kek, TimeSpan.FromMilliseconds(20));
        var store = new EnvelopeExperienceKeyStore(kek, _repository, maxConcurrentKeyLookups: 8);
        var references = Enumerable.Range(0, 50).Select(_ => new ExperienceKeyReference(Guid.NewGuid(), ScopeA)).ToArray();
        foreach (var reference in references)
        {
            await _store.CreateKeyAsync(reference, CancellationToken.None);
        }

        var batch = await store.GetKeysAsync(references, CancellationToken.None);

        Assert.All(batch, lookup => Assert.Equal(ExperienceKeyStatus.Active, lookup.Status));
        Assert.Equal(50, kek.Unwraps);
        Assert.InRange(kek.MaxInFlight, 2, 8);
        Assert.Equal(EnvelopeExperienceKeyStore.DefaultMaxConcurrentKeyLookups, _store.MaxConcurrentKeyLookups);
        Assert.Equal(16, EnvelopeExperienceKeyStore.DefaultMaxConcurrentKeyLookups);
    }

    [Fact]
    public async Task A_batch_that_fails_part_way_disposes_every_key_it_obtained_and_throws_the_failure()
    {
        var obtained = new System.Collections.Concurrent.ConcurrentBag<ExperienceDataKey>();
        var failing = new ExperienceKeyReference(Guid.NewGuid(), ScopeA);
        var references = Enumerable.Range(0, 20).Select(_ => new ExperienceKeyReference(Guid.NewGuid(), ScopeA)).Append(failing).ToArray();

        async ValueTask<ExperienceKeyLookup> Lookup(ExperienceKeyReference reference, CancellationToken token)
        {
            await Task.Yield();
            if (reference == failing)
            {
                throw new InvalidOperationException("The KMS is unreachable.");
            }

            var key = new ExperienceDataKey(RandomNumberGenerator.GetBytes(ExperienceDataKey.SizeInBytes));
            obtained.Add(key);
            return ExperienceKeyLookup.Active(key);
        }

        foreach (var concurrency in new[] { 1, 4 })
        {
            obtained.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => EnvelopeExperienceKeyStore.LookUpConcurrentlyAsync(references, concurrency, Lookup, CancellationToken.None).AsTask());
            Assert.NotEmpty(obtained);
            Assert.All(obtained, key => Assert.Throws<ObjectDisposedException>(() => key.Span.Length));
        }
    }

    [Fact]
    public async Task A_batch_through_a_failing_KEK_throws_its_failure()
    {
        var kek = new SlowKek(_kek, TimeSpan.Zero) { FailOn = Guid.NewGuid() };
        var store = new EnvelopeExperienceKeyStore(kek, _repository);
        var references = Enumerable.Range(0, 10).Select(_ => new ExperienceKeyReference(Guid.NewGuid(), ScopeA))
            .Append(new ExperienceKeyReference(kek.FailOn.Value, ScopeA)).ToArray();
        foreach (var reference in references)
        {
            await _store.CreateKeyAsync(reference, CancellationToken.None);
        }

        await Assert.ThrowsAsync<CryptographicException>(() => store.GetKeysAsync(references, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Batch_arguments_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvelopeExperienceKeyStore(_kek, _repository, maxConcurrentKeyLookups: 0));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _store.GetKeysAsync(null!, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.GetKeysAsync([new ExperienceKeyReference(Guid.Empty, ScopeA)], CancellationToken.None).AsTask());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var references = Enumerable.Range(0, 4).Select(_ => new ExperienceKeyReference(Guid.NewGuid(), ScopeA)).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.GetKeysAsync(references, cancelled.Token).AsTask());
    }

    /// <summary>A KEK with per-unwrap latency that records how many unwraps were in flight at once.</summary>
    private sealed class SlowKek(IExperienceKeyEncryptionKey inner, TimeSpan latency) : IExperienceKeyEncryptionKey
    {
        private int _inFlight;
        private int _maxInFlight;
        private int _unwraps;

        public Guid? FailOn { get; init; }

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public int Unwraps => Volatile.Read(ref _unwraps);

        public string CurrentKeyId => inner.CurrentKeyId;

        public ValueTask<ExperienceWrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            inner.WrapAsync(dataKey, reference, cancellationToken);

        public async ValueTask<byte[]> UnwrapAsync(ExperienceWrappedKey wrappedKey, ExperienceKeyReference reference, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            Interlocked.Increment(ref _unwraps);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                if (latency > TimeSpan.Zero)
                {
                    await Task.Delay(latency, cancellationToken);
                }

                if (reference.ExperienceId == FailOn)
                {
                    throw new CryptographicException("The KMS refused the unwrap.");
                }

                return await inner.UnwrapAsync(wrappedKey, reference, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
