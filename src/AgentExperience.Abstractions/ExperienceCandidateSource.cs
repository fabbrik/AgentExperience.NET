namespace AgentExperience.Abstractions;

/// <summary>
/// Port for finding Experience Records that could apply to a task, matched on task text. It is a
/// read-only search seam kept deliberately separate from <see cref="IExperienceRecordStore"/>: the
/// store persists and reads canonical records by identity or scope, while this port answers "which
/// stored records look relevant to this text?" and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The same trust boundary applies as to <see cref="IExperienceRecordStore"/>: every call takes a
/// host-established <see cref="AuthorizationContext"/>, a request scope outside it is
/// <see cref="ExperienceStoreOutcome.Denied"/> before any storage access, and scope matching is exact
/// (ordinal, case-sensitive, <see langword="null"/> matches only <see langword="null"/>). Expected
/// conditions return typed results; infrastructure failures throw
/// <see cref="ExperienceStoreException"/>; caller cancellation surfaces as an unwrapped
/// <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// An implementation decides <em>nothing</em> about eligibility beyond what the query asks for: it
/// applies the scope, the requested statuses, and the minimum confidence, matches the text, and
/// returns each match with a normalized relevance. Which statuses are eligible, whether a record has
/// expired, whether its environment is compatible, and how candidates are ranked are all Core's
/// decisions, made over what this port returns.
/// </para>
/// </remarks>
public interface IExperienceCandidateSource
{
    /// <summary>
    /// Finds records within exactly <see cref="ExperienceCandidateQuery.Scope"/> whose indexed task
    /// text matches <see cref="ExperienceCandidateQuery.TaskText"/>, whose
    /// <see cref="ExperienceRecord.Status"/> is one of
    /// <see cref="ExperienceCandidateQuery.EligibleStatuses"/>, and whose
    /// <see cref="ExperienceRecord.ReuseConfidence"/> is at least
    /// <see cref="ExperienceCandidateQuery.MinimumConfidence"/>. At most
    /// <see cref="ExperienceCandidateQuery.Limit"/> records are returned, the strongest text matches
    /// first.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="query">The scoped search. Never treated as authority.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Found"/> (possibly with no candidates), <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    Task<ExperienceCandidateSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// A scoped, text-matched search for reusable Experience Records.
/// </summary>
/// <param name="Scope">The exact scope to search within. Never treated as authority.</param>
/// <param name="TaskText">The task text to match against. Must be non-blank and at most <see cref="MaxTaskTextLength"/> characters.</param>
/// <param name="EligibleStatuses">The statuses a record must be in to be returned. Must be non-empty and contain only defined values; the caller decides which statuses are eligible.</param>
/// <param name="MinimumConfidence">The smallest <see cref="ExperienceRecord.ReuseConfidence"/> a record may have and still be returned, in [0, 1].</param>
/// <param name="Limit">Maximum number of candidates to return, from <see cref="MinLimit"/> to <see cref="MaxLimit"/>. Defaults to <see cref="DefaultLimit"/>.</param>
public sealed record ExperienceCandidateQuery(
    Scope Scope,
    string TaskText,
    IReadOnlyList<ExperienceStatus> EligibleStatuses,
    double MinimumConfidence,
    int Limit = ExperienceCandidateQuery.DefaultLimit)
{
    /// <summary>
    /// The longest permitted <see cref="TaskText"/>. A task description is a sentence or a paragraph;
    /// bounding it here keeps an accidental multi-megabyte payload a typed
    /// <see cref="ExperienceStoreOutcome.Invalid"/> rather than something the text-search parser chokes
    /// on deep inside the database.
    /// </summary>
    public const int MaxTaskTextLength = 4096;

    /// <summary>The smallest permitted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest permitted <see cref="Limit"/>.</summary>
    public const int MaxLimit = 200;

    /// <summary>The <see cref="Limit"/> used when none is specified.</summary>
    public const int DefaultLimit = 50;
}

/// <summary>
/// One record a search matched, with how strongly its indexed text matched the query.
/// </summary>
/// <param name="Record">The matching record, read back in full.</param>
/// <param name="Relevance">
/// How strongly the record's indexed text matched, normalized to [0, 1] by the implementation, where
/// 0 is no measurable match and 1 is the strongest the implementation can report. Comparable only
/// between candidates from the same search.
/// </param>
public sealed record ExperienceCandidate(ExperienceRecord Record, double Relevance);

/// <summary>
/// The result of <see cref="IExperienceCandidateSource.SearchAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Candidates">The matching candidates, strongest match first, when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceCandidateSearchResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ExperienceCandidate> Candidates,
    IReadOnlyList<StoreValidationError> Errors);
