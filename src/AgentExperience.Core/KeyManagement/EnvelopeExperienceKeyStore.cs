using System.Security.Cryptography;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.KeyManagement;

/// <summary>
/// An <see cref="IExperienceKeyStore"/> built by envelope encryption: each record gets a fresh random 256-bit
/// data key, the data key is stored only <em>wrapped</em> by the host's key-encryption key
/// (<see cref="IExperienceKeyEncryptionKey"/>, typically a KMS), and the wrapped keys live in an
/// <see cref="IExperienceWrappedKeyRepository"/> outside the database's backup domain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Destroying a key</b> removes its wrapped copy from the repository and leaves a marker with no key
/// material, so the reference answers <see cref="ExperienceKeyStatus.Destroyed"/> for ever. What still holds
/// the data key afterwards is exactly: a repository backup taken before the destruction, while the KEK that
/// wrapped it is still unwrappable. Retiring a KEK version after <see cref="RewrapAsync"/> closes that too.
/// </para>
/// <para>
/// <b>No caching.</b> Every <see cref="GetKeyAsync"/> unwraps through the KEK. A cache would keep a
/// destroyed key alive in every process that had read it.
/// </para>
/// <para>
/// <b>Batches.</b> <see cref="GetKeysAsync"/> looks up and unwraps a batch concurrently, at most
/// <see cref="MaxConcurrentKeyLookups"/> at a time, so a read of many sealed rows costs about as long as a
/// few KMS round trips rather than one per row. The bound is per call: with several calls in flight (the text
/// search, the vector search and the eligibility re-read of one injection, and parallel retrievals) the
/// unwraps in flight can reach the bound times the number of concurrent calls.
/// </para>
/// <para>
/// <b>Thread safety.</b> Because of that concurrency, the <see cref="IExperienceWrappedKeyRepository"/> and
/// <see cref="IExperienceKeyEncryptionKey"/> this store is given are called from several threads at once and
/// must be thread-safe.
/// </para>
/// </remarks>
public sealed class EnvelopeExperienceKeyStore : IExperienceKeyStore
{
    /// <summary>The largest page <see cref="RewrapAsync"/> accepts.</summary>
    public const int MaxRewrapBatchSize = 500;

    /// <summary>How many lookups <see cref="GetKeysAsync"/> runs at once unless the constructor says otherwise.</summary>
    public const int DefaultMaxConcurrentKeyLookups = 16;

    private readonly IExperienceKeyEncryptionKey _keyEncryptionKey;
    private readonly IExperienceWrappedKeyRepository _repository;

    /// <summary>Creates the store over a KEK and a repository of wrapped keys.</summary>
    /// <param name="keyEncryptionKey">The host's key-encryption key.</param>
    /// <param name="repository">Where wrapped keys live -- outside the database's backup domain.</param>
    public EnvelopeExperienceKeyStore(IExperienceKeyEncryptionKey keyEncryptionKey, IExperienceWrappedKeyRepository repository)
        : this(keyEncryptionKey, repository, DefaultMaxConcurrentKeyLookups)
    {
    }

