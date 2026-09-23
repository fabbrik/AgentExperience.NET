using AgentExperience.Abstractions;

namespace AgentExperience.Sample.EndToEnd.Doubles;

/// <summary>
/// A demonstration double, not a durable ledger: reuse-feedback rows held in a dictionary that dies
/// with the process.
/// </summary>
/// <remarks>
/// It keeps the two properties the real ledger's behaviour rests on, so the sample's outcome means
/// what it says: the feedback ID is the idempotency key, and a resubmission under the same ID with
/// different content is a <see cref="ExperienceReuseFeedbackStoreOutcome.Conflict"/> that writes
/// nothing rather than an overwrite.
/// </remarks>
internal sealed class InMemoryReuseFeedbackStore : IExperienceReuseFeedbackStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RecordedExperienceReuseFeedback> _rows = [];

    /// <summary>Every submission recorded so far, keyed by feedback ID.</summary>
    public IReadOnlyDictionary<Guid, RecordedExperienceReuseFeedback> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows.ToDictionary();
            }
        }
    }

    /// <inheritdoc />
    public Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
        AuthorizationContext authorization,
        RecordedExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(feedback);

        lock (_gate)
        {
            if (!authorization.Permits(feedback.Scope))
            {
                return Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Denied, null, []));
            }

            if (_rows.TryGetValue(feedback.FeedbackId, out var existing))
            {
                return Task.FromResult(SameSubmission(existing, feedback)
                    ? new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, existing, [])
                    : new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Conflict, existing, []));
            }

            _rows[feedback.FeedbackId] = feedback;
            return Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Recorded, feedback, []));
        }
    }

    /// <summary>
    /// Whether two submissions under one feedback ID say the same thing. Compared field by field
    /// rather than with record equality, because the exposure list is a collection and a record's
    /// generated equality would compare it by reference and call every retry a conflict.
    /// </summary>
    private static bool SameSubmission(RecordedExperienceReuseFeedback stored, RecordedExperienceReuseFeedback incoming) =>
        stored.RunId == incoming.RunId
        && stored.Scope == incoming.Scope
        && stored.RunOutcome == incoming.RunOutcome
        && stored.ClaimedBenefit == incoming.ClaimedBenefit
        && stored.Benefit == incoming.Benefit
        && stored.AttributionSource == incoming.AttributionSource
        && stored.Measure == incoming.Measure
        && stored.ObservedAt == incoming.ObservedAt
        && stored.TrialLabel == incoming.TrialLabel
        && stored.Exposures.Select(e => e.ExperienceId).SequenceEqual(incoming.Exposures.Select(e => e.ExperienceId));
}
