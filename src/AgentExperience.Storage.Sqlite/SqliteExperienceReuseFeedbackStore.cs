using AgentExperience.Abstractions;
using static AgentExperience.Storage.Sqlite.SqliteDatabase;

namespace AgentExperience.Storage.Sqlite;

/// <summary>
/// An <see cref="IExperienceReuseFeedbackStore"/> in the SQLite database: the PostgreSQL ledger's rules, and its
/// conformance suite. Every submission is validated with that store's own rules; the feedback ID is the idempotency key,
/// so an identical resubmission is <see cref="ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded"/> and one differing in
/// any stored field (the set of exposures included; their order is normalized by record ID first) is
/// <see cref="ExperienceReuseFeedbackStoreOutcome.Conflict"/> with nothing written; a conflicting submission is handed
/// back only when the caller has authority over its scope. Timestamps are kept in UTC, truncated to whole microseconds.
/// </summary>
internal sealed class SqliteExperienceReuseFeedbackStore(SqliteDatabase database) : IExperienceReuseFeedbackStore
{
    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    /// <summary>Creates a ledger in the database <paramref name="connectionString"/> names.</summary>
    public SqliteExperienceReuseFeedbackStore(string connectionString)
        : this(new SqliteDatabase(connectionString))
    {
    }

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

        var normalized = ExperienceRecordValidator.NormalizeReuseFeedback(feedback);
        var submitted = normalized with
        {
            EvidenceIds = [.. normalized.EvidenceIds],
            Exposures = [.. normalized.Exposures],
            AttributedAt = normalized.AttributedAt is { } attributedAt ? Micro(attributedAt) : null,
            ObservedAt = Micro(normalized.ObservedAt),
        };

        return database.WriteAsync(
            "The SQLite reuse feedback could not be recorded.",
            async (connection, transaction) =>
            {
                var existing = (string?)await ScalarAsync(
                    connection,
                    transaction,
                    "SELECT feedback FROM experience_reuse_feedback WHERE feedback_id = $id",
                    cancellationToken,
                    ("$id", Id(submitted.FeedbackId))).ConfigureAwait(false);
                if (existing is not null)
                {
                    var stored = FromJson<RecordedExperienceReuseFeedback>(existing);
                    if (SameContent(stored, submitted))
                    {
                        return (new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, stored, NoErrors), false);
                    }

                    // A colliding ID never hands back content from a scope the caller has no authority over.
                    return (new ExperienceReuseFeedbackStoreResult(
                        ExperienceReuseFeedbackStoreOutcome.Conflict,
                        authorization.Permits(stored.Scope) ? stored : null,
                        NoErrors), false);
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO experience_reuse_feedback (feedback_id, {ScopeColumns}, feedback) VALUES ($id, {ScopeValues}, $feedback)",
                    cancellationToken,
                    [("$id", Id(submitted.FeedbackId)), ("$feedback", ToJson(submitted)), .. ScopeParameters(submitted.Scope)]).ConfigureAwait(false);
                return (new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Recorded, submitted, NoErrors), true);
            },
            cancellationToken);
    }

    /// <summary>Whether the stored submission is the one in hand: every stored field, the two lists as sequences.</summary>
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
