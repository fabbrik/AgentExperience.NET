using AgentExperience.Abstractions;

namespace AgentExperience.Storage.InMemory;

/// <summary>
/// An <see cref="IExperienceEmbeddingIndex"/> over the records of one <see cref="InMemoryExperienceRecordStore"/>, with
/// exact cosine search. <b>For development and tests only</b>: its vectors live in process memory and are lost when the
/// process ends, like the records they describe.
/// </summary>
/// <remarks>
/// <para>
/// It applies the PostgreSQL embedding index's rules, observed through the port: a request is validated by the same
/// shared rules, then checked against the authorization, before anything is read. A write is conditional on the record
/// existing in exactly the write's scope at exactly the descriptor's source revision, checked and stored as one step
/// under the record store's lock, so a record that moved is <see cref="ExperienceIndexOutcome.Stale"/>, one that is not
/// there is <see cref="ExperienceIndexOutcome.Missing"/> and no vector is ever stored for it, and an older write can
/// never replace a vector stored from a newer revision. The scope stored with a vector is the record's own.
/// </para>
/// <para>
/// A scan and a search apply the same status filter and confidence floor; a search also requires the query's model and
/// dimension, so an incomparable vector is never compared, and reports <see cref="ExperienceVectorSearchOutcome.ModelMismatch"/>
/// or <see cref="ExperienceVectorSearchOutcome.DimensionMismatch"/> when the scope holds embeddings but none is comparable.
/// The relevance is <c>1 - d / 2</c> for the cosine distance <c>d</c> in [0, 2], as pgvector's <c>&lt;=&gt;</c> gives it;
/// a zero-magnitude vector scores 0. Results are nearest first, ties broken by experience ID. The search is exact, not
/// approximate: it compares the query with every comparable vector in the scope.
/// </para>
/// <para>
/// There are no grants and no erasure here, as in the in-memory record store: a search returns only the requester's
/// own records.
/// </para>
/// </remarks>
internal sealed class InMemoryExperienceEmbeddingIndex : IExperienceEmbeddingIndex
{
    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly InMemoryExperienceRecordStore _store;

    /// <summary>The stored vectors, keyed by experience ID. Read and written only under the record store's lock.</summary>
    private readonly Dictionary<Guid, StoredEmbedding> _embeddings = [];

    /// <summary>Creates an index over <paramref name="store"/>'s records. <b>For development and tests only.</b></summary>
    /// <param name="store">The record store whose records are indexed. A committed write is visible to the next call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public InMemoryExperienceEmbeddingIndex(InMemoryExperienceRecordStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<ExperienceIndexWriteResult> WriteAsync(
        AuthorizationContext authorization,
        ExperienceIndexWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(write);

