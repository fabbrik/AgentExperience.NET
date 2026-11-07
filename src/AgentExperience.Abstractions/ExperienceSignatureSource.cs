namespace AgentExperience.Abstractions;

/// <summary>
/// Optional port for finding Experience Records by the situation a run is in rather than by its request text: records
/// holding a failed tool call with a given <see cref="ExperienceFailureSignature"/>. It is a read-only
/// search seam beside <see cref="IExperienceCandidateSource"/>, which it does not change; a store that does not index
/// signatures simply does not implement it.
/// </summary>
/// <remarks>
/// <para>
/// The same trust boundary and the same eligibility as text search: every call takes a host-established
/// <see cref="AuthorizationContext"/>, a scope outside it is <see cref="ExperienceStoreOutcome.Denied"/> before any
/// storage access, and scope matching is exact; a record is returned only when it is live, in one of the query's
/// eligible statuses, at or above its minimum confidence, and -- when the query asks -- not model-authored. A store that
/// widens text search through sharing grants widens this search the same way, and says so on each candidate.
/// Expected conditions return typed results; infrastructure failures throw <see cref="ExperienceStoreException"/>;
/// caller cancellation surfaces as an unwrapped <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// Only signatures of <see cref="ExperienceFailureSignature.SignatureVersion"/> are matched.
/// </para>
/// </remarks>
public interface IExperienceSignatureSource
{
    /// <summary>
    /// Finds records within exactly <paramref name="scope"/> holding a failed call whose signature matches
    /// <see cref="ExperienceSignatureQuery.Signature"/>: the same <see cref="ExperienceFailureSignature.Key"/> under
    /// <see cref="ExperienceSignatureMatch.Exact"/>; the same tool name and error class, whatever the fingerprint,
    /// under <see cref="ExperienceSignatureMatch.Coarse"/>. Each record is returned once, saying how it matched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order.</b> Exact matches first, then records whose matched signature is
    /// <see cref="ExperienceSignatureResolution.Resolved"/>, then higher <see cref="ExperienceRecord.ReuseConfidence"/>,
    /// then more recently created, then by ID (an ID tie-break orders differently between stores, as text search's does).
    /// At most <see cref="ExperienceSignatureQuery.Limit"/> are returned, after every filter.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope to search within. Never treated as authority.</param>
    /// <param name="query">The signature and the eligibility filters.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Found"/> (possibly with no candidates), <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    Task<ExperienceSignatureSearchResult> FindBySignatureAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceSignatureQuery query,
        CancellationToken cancellationToken);
}

/// <summary>How a signature search matches.</summary>
public enum ExperienceSignatureMatch
{
    /// <summary>The same <see cref="ExperienceFailureSignature.Key"/>: the same tool, the same error class and the same fingerprint.</summary>
    Exact = 0,

    /// <summary>The same tool name and error class, whatever the fingerprint. A coarse search returns exact matches too, first.</summary>
    Coarse = 1,
}

/// <summary>A scoped signature search for reusable Experience Records.</summary>
/// <param name="Signature">The failure signature to match. Its parts must be what <see cref="ExperienceFailureSignature.From(string, string)"/> could derive: a keyable tool name, a non-blank error class of at most <see cref="ExperienceFailureSignature.MaxErrorClassLength"/> characters with no control character, and a 32-character lowercase hexadecimal fingerprint.</param>
/// <param name="EligibleStatuses">The statuses a record must be in to be returned. Must be non-empty and contain only defined values.</param>
/// <param name="MinimumConfidence">The smallest <see cref="ExperienceRecord.ReuseConfidence"/> a record may have and still be returned, in [0, 1].</param>
/// <param name="Match">Whether to match the whole signature or only its tool and error class.</param>
/// <param name="Limit">Maximum number of candidates to return, from <see cref="MinLimit"/> to <see cref="MaxLimit"/>.</param>
/// <param name="CorrelationId">The host's identifier for the work causing this search, recorded on the access rows written for any grant-permitted records it returns. <see langword="null"/> when the caller has none.</param>
public sealed record ExperienceSignatureQuery(
    ExperienceFailureSignature Signature,
    IReadOnlyList<ExperienceStatus> EligibleStatuses,
    double MinimumConfidence,
    ExperienceSignatureMatch Match = ExperienceSignatureMatch.Exact,
    int Limit = ExperienceSignatureQuery.DefaultLimit,
    string? CorrelationId = null)
{
    /// <summary>The smallest permitted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest permitted <see cref="Limit"/>.</summary>
    public const int MaxLimit = 200;

    /// <summary>The <see cref="Limit"/> used when none is specified.</summary>
    public const int DefaultLimit = 20;

    /// <summary>
    /// <see langword="true"/> to leave out every model-authored record, by exactly the rule
    /// <see cref="ExperienceCandidateQuery.ExcludeModelAuthored"/> states, before <see cref="Limit"/>.
    /// </summary>
    public bool ExcludeModelAuthored { get; init; }
}

/// <summary>One record a signature search matched.</summary>
/// <param name="Record">The matching record, read back in full.</param>
/// <param name="Match">How it matched: <see cref="ExperienceSignatureMatch.Exact"/> when it holds the query's whole signature, <see cref="ExperienceSignatureMatch.Coarse"/> when only the tool and error class agree.</param>
/// <param name="Resolution">Whether the record's matched signature was followed by a call that worked (for a coarse match, any of its matching signatures; for an exact match, the exact one).</param>
/// <param name="SharedByGrant">As <see cref="ExperienceCandidate.SharedByGrant"/>: set only by a source that applied a sharing grant's predicate.</param>
/// <param name="PermittingGrantId">As <see cref="ExperienceCandidate.PermittingGrantId"/>.</param>
public sealed record ExperienceSignatureCandidate(
    ExperienceRecord Record,
    ExperienceSignatureMatch Match,
    ExperienceSignatureResolution Resolution,
    bool SharedByGrant = false,
    Guid? PermittingGrantId = null)
{
    /// <summary>As <see cref="ExperienceCandidate.GrantDisclosure"/>: set only alongside <see cref="SharedByGrant"/>.</summary>
    public ExperienceGrantDisclosure? GrantDisclosure { get; init; }
}

/// <summary>The result of <see cref="IExperienceSignatureSource.FindBySignatureAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Candidates">The matching candidates, in the port's order, when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceSignatureSearchResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ExperienceSignatureCandidate> Candidates,
    IReadOnlyList<StoreValidationError> Errors);
