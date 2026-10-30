using AgentExperience.Abstractions;

namespace AgentExperience.Storage.InMemory;

/// <summary>
/// An <see cref="IExperienceSignatureSource"/> over the records of one <see cref="InMemoryExperienceRecordStore"/>
/// (story 28.2). <b>For development and tests only</b>, like the store it reads.
/// </summary>
/// <remarks>
/// <para>
/// The store derives each record's failure signatures (<see cref="ExperienceFailureSignatures.Of"/>) once, when the
/// record is created, and indexes them by key and by tool and error class; a lifecycle commit changes a record's status
/// and confidence, which this source reads at search time, never its attempts.
/// </para>
/// <para>
/// <b>Filters and order.</b> Only records in exactly the given scope, in one of the query's eligible statuses, at or
/// above its minimum confidence and, when <see cref="ExperienceSignatureQuery.ExcludeModelAuthored"/> is set, not
/// model-authored; then exact matches first, resolved before unresolved, higher confidence, more recently created, and
/// by ID, and only then cut to the limit. There are no grants here: a search returns only the requester's own records.
/// </para>
/// </remarks>
public sealed class InMemoryExperienceSignatureSource : IExperienceSignatureSource
{
    private readonly InMemoryExperienceRecordStore _store;

    /// <summary>
    /// Creates a signature source over <paramref name="store"/>'s records. <b>For development and tests only.</b>
    /// </summary>
    /// <param name="store">The record store whose records are searched. A committed write is visible to the next search.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public InMemoryExperienceSignatureSource(InMemoryExperienceRecordStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<ExperienceSignatureSearchResult> FindBySignatureAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceSignatureQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateSignatureQuery(scope, query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceSignatureSearchResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceSignatureSearchResult(ExperienceStoreOutcome.Denied, [], []));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceSignatureSearchResult>(cancellationToken);
        }

        var statuses = query.EligibleStatuses.ToHashSet();
        var candidates = _store
            .SelectBySignature(
                scope,
                record => statuses.Contains(record.Status)
                    && record.ReuseConfidence >= query.MinimumConfidence
                    && !(query.ExcludeModelAuthored && ReflectionAuthorshipRule.IsModelAuthored(record.Reflection)),
                query.Signature,
                query.Match)
            .OrderBy(found => found.Match == ExperienceSignatureMatch.Exact ? 0 : 1)
            .ThenBy(found => found.Resolution == ExperienceSignatureResolution.Resolved ? 0 : 1)
            .ThenByDescending(found => found.Record.ReuseConfidence)
            .ThenByDescending(found => found.Record.CreatedAt)
            .ThenBy(found => found.Record.ExperienceId)
            .Take(query.Limit)
            .Select(found => new ExperienceSignatureCandidate(found.Record, found.Match, found.Resolution))
            .ToList();

        return Task.FromResult(new ExperienceSignatureSearchResult(ExperienceStoreOutcome.Found, candidates, []));
    }
}