        var errors = ExperienceRecordValidator.ValidateIndexWrite(write);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceIndexWriteResult(ExperienceIndexOutcome.Invalid, 0, errors));
        }

        if (!authorization.Permits(write.Scope))
        {
            return Task.FromResult(new ExperienceIndexWriteResult(ExperienceIndexOutcome.Denied, 0, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceIndexWriteResult>(cancellationToken);
        }

        // Copied before the lock, so the caller's buffer can change afterwards without changing what is stored.
        var vector = write.Vector.ToArray();
        return Task.FromResult(_store.Locked(records =>
        {
            if (!records.TryGetValue(write.ExperienceId, out var record) || record.Scope != write.Scope)
            {
                // Another scope's record is indistinguishable from a missing one, and no vector is stored for either.
                return new ExperienceIndexWriteResult(ExperienceIndexOutcome.Missing, 0, NoErrors);
            }

            if (record.Revision != write.Descriptor.SourceRevision
                || (_embeddings.TryGetValue(record.ExperienceId, out var stored) && stored.Descriptor.SourceRevision > write.Descriptor.SourceRevision))
            {
                return new ExperienceIndexWriteResult(ExperienceIndexOutcome.Stale, record.Revision, NoErrors);
            }

            _embeddings[record.ExperienceId] = new StoredEmbedding(record.Scope, write.Descriptor, vector);
            return new ExperienceIndexWriteResult(ExperienceIndexOutcome.Written, 0, NoErrors);
        }));
    }

    /// <inheritdoc />
    public Task<ExperienceIndexScanResult> ScanAsync(
        AuthorizationContext authorization,
        ExperienceIndexScan scan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scan);

        var errors = ExperienceRecordValidator.ValidateIndexScan(scan);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceIndexScanResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(scan.Scope))
        {
            return Task.FromResult(new ExperienceIndexScanResult(ExperienceStoreOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceIndexScanResult>(cancellationToken);
        }

        var statuses = scan.EligibleStatuses.ToHashSet();
        var ids = scan.ExperienceIds?.ToHashSet();
        var targets = _store.Locked(records => records.Values
            .Where(record => record.Scope == scan.Scope
                && statuses.Contains(record.Status)
                && record.ReuseConfidence >= scan.MinimumConfidence
                && (ids is null || ids.Contains(record.ExperienceId))
                && (scan.StartAfterId is not { } after || record.ExperienceId.CompareTo(after) > 0))
            .OrderBy(record => record.ExperienceId)
            .Take(scan.Limit)
            .Select(record => new ExperienceIndexTarget(
                record.ExperienceId,
                record.Revision,
                ExperienceRetrievalSummary.For(record),
                // Whatever is stored, under whichever model: the caller compares it with its own.
                _embeddings.TryGetValue(record.ExperienceId, out var stored) ? stored.Descriptor : null))
            .ToList());

        return Task.FromResult(new ExperienceIndexScanResult(
            ExperienceStoreOutcome.Found,
            targets,
            NoErrors,
            targets.Count == 0 ? null : targets[^1].ExperienceId));
    }

    /// <inheritdoc />
    public Task<ExperienceVectorSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceVectorQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateVectorQuery(query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceVectorSearchResult(ExperienceVectorSearchOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(query.Scope))
        {
            return Task.FromResult(new ExperienceVectorSearchResult(ExperienceVectorSearchOutcome.Denied, [], NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceVectorSearchResult>(cancellationToken);
        }

        var statuses = query.EligibleStatuses.ToHashSet();
        var dimension = query.Vector.Length;

        // Only the pairs are taken under the lock: records and vectors are immutable once stored, so filtering and scoring
        // run after it, and a search never holds the store's lock for a whole pass.
        var pairs = _store.Locked(records =>
        {
            var taken = new List<(StoredEmbedding Embedding, ExperienceRecord Record)>(_embeddings.Count);
            foreach (var (id, embedding) in _embeddings)
            {
                if (embedding.Scope == query.Scope && records.TryGetValue(id, out var record))
                {
                    taken.Add((embedding, record));
                }
            }

            return taken;
        });

        var population = pairs
            .Where(entry => entry.Record.Scope == query.Scope
                && statuses.Contains(entry.Record.Status)
                && entry.Record.ReuseConfidence >= query.MinimumConfidence
                && !(query.ExcludeModelAuthored && ReflectionAuthorshipRule.IsModelAuthored(entry.Record.Reflection)))
            .ToList();

        var comparable = population
            .Where(entry => string.Equals(entry.Embedding.Descriptor.ModelId, query.ModelId, StringComparison.Ordinal)
                && entry.Embedding.Descriptor.Dimension == dimension
                && entry.Embedding.Vector.Length == dimension)
            .ToList();

        if (comparable.Count == 0 && population.Count > 0)
        {
            // Embeddings are here, but none is comparable: say which, naming the width only when the model matched.
            var sameModel = population.Any(entry => string.Equals(entry.Embedding.Descriptor.ModelId, query.ModelId, StringComparison.Ordinal));
            return Task.FromResult(new ExperienceVectorSearchResult(
                sameModel ? ExperienceVectorSearchOutcome.DimensionMismatch : ExperienceVectorSearchOutcome.ModelMismatch,
                [],
                NoErrors));
        }

        var queryVector = query.Vector.Span;
        var scored = new List<ExperienceCandidate>(comparable.Count);
        foreach (var (embedding, record) in comparable)
        {
            scored.Add(new ExperienceCandidate(record, Relevance(queryVector, embedding.Vector)));
        }

        var candidates = scored
            .OrderByDescending(candidate => candidate.Relevance)
            .ThenBy(candidate => candidate.Record.ExperienceId)
            .Take(query.Limit)
            .ToList();

        return Task.FromResult(new ExperienceVectorSearchResult(ExperienceVectorSearchOutcome.Found, candidates, NoErrors));
    }

    /// <inheritdoc />
    public Task<ExperienceIndexRemoveResult> RemoveAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateIndexRemove(scope, experienceId);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceIndexRemoveResult(ExperienceIndexRemoveOutcome.Invalid, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceIndexRemoveResult(ExperienceIndexRemoveOutcome.Denied, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceIndexRemoveResult>(cancellationToken);
        }

        // Matched on the vector's own scope, so a vector is removable whatever happened to its record since.
        return Task.FromResult(_store.Locked(_ =>
            _embeddings.TryGetValue(experienceId, out var stored) && stored.Scope == scope && _embeddings.Remove(experienceId)
                ? new ExperienceIndexRemoveResult(ExperienceIndexRemoveOutcome.Removed, NoErrors)
                : new ExperienceIndexRemoveResult(ExperienceIndexRemoveOutcome.NotIndexed, NoErrors)));
    }

    /// <summary>
    /// <c>1 - d / 2</c> for the cosine distance <c>d = 1 - cos</c>, that is <c>(1 + cos) / 2</c>, clamped to [0, 1]. A
    /// zero-magnitude vector has no direction, so it scores 0, as the PostgreSQL index scores pgvector's NaN.
    /// </summary>
    private static double Relevance(ReadOnlySpan<float> query, float[] stored)
    {
        double dot = 0, queryNorm = 0, storedNorm = 0;
        for (var i = 0; i < query.Length; i++)
        {
            dot += (double)query[i] * stored[i];
            queryNorm += (double)query[i] * query[i];
            storedNorm += (double)stored[i] * stored[i];
        }

        if (queryNorm == 0 || storedNorm == 0)
        {
            return 0d;
        }

        var cosine = dot / (Math.Sqrt(queryNorm) * Math.Sqrt(storedNorm));
        return double.IsFinite(cosine) ? Math.Clamp((1d + cosine) / 2d, 0d, 1d) : 0d;
    }

    /// <summary>One stored vector: the scope copied from its record, what it is, and its components.</summary>
    private sealed record StoredEmbedding(Scope Scope, ExperienceEmbeddingDescriptor Descriptor, float[] Vector);
}