    /// <summary>Creates the store over a KEK and a repository of wrapped keys, with a batch concurrency bound.</summary>
    /// <param name="keyEncryptionKey">The host's key-encryption key.</param>
    /// <param name="repository">Where wrapped keys live -- outside the database's backup domain.</param>
    /// <param name="maxConcurrentKeyLookups">
    /// The most repository lookups and KEK unwraps one <see cref="GetKeysAsync"/> call runs at once. At least 1.
    /// It bounds one call, not the process: concurrent calls each run up to this many, so size it to the KMS's
    /// rate limit divided by the calls you expect in flight together.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrentKeyLookups"/> is less than 1.</exception>
    public EnvelopeExperienceKeyStore(
        IExperienceKeyEncryptionKey keyEncryptionKey,
        IExperienceWrappedKeyRepository repository,
        int maxConcurrentKeyLookups)
    {
        ArgumentNullException.ThrowIfNull(keyEncryptionKey);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentKeyLookups, 1);
        _keyEncryptionKey = keyEncryptionKey;
        _repository = repository;
        MaxConcurrentKeyLookups = maxConcurrentKeyLookups;
    }

    /// <summary>The most lookups one <see cref="GetKeysAsync"/> call runs at once; concurrent calls each run up to this many.</summary>
    public int MaxConcurrentKeyLookups { get; }

    /// <inheritdoc />
    public async ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        KeyReferenceEncoding.Validate(reference);

        var existing = await _repository.GetAsync(reference, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await OpenAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        var dataKey = new byte[ExperienceDataKey.SizeInBytes];
        try
        {
            RandomNumberGenerator.Fill(dataKey);
            var wrapped = await _keyEncryptionKey.WrapAsync(dataKey, reference, cancellationToken).ConfigureAwait(false);

            // Atomic: a concurrent creator that stored first wins, and both callers then use its key, so one
            // record is never sealed under two keys. A destruction that got there first wins too.
            var winner = await _repository.AddIfAbsentAsync(reference, wrapped, cancellationToken).ConfigureAwait(false);
            if (winner.WrappedKey is { } stored
                && string.Equals(stored.KeyEncryptionKeyId, wrapped.KeyEncryptionKeyId, StringComparison.Ordinal)
                && stored.Ciphertext.Span.SequenceEqual(wrapped.Ciphertext.Span))
            {
                return ExperienceKeyLookup.Active(new ExperienceDataKey(dataKey));
            }

            return await OpenAsync(winner, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        KeyReferenceEncoding.Validate(reference);

        var entry = await _repository.GetAsync(reference, cancellationToken).ConfigureAwait(false);
        return entry is null
            ? ExperienceKeyLookup.NotFound
            : await OpenAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns several records' keys without creating any, looking them up and unwrapping them concurrently --
    /// at most <see cref="MaxConcurrentKeyLookups"/> at a time. Result <c>i</c> answers
    /// <paramref name="references"/>[<c>i</c>], exactly as <see cref="GetKeyAsync"/> would. Nothing is cached:
    /// every call reads the repository again, so a key destroyed before the call is never returned.
    /// </summary>
    /// <remarks>
    /// When any lookup or unwrap throws, the remaining ones are cancelled, every key already unwrapped is
    /// disposed, and the first failure propagates.
    /// </remarks>
    /// <param name="references">The records.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One lookup per reference, in the same order.</returns>
    public async ValueTask<IReadOnlyList<ExperienceKeyLookup>> GetKeysAsync(
        IReadOnlyList<ExperienceKeyReference> references,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        foreach (var reference in references)
        {
            KeyReferenceEncoding.Validate(reference);
        }

        return await LookUpConcurrentlyAsync(references, MaxConcurrentKeyLookups, GetKeyAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The fan-out behind <see cref="GetKeysAsync"/>: runs <paramref name="lookup"/> for every reference, at
    /// most <paramref name="maxConcurrency"/> at a time, and returns the answers in reference order. When any
    /// lookup throws, the rest are cancelled, every key already obtained is disposed, and the first failure
    /// propagates.
    /// </summary>
    internal static async ValueTask<IReadOnlyList<ExperienceKeyLookup>> LookUpConcurrentlyAsync(
        IReadOnlyList<ExperienceKeyReference> references,
        int maxConcurrency,
        Func<ExperienceKeyReference, CancellationToken, ValueTask<ExperienceKeyLookup>> lookup,
        CancellationToken cancellationToken)
    {
        var results = new ExperienceKeyLookup[references.Count];
        try
        {
            if (results.Length <= 1 || maxConcurrency == 1)
            {
                for (var i = 0; i < results.Length; i++)
                {
                    results[i] = await lookup(references[i], cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // Every worker has finished -- succeeded, failed or been cancelled -- by the time this returns or
                // throws, so no key can be written into results after the cleanup below has run.
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, results.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
                    async (index, token) => results[index] = await lookup(references[index], token).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (var result in results)
            {
                result.Key?.Dispose();
            }

            throw;
        }

        return results;
    }

    /// <inheritdoc />
    public ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken)
    {
        KeyReferenceEncoding.Validate(reference);
        return _repository.DestroyAsync(reference, cancellationToken);
    }

    /// <summary>
    /// Re-wraps up to <paramref name="batchSize"/> data keys that are not yet under the KEK's
    /// <see cref="IExperienceKeyEncryptionKey.CurrentKeyId"/>. Bounded and resumable: call it until
    /// <see cref="ExperienceKeyRewrapResult.MoreRemain"/> is <see langword="false"/>, then retire the old KEK.
    /// </summary>
    /// <remarks>
    /// Each key is unwrapped under its old KEK and wrapped under the current one, and replaced only while the
    /// stored entry is still the one read (compare-and-replace), so a destruction racing the rotation always
    /// wins: a destroyed key is never written back.
    /// </remarks>
    /// <param name="batchSize">The most keys this call re-wraps, 1 to <see cref="MaxRewrapBatchSize"/>.</param>
    /// <param name="cancellationToken">Cancels the operation between keys.</param>
    /// <returns>How many keys were re-wrapped, and whether more remain.</returns>
    public async Task<ExperienceKeyRewrapResult> RewrapAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, MaxRewrapBatchSize);

        var currentKeyId = _keyEncryptionKey.CurrentKeyId;
        var page = await _repository.ListNotWrappedUnderAsync(currentKeyId, batchSize + 1, cancellationToken).ConfigureAwait(false);

        var rewrapped = 0;
        foreach (var entry in page.Take(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.WrappedKey is not { } old)
            {
                continue;
            }

            var dataKey = await _keyEncryptionKey.UnwrapAsync(old, entry.Reference, cancellationToken).ConfigureAwait(false);
            try
            {
                var replacement = await _keyEncryptionKey.WrapAsync(dataKey, entry.Reference, cancellationToken).ConfigureAwait(false);
                if (await _repository.ReplaceAsync(entry.Reference, old, replacement, cancellationToken).ConfigureAwait(false))
                {
                    rewrapped++;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }

        return new ExperienceKeyRewrapResult(rewrapped, page.Count > batchSize);
    }

    private async ValueTask<ExperienceKeyLookup> OpenAsync(ExperienceWrappedKeyEntry entry, CancellationToken cancellationToken)
    {
        if (entry.WrappedKey is not { } wrapped)
        {
            return ExperienceKeyLookup.Destroyed;
        }

        var dataKey = await _keyEncryptionKey.UnwrapAsync(wrapped, entry.Reference, cancellationToken).ConfigureAwait(false);
        try
        {
            return ExperienceKeyLookup.Active(new ExperienceDataKey(dataKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }
}

/// <summary>What one <see cref="EnvelopeExperienceKeyStore.RewrapAsync"/> call did.</summary>
/// <param name="RewrappedCount">Keys this call moved under the current KEK.</param>
/// <param name="MoreRemain">Whether another call would find keys still under an older KEK.</param>
public sealed record ExperienceKeyRewrapResult(int RewrappedCount, bool MoreRemain);
