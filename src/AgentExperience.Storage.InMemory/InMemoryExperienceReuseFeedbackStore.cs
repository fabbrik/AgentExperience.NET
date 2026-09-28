using AgentExperience.Abstractions;

namespace AgentExperience.Storage.InMemory;

/// <summary>
/// An <see cref="IExperienceReuseFeedbackStore"/> held in process memory. <b>For development and tests only.</b>
/// Every submission is lost when the process ends, and none of the PostgreSQL store's guarantees apply: no
/// database-enforced append-only ledger, no erasure reach, no backups, no two database roles, no crypto-shredding.
/// </summary>
/// <remarks>
/// <para>
/// It keeps the ledger's rules and passes the same conformance suite as the PostgreSQL store: every submission is
/// validated with that store's own rules; the feedback ID is the idempotency key, so an identical resubmission is
/// <see cref="ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded"/> and one differing in any stored field (the
/// exposures included) is <see cref="ExperienceReuseFeedbackStoreOutcome.Conflict"/> with nothing written; and a
/// conflicting submission is handed back only when the caller has authority over its scope.
/// </para>
/// <para>
/// Thread-safe: each submission is recorded atomically under one lock. A submission is deep-copied on write into a
/// snapshot with read-only collections and its timestamps in UTC, truncated to whole microseconds (as the PostgreSQL
/// ledger stores them), and that snapshot is what every result hands back. It is independent of the record store, like the PostgreSQL
/// ledger, and checks nothing about the exposed records.
/// </para>
/// </remarks>
public sealed class InMemoryExperienceReuseFeedbackStore : IExperienceReuseFeedbackStore
{
    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly object _gate = new();
    private readonly Dictionary<Guid, RecordedExperienceReuseFeedback> _submissions = [];

    /// <inheritdoc />
    public Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
        AuthorizationContext authorization,
        RecordedExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(feedback);

        var errors = ExperienceRecordValidator.ValidateReuseFeedback(feedback);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Invalid, null, errors));
        }

        if (!authorization.Permits(feedback.Scope))
        {
            return Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Denied, null, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceReuseFeedbackStoreResult>(cancellationToken);
        }

        // Copied before the lock, with its timestamps normalized as the PostgreSQL ledger stores and compares them, so
        // nothing stored or compared is an instance the caller can still change.
        var submitted = StoredSnapshots.Feedback(feedback);

        lock (_gate)
        {
            if (_submissions.TryGetValue(submitted.FeedbackId, out var stored))
            {
                if (SameContent(stored, submitted))
                {
                    return Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, stored, NoErrors));
                }

                // A colliding ID never hands back content from a scope the caller has no authority over.
                return Task.FromResult(new ExperienceReuseFeedbackStoreResult(
                    ExperienceReuseFeedbackStoreOutcome.Conflict,
                    authorization.Permits(stored.Scope) ? stored : null,
                    NoErrors));
            }

            _submissions.Add(submitted.FeedbackId, submitted);
            return Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Recorded, submitted, NoErrors));
        }
    }

    /// <summary>
    /// Whether the stored submission is the one in hand: every stored field, compared explicitly as the PostgreSQL
    /// store compares it, with the two lists compared as sequences rather than by reference.
    /// </summary>
    private static bool SameContent(RecordedExperienceReuseFeedback stored, RecordedExperienceReuseFeedback submitted) =>
        stored.RunId == submitted.RunId
        && stored.Scope == submitted.Scope
        && stored.RunOutcome == submitted.RunOutcome
        && stored.ClaimedBenefit == submitted.ClaimedBenefit
        && stored.Benefit == submitted.Benefit
        && stored.AttributionSource == submitted.AttributionSource
        && string.Equals(stored.ReviewerIdentity, submitted.ReviewerIdentity, StringComparison.Ordinal)
        && string.Equals(stored.EvaluatorId, submitted.EvaluatorId, StringComparison.Ordinal)
        && stored.VerificationRoundId == submitted.VerificationRoundId
        && stored.AssessmentId == submitted.AssessmentId
        && string.Equals(stored.Rationale, submitted.Rationale, StringComparison.Ordinal)
        && stored.EvidenceIds.SequenceEqual(submitted.EvidenceIds)
        && stored.AttributedAt == submitted.AttributedAt
        && string.Equals(stored.Measure.Kind, submitted.Measure.Kind, StringComparison.Ordinal)
        && stored.Measure.Value.Equals(submitted.Measure.Value)
        && string.Equals(stored.TrialLabel, submitted.TrialLabel, StringComparison.Ordinal)
        && stored.ObservedAt == submitted.ObservedAt
        && stored.Exposures.SequenceEqual(submitted.Exposures);
}
