using AgentExperience.Abstractions;

namespace AgentExperience.Sample.EndToEnd.Doubles;

/// <summary>
/// A demonstration double's structural validation, not the adapter's: the subset of
/// <c>AgentExperience.Storage.Postgres</c>'s <c>ExperienceRecordValidator</c> that the sample's own
/// records and lifecycle events could violate.
/// </summary>
/// <remarks>
/// <para>
/// The real validator is <see langword="internal"/> to its package and visible only to that
/// package's tests and to the vectors package, so the sample cannot call it. Restating a subset is
/// the honest alternative to skipping validation altogether: a double that accepts a record the
/// PostgreSQL adapter would refuse as <see cref="ExperienceStoreOutcome.Invalid"/> would let the
/// sample's default mode narrate a persistence that the opt-in mode would have rejected.
/// </para>
/// <para>
/// Like the real one, it collects every error rather than stopping at the first, reports a field
/// path with a content-free message, and runs before authorization and before any store access.
/// </para>
/// </remarks>
internal static class SampleRecordValidation
{
    /// <summary>Validates a record about to be created. Empty means valid.</summary>
    /// <param name="record">The record the finalization service built.</param>
    public static IReadOnlyList<StoreValidationError> ValidateRecord(ExperienceRecord record)
    {
        var errors = new List<StoreValidationError>();

        if (record.ExperienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        if (record.SourceRunId == Guid.Empty)
        {
            errors.Add(new("SourceRunId", "must not be an empty GUID."));
        }

        ValidateScope(record.Scope, "Scope", errors);
        RequireNotBlank(record.TaskId, "TaskId", errors);
        RequireUnitInterval(record.CompletionScore, "CompletionScore", errors);
        RequireUnitInterval(record.ReuseConfidence, "ReuseConfidence", errors);
        RequireDefined(record.Status, "Status", errors);

        if (record.SupportingValidations < 0)
        {
            errors.Add(new("SupportingValidations", "must not be negative."));
        }

        if (record.Contradictions < 0)
        {
            errors.Add(new("Contradictions", "must not be negative."));
        }

        if (record.Revision < 0)
        {
            errors.Add(new("Revision", "must not be negative."));
        }

        for (var i = 0; i < record.Attempts.Count; i++)
        {
            var attempt = record.Attempts[i];
            if (attempt.AttemptId == Guid.Empty)
            {
                errors.Add(new($"Attempts[{i}].AttemptId", "must not be an empty GUID."));
            }

            if (attempt.SequenceNumber != i)
            {
                errors.Add(new($"Attempts[{i}].SequenceNumber", "must be the attempt's zero-based position in the run."));
            }
        }

        return errors;
    }

    /// <summary>Validates a lifecycle event about to be committed. Empty means valid.</summary>
    /// <param name="scope">The scope the commit is made under.</param>
    /// <param name="lifecycleEvent">The transition Core decided.</param>
    /// <param name="authorization">The caller's authorization, which a confidence update is recorded against.</param>
    public static IReadOnlyList<StoreValidationError> ValidateLifecycleEvent(
        Scope scope,
        LifecycleEvent lifecycleEvent,
        AuthorizationContext authorization)
    {
        var errors = new List<StoreValidationError>();

        if (lifecycleEvent.Confidence is not null && string.IsNullOrWhiteSpace(authorization.PrincipalId))
        {
            errors.Add(new(
                "Authorization.PrincipalId",
                "must be non-blank for a confidence update: it is the actor the update is recorded against."));
        }

        if (lifecycleEvent.EventId == Guid.Empty)
        {
            errors.Add(new("EventId", "must not be an empty GUID."));
        }

        if (lifecycleEvent.ExperienceRecordId == Guid.Empty)
        {
            errors.Add(new("ExperienceRecordId", "must not be an empty GUID."));
        }

        ValidateScope(scope, "Scope", errors);

        if (lifecycleEvent.PriorStatus is { } priorStatus)
        {
            RequireDefined(priorStatus, "PriorStatus", errors);
        }

        RequireDefined(lifecycleEvent.CurrentStatus, "CurrentStatus", errors);
        RequireNotBlank(lifecycleEvent.Reason, "Reason", errors);
        RequireNotBlank(lifecycleEvent.Producer, "Producer", errors);

        if (lifecycleEvent.OccurredAt == default)
        {
            // Part of the event's stored identity, so an unset value would silently become part of the
            // idempotency key.
            errors.Add(new("OccurredAt", "must be set to when the transition occurred."));
        }

        if (lifecycleEvent.ExpectedRevision < 0)
        {
            errors.Add(new("ExpectedRevision", "must not be negative."));
        }

        if (lifecycleEvent.ReplacementExperienceId is { } replacementId)
        {
            if (lifecycleEvent.Confidence is not null)
            {
                errors.Add(new(
                    "ReplacementExperienceId",
                    "must be null on an event that carries a confidence update; supersession and evidence are separate transitions."));
            }

            if (lifecycleEvent.CurrentStatus != ExperienceStatus.Superseded)
            {
                errors.Add(new("ReplacementExperienceId", "may only be set on a transition to Superseded."));
            }

            if (replacementId == lifecycleEvent.ExperienceRecordId)
            {
                errors.Add(new("ReplacementExperienceId", "must not be the record being superseded."));
            }
        }

        return errors;
    }

    private static void ValidateScope(Scope? scope, string path, List<StoreValidationError> errors)
    {
        if (scope is null)
        {
            errors.Add(new(path, "is required."));
            return;
        }

        RequireNotBlank(scope.TenantId, path + ".TenantId", errors);
        RequireNotBlank(scope.ApplicationId, path + ".ApplicationId", errors);
        RequireNotBlank(scope.ProjectId, path + ".ProjectId", errors);
    }

    private static void RequireNotBlank(string? value, string path, List<StoreValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(path, "must not be empty or whitespace."));
        }
    }

    private static void RequireUnitInterval(double value, string path, List<StoreValidationError> errors)
    {
        if (double.IsNaN(value) || value < 0d || value > 1d)
        {
            errors.Add(new(path, "must be a number in [0, 1]."));
        }
    }

    private static void RequireDefined<TEnum>(TEnum value, string path, List<StoreValidationError> errors)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            errors.Add(new(path, "must be a defined value of its enumeration."));
        }
    }
}
