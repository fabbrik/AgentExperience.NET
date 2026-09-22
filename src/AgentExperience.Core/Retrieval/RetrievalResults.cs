using AgentExperience.Abstractions;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// One request to find experience that applies to a task.
/// </summary>
/// <param name="Authorization">What the host has established the caller may do. The request's <paramref name="Scope"/> must lie inside it, or the call is denied before any search is issued.</param>
/// <param name="Scope">The exact scope to retrieve within. Never treated as authority, and never widened.</param>
/// <param name="TaskText">The task text to match stored experience against. Must be non-blank.</param>
/// <param name="RequiredEnvironmentAttributes">
/// Environment attributes a record must carry to be reusable here. Every pair must equal the record's
/// <see cref="EnvironmentFingerprint.Metadata"/> entry exactly (ordinal), and a record missing the key
/// is excluded. <see langword="null"/> or empty means the request names no requirement, and the result
/// is marked <see cref="ExperienceRetrievalResult.EnvironmentUnrestricted"/>.
/// </param>
/// <param name="CorrelationId">Optional. The host's correlation identifier, echoed on the result -- including on a timeout, so a timed-out retrieval can be tied back to the request that caused it.</param>
/// <param name="Limit">Optional. The most ranked records to return. Defaults to the policy's candidate limit when <see langword="null"/>; must be strictly positive and no greater than that limit.</param>
public sealed record RetrieveExperienceRequest(
    AuthorizationContext Authorization,
    Scope Scope,
    string TaskText,
    IReadOnlyDictionary<string, string>? RequiredEnvironmentAttributes = null,
    string? CorrelationId = null,
    int? Limit = null);

/// <summary>The component axes a retrieved record is scored on. Each is normalized to [0, 1].</summary>
public enum RankingComponentKind
{
    /// <summary>How strongly the record's indexed text matched the request's task text.</summary>
    Relevance,

    /// <summary>The record's <see cref="ExperienceRecord.ReuseConfidence"/>, which is already in [0, 1].</summary>
    Confidence,

    /// <summary>
    /// How recently the record last saw <em>lifecycle activity</em>, decayed by the policy's recency
    /// half-life. Measured from <see cref="ExperienceRecord.UpdatedAt"/>, which every lifecycle commit
    /// bumps, so a years-old lesson reinforced yesterday scores as fully recent. It is the age of the
    /// last status change, not of the lesson.
    /// </summary>
    Recency,

    /// <summary>The record's lifecycle status among the eligible ones.</summary>
    Status,

    /// <summary>How well the record's environment matches the request's required attributes.</summary>
    EnvironmentCompatibility,
}

/// <summary>
/// One normalized ranking component and the weight actually applied to it, so a host can always see
/// why one record outranked another rather than being handed an opaque score.
/// </summary>
/// <param name="Kind">Which axis this is.</param>
/// <param name="Value">The normalized component value, in [0, 1].</param>
/// <param name="Weight">The effective weight applied to <paramref name="Value"/>.</param>
public sealed record RankingComponent(RankingComponentKind Kind, double Value, double Weight)
{
    /// <summary>This component's contribution to the record's total score: <see cref="Value"/> times <see cref="Weight"/>.</summary>
    public double Contribution => Value * Weight;
}

/// <summary>
/// A record that passed every eligibility check, with its score and the components that produced it.
/// </summary>
/// <param name="Record">The eligible record, exactly as stored. Retrieval never rewrites it.</param>
/// <param name="Score">The weighted total of <paramref name="Components"/>, in [0, 1] whenever the weights sum to 1.</param>
/// <param name="Components">Every component, in <see cref="RankingComponentKind"/> order, each with the weight applied to it.</param>
/// <param name="SharedByGrant">
/// <see langword="true"/> when the candidate source reported that this record belongs to another
/// scope and was matched only through an active <see cref="ExperienceGrant"/>. Retrieval passes the
/// flag through unchanged -- it never decides sharing itself -- so a host's risk policy and anything
/// that renders the record can tell borrowed experience from the requester's own.
/// </param>
/// <param name="PermittingGrantId">
/// Which <see cref="ExperienceGrant"/> permitted this record to be <em>delivered</em>, when something
/// on the path said so. Retrieval itself never sets it and it is <see langword="null"/> on everything
/// this service returns: a candidate source reports that a search <em>matched</em> a shared record,
/// and a match is not a delivery -- no grant has been used to hand anything over yet, so naming one
/// here would claim an access that has not happened.
/// <para>
/// It is filled in by the final pre-injection re-read, which is a delivery and is the read an access
/// row is written for. A host reading it therefore has the same grant ID that appears in the audit
/// trail for that record.
/// </para>
/// </param>
public sealed record RankedExperience(
    ExperienceRecord Record,
    double Score,
    IReadOnlyList<RankingComponent> Components,
    bool SharedByGrant = false,
    Guid? PermittingGrantId = null);

