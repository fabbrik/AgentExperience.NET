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
/// applies the scope, the requested statuses, the minimum confidence, and the authorship exclusion when the
/// query asks for it, matches the text, and returns each match with a normalized relevance. Which statuses are eligible, whether a record has
/// expired, whether its environment is compatible, and how candidates are ranked are all Core's
/// decisions, made over what this port returns.
/// </para>
/// </remarks>
public interface IExperienceCandidateSource
{
    /// <summary>
    /// Finds records within exactly <see cref="ExperienceCandidateQuery.Scope"/> whose indexed task
    /// text matches <see cref="ExperienceCandidateQuery.TaskText"/> -- contains at least
    /// <see cref="ExperienceCandidateQuery.MinimumMatchedTerms"/> of its terms, capped at their number -- whose
    /// <see cref="ExperienceRecord.Status"/> is one of
    /// <see cref="ExperienceCandidateQuery.EligibleStatuses"/>, and whose
    /// <see cref="ExperienceRecord.ReuseConfidence"/> is at least
    /// <see cref="ExperienceCandidateQuery.MinimumConfidence"/>, leaving out model-authored records when
    /// <see cref="ExperienceCandidateQuery.ExcludeModelAuthored"/> is set. At most
    /// <see cref="ExperienceCandidateQuery.Limit"/> records are returned, the strongest text matches
    /// first: of two records whose matched terms lie in the same fields, the one covering more of the query's terms
    /// comes first. A source may weight fields (the in-memory source counts a task-summary term above a lesson term), so
    /// across fields fewer terms can outrank more.
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
/// <param name="CorrelationId">
/// The host's identifier for the work causing this search, recorded on the access rows written for
/// any grant-permitted records it returns, so a disclosure can be tied to the invocation behind it.
/// Nothing else reads it. <see langword="null"/> when the caller has none.
/// </param>
public sealed record ExperienceCandidateQuery(
    Scope Scope,
    string TaskText,
    IReadOnlyList<ExperienceStatus> EligibleStatuses,
    double MinimumConfidence,
    int Limit = ExperienceCandidateQuery.DefaultLimit,
    string? CorrelationId = null)
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

    /// <summary>
    /// <see langword="true"/> to leave out every model-authored record: one whose <see cref="ExperienceRecord.Reflection"/>
    /// exists and whose <see cref="Reflection.Authorship"/> is anything but <see cref="ReflectionAuthorship.Deterministic"/>,
    /// so an undefined or future value counts as model-authored, or whose <see cref="Reflection.Producer"/> starts with
    /// <c>AgentExperience.ChatClientExperienceReflector/</c> (ordinal): the library's own model-backed reflector wrote it,
    /// whatever authorship it declared. A record with no reflection is not model-authored.
    /// <see langword="false"/> (the default) leaves the search exactly as it is without this property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exclusion is a filter like <see cref="EligibleStatuses"/> and <see cref="MinimumConfidence"/>, applied
    /// <em>before</em> <see cref="Limit"/>: an excluding search returns up to <see cref="Limit"/> of the strongest
    /// records that are not model-authored, never fewer because model-authored records filled the window. Authorship is
    /// read from the stored reflection; the producer counts only when it is the library's own model-backed reflector's,
    /// never a third-party reflector's.
    /// </para>
    /// <para>
    /// An implementation that cannot honour it must not ignore it: it answers
    /// <see cref="ExperienceStoreOutcome.Invalid"/>, with an error naming this property, rather than returning a page
    /// that may hold model-authored records. The store conformance suite checks the exclusion. A record whose authorship
    /// the source cannot read counts as model-authored: the PostgreSQL candidate source leaves out a sealed row stored
    /// without its authorship flag until the owner's backfill writes it. A consumer that must never see a
    /// model-authored record still checks what it receives, as Core's retrieval service and the injection provider do.
    /// </para>
    /// </remarks>
    public bool ExcludeModelAuthored { get; init; }

    /// <summary>The <see cref="MinimumMatchedTerms"/> used when none is specified.</summary>
    public const int DefaultMinimumMatchedTerms = 3;

    /// <summary>
    /// A <see cref="MinimumMatchedTerms"/> that asks for every query term: the cap lowers it to the query's term count,
    /// so a record must contain all of them, which is how both shipped sources matched up to 0.1.0-preview.8.
    /// </summary>
    public const int AllTerms = int.MaxValue;

    /// <summary>
    /// How many distinct query terms a record must contain to be a candidate, capped at the number of terms the query
    /// has, so a one-word query still matches with any minimum. At least 1; defaults to
    /// <see cref="DefaultMinimumMatchedTerms"/>. <see cref="AllTerms"/> (or any value at least the query's term count)
    /// asks for every term.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A query's terms are the source's, not a count of the words typed: the in-memory source counts distinct whole
    /// words, less stopwords and one-character words; the PostgreSQL source counts distinct stems, less stopwords, with
    /// a hyphenated compound counted once. A query with no terms left matches nothing, whatever this is.
    /// </para>
    /// <para>
    /// A record that contains only some terms ranks below one that contains more in the same fields: both shipped
    /// sources report a coverage-based <see cref="ExperienceCandidate.Relevance"/>. The source keeps the order it gave
    /// records containing every term when every term was required (on PostgreSQL, for a plain-word request), but the
    /// PostgreSQL source now reports every such record at relevance 1, so Core's ranking no longer sees text strength
    /// between full matches and orders them by its other components. The minimum keeps one-word coincidences out of a long request's candidates. It is
    /// a filter like <see cref="MinimumConfidence"/>, applied before <see cref="Limit"/>. A value below 1 is
    /// <see cref="ExperienceStoreOutcome.Invalid"/>.
    /// </para>
    /// </remarks>
    public int MinimumMatchedTerms { get; init; } = DefaultMinimumMatchedTerms;
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
/// <param name="SharedByGrant">
/// <see langword="true"/> when this record does not belong to the requested scope and was matched
/// only because an active <see cref="ExperienceGrant"/> permits that scope to read it. Only the
/// implementation that applied the scope predicate knows this, so only it may set it: a caller must
/// never infer sharing from comparing scopes, and a consumer must treat an unset flag as "this record
/// is the requester's own". It exists so a consumer can keep the strict scope check it would
/// otherwise have to weaken, and so borrowed experience can be labelled as such.
/// </param>
/// <param name="PermittingGrantId">
/// Which <see cref="ExperienceGrant"/> permitted this record to be returned, when
/// <paramref name="SharedByGrant"/> is <see langword="true"/>: the one the implementation's own
/// predicate used, not merely one that could have. <see langword="null"/> for a record the requester
/// owns, and <see langword="null"/> from an implementation that cannot say which grant applied.
/// <para>
/// <paramref name="Record"/> is the record read back <em>in full</em>, so returning one is a
/// disclosure and not a match notice. This ID is the one on the access row an
/// <see cref="IExperienceGrantAccessLog"/> writes for it.
/// </para>
/// </param>
public sealed record ExperienceCandidate(
    ExperienceRecord Record,
    double Relevance,
    bool SharedByGrant = false,
    Guid? PermittingGrantId = null);

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