/// <summary>Why a candidate the search returned was not ranked.</summary>
public enum RetrievalExclusionReason
{
    /// <summary>Its status is not one of the eligible ones. Only <see cref="ExperienceStatus.Validated"/> and <see cref="ExperienceStatus.Reinforced"/> are.</summary>
    IneligibleStatus,

    /// <summary>Its <see cref="ExperienceRecord.UpdatedAt"/> is older than the policy's <see cref="RetrievalPolicy.MaxAge"/>.</summary>
    Expired,

    /// <summary>A required environment attribute differs, or the record does not carry the key at all.</summary>
    EnvironmentMismatch,
}

/// <summary>
/// One candidate that an eligibility check removed before ranking, named so a host can tell "nothing
/// matched" apart from "something matched but was not reusable here".
/// </summary>
/// <param name="ExperienceId">The excluded record.</param>
/// <param name="Reason">Which check excluded it.</param>
public sealed record ExcludedExperience(Guid ExperienceId, RetrievalExclusionReason Reason);

/// <summary>What a retrieval call ended as.</summary>
public enum RetrievalOutcome
{
    /// <summary>The search ran inside the timeout and the result holds every eligible record it found, ranked.</summary>
    Completed,

    /// <summary>The call exceeded the policy's timeout. The result is empty; nothing about it is an error.</summary>
    TimedOut,

    /// <summary>The request scope lies outside the host-established authorization. No search was issued.</summary>
    Denied,

    /// <summary>The search failed (for example the database was unavailable, or a stored record could not be read). The result is empty, never unfiltered.</summary>
    Failed,
}

/// <summary>
/// Why a retrieval failed. <see cref="Reason"/> is safe to log or surface: it never carries record
/// content. <see cref="Exception"/> is <em>not</em> held to that standard -- it is whatever the port
/// threw, and a driver's message can quote SQL text, parameter values, or connection detail. Treat it
/// as local diagnostics only, and do not copy it into a user-visible response or a shared log without
/// deciding that yourself.
/// </summary>
/// <param name="Reason">A human-readable, content-free explanation.</param>
/// <param name="Exception">The original failure, when one was caught. Diagnostic only; may carry adapter detail.</param>
public sealed record RetrievalFailure(string Reason, Exception? Exception);

/// <summary>
/// Why a retrieval answered from the text channel alone. Every value is an explicit statement that
/// the vector channel did <em>not</em> contribute -- none of them is ever inferred from an empty
/// vector result, because "nothing was semantically similar" and "the vector channel could not be
/// trusted" are different claims and only the second one should make a host look at its wiring.
/// </summary>
public enum TextOnlyReason
{
    /// <summary>
    /// No embedding index or no embedding generator was wired in, so there is no vector channel at
    /// all. This is a configuration fact, not a failure: a text-only deployment is a supported one.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The embedding provider could not produce a query vector -- it threw, timed out, or returned
    /// something unusable -- so no vector comparison was possible. The text channel still answered.
    /// </summary>
    ProviderUnavailable,

    /// <summary>
    /// The scope's stored embeddings come from a different model than the query vector, so none of
    /// them is comparable with it. No vector comparison was attempted.
    /// </summary>
    ModelMismatch,

    /// <summary>
    /// The scope's stored embeddings are from this model but at a different width, so none of them is
    /// comparable with the query vector. No vector comparison was attempted.
    /// </summary>
    DimensionMismatch,

    /// <summary>
    /// The vector search itself failed, was denied, or was refused as malformed. The text channel
    /// still answered, and its candidates are still returned.
    /// </summary>
    VectorSearchFailed,
}

/// <summary>
/// The explicit text-only signal: present exactly when the vector channel contributed nothing to a
/// result, with the reason it did not. It is deliberately not an error -- a retrieval that fell back
/// to text is a complete, usable answer, just a narrower one -- but it is always stated rather than
/// left to be inferred from an empty vector match.
/// </summary>
/// <param name="Reason">Which of the documented fallbacks applied.</param>
/// <param name="Detail">A human-readable, content-free explanation. Safe to log or surface.</param>
/// <param name="Exception">
/// The original failure, when one was caught. As with <see cref="RetrievalFailure.Exception"/> this
/// is <em>not</em> content-free -- a driver or HTTP client message can quote SQL text, parameters, or
/// a request body. Treat it as local diagnostics only.
/// </param>
public sealed record VectorChannelFallback(TextOnlyReason Reason, string Detail, Exception? Exception);

/// <summary>
/// The result of a retrieval call. It is always a complete answer: an empty
/// <see cref="Records"/> list with a non-<see cref="RetrievalOutcome.Completed"/>
/// <see cref="Outcome"/> means retrieval declined to answer, never that the caller may proceed with
/// unfiltered experience.
/// </summary>
/// <param name="Outcome">What the call ended as.</param>
/// <param name="Records">The eligible records in rank order, highest score first, ties broken by <see cref="ExperienceRecord.ExperienceId"/> ascending. Empty unless <see cref="Outcome"/> is <see cref="RetrievalOutcome.Completed"/>, and bounded by the request's limit and by <see cref="Truncated"/>'s ceiling.</param>
/// <param name="Excluded">
/// Candidates an eligibility check <em>in Core</em> removed before ranking, with the check that
/// removed each. It is not a complete account of everything that was filtered: scope, status, and the
/// reuse-confidence floor are applied in the database, so records they exclude never reach Core and are
/// never itemized here.
/// </param>
/// <param name="Truncated">
/// <see langword="true"/> when the search hit the policy's candidate ceiling and more matching records
/// existed than were considered. Because the search orders by text relevance before cutting, the
/// records beyond the ceiling are simply not ranked -- they are not in <see cref="Excluded"/> either,
/// and one of them may well have outranked what came back. Treat it as "this answer is partial".
/// </param>
/// <param name="EnvironmentUnrestricted">
/// <see langword="true"/> when the request named no required environment attributes, so every
/// candidate passed that check unconditionally. This is explicit rather than inferred: "no
/// environment requirement" and "every requirement happened to match" are different claims.
/// </param>
/// <param name="CorrelationId">The request's correlation identifier, echoed back on every outcome including <see cref="RetrievalOutcome.TimedOut"/>.</param>
/// <param name="Elapsed">How long the call took, measured with the service's <see cref="TimeProvider"/>.</param>
/// <param name="Failure">Why the call failed, when <see cref="Outcome"/> is <see cref="RetrievalOutcome.Failed"/>; otherwise <see langword="null"/>.</param>
/// <param name="VectorFallback">
/// Why the vector channel contributed nothing, when it did not; <see langword="null"/> when both
/// channels ran. Present even on a perfectly good text-only answer, because "this deployment has no
/// vector channel" and "the vector channel could not be trusted this time" are things a host has to
/// be able to tell apart.
/// </param>
public sealed record ExperienceRetrievalResult(
    RetrievalOutcome Outcome,
    IReadOnlyList<RankedExperience> Records,
    IReadOnlyList<ExcludedExperience> Excluded,
    bool Truncated,
    bool EnvironmentUnrestricted,
    string? CorrelationId,
    TimeSpan Elapsed,
    RetrievalFailure? Failure,
    VectorChannelFallback? VectorFallback = null)
{
    /// <summary>
    /// The timeout signal: <see langword="true"/> exactly when the call ran out of time. A timeout is
    /// never an exception and is never reported as a failure, so a host can tell "too slow this time"
    /// from "something is broken".
    /// </summary>
    public bool TimedOut => Outcome is RetrievalOutcome.TimedOut;

    /// <summary>
    /// The text-only signal: <see langword="true"/> exactly when <see cref="VectorFallback"/> is
    /// present, that is, when this answer came from the text channel alone. It is never true merely
    /// because the vector channel matched nothing.
    /// </summary>
    public bool TextOnly => VectorFallback is not null;
}
