using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Structural validation run before authorization and before any database access. Collects every
/// error (never stops at the first) with a field path and a content-free message. Rejects nulls in
/// non-nullable members so a stored payload is always readable back.
/// </summary>
internal static class ExperienceRecordValidator
{
    private const string Required = "is required.";
    private const string NotBlank = "must not be empty or whitespace.";

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
        ValidateAttempts(record.Attempts, errors);
        ValidateOutcome(record.Outcome, errors);
        RequireUnitInterval(record.CompletionScore, "CompletionScore", errors);

        if (record.Reflection is not null)
        {
            ValidateReflection(record.Reflection, errors);
        }

        ValidateEnvironment(record.Environment, errors);
        ValidateProvenance(record.Provenance, errors);
        RequireDefined(record.Status, "Status", errors);
        RequireUnitInterval(record.ReuseConfidence, "ReuseConfidence", errors);

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

        return errors;
    }

    public static IReadOnlyList<StoreValidationError> ValidateGet(Scope scope, Guid experienceId)
    {
        var errors = new List<StoreValidationError>();
        if (experienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>
    /// Validates a lifecycle commit: the event's own fields plus the request scope the record must lie
    /// in. Field paths name the <see cref="LifecycleEvent"/> member, so a caller can map an error back
    /// to what it supplied.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateLifecycleEvent(Scope scope, LifecycleEvent lifecycleEvent)
    {
        var errors = new List<StoreValidationError>();

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
            // OccurredAt is part of the event's stored identity, so an unset value would silently become
            // part of the idempotency key. No upper bound: clock skew makes a future check unsafe.
            errors.Add(new("OccurredAt", "must be set to when the transition occurred."));
        }

        if (lifecycleEvent.ExpectedRevision < 0)
        {
            errors.Add(new("ExpectedRevision", "must not be negative."));
        }
        else if (lifecycleEvent.ExpectedRevision >= long.MaxValue - 1)
        {
            // A successful commit stores ExpectedRevision + 1, which would wrap silently at long.MaxValue.
            // One below it is rejected too: committing there would leave the record permanently stuck,
            // because every later commit would expect an unrepresentable revision.
            errors.Add(new("ExpectedRevision", "must leave room for the next revision, so a later commit stays possible."));
        }

        return errors;
    }

    public static IReadOnlyList<StoreValidationError> ValidateQuery(ExperienceRecordQuery query)
    {
        var errors = new List<StoreValidationError>();
        ValidateScope(query.Scope, "Scope", errors);

        if (query.Statuses is not null)
        {
            if (query.Statuses.Count == 0)
            {
                errors.Add(new("Statuses", "must be null (all statuses) or contain at least one status."));
            }

            for (var i = 0; i < query.Statuses.Count; i++)
            {
                RequireDefined(query.Statuses[i], $"Statuses[{i}]", errors);
            }
        }

        if (query.Limit is < ExperienceRecordQuery.MinLimit or > ExperienceRecordQuery.MaxLimit)
        {
            errors.Add(new("Limit", $"must be between {ExperienceRecordQuery.MinLimit} and {ExperienceRecordQuery.MaxLimit}."));
        }

        return errors;
    }

    private static void ValidateScope(Scope? scope, string path, List<StoreValidationError> errors)
    {
        if (scope is null)
        {
            errors.Add(new(path, Required));
            return;
        }

        RequireNotBlank(scope.TenantId, $"{path}.TenantId", errors);
        RequireNotBlank(scope.ApplicationId, $"{path}.ApplicationId", errors);
        RequireNotBlank(scope.ProjectId, $"{path}.ProjectId", errors);
        RequireNullOrNotBlank(scope.TeamId, $"{path}.TeamId", errors);
        RequireNullOrNotBlank(scope.AgentId, $"{path}.AgentId", errors);
        RequireNullOrNotBlank(scope.UserId, $"{path}.UserId", errors);
    }

    private static void ValidateAttempts(IReadOnlyList<Attempt>? attempts, List<StoreValidationError> errors)
    {
        if (attempts is null)
        {
            errors.Add(new("Attempts", Required));
            return;
        }

        for (var i = 0; i < attempts.Count; i++)
        {
            var attemptPath = $"Attempts[{i}]";
            var attempt = attempts[i];
            if (attempt is null)
            {
                errors.Add(new(attemptPath, Required));
                continue;
            }

            if (attempt.ToolCalls is null)
            {
                errors.Add(new($"{attemptPath}.ToolCalls", Required));
                continue;
            }

            for (var j = 0; j < attempt.ToolCalls.Count; j++)
            {
                var toolPath = $"{attemptPath}.ToolCalls[{j}]";
                var toolCall = attempt.ToolCalls[j];
                if (toolCall is null)
                {
                    errors.Add(new(toolPath, Required));
                    continue;
                }

                RequireNotNull(toolCall.ToolName, $"{toolPath}.ToolName", errors);
                RequireNotNull(toolCall.Arguments, $"{toolPath}.Arguments", errors);
            }
        }
    }

    private static void ValidateOutcome(Outcome? outcome, List<StoreValidationError> errors)
    {
        if (outcome is null)
        {
            errors.Add(new("Outcome", Required));
            return;
        }

        RequireDefined(outcome.Status, "Outcome.Status", errors);

        if (outcome.Evidence is null)
        {
            errors.Add(new("Outcome.Evidence", Required));
            return;
        }

        for (var i = 0; i < outcome.Evidence.Count; i++)
        {
            var path = $"Outcome.Evidence[{i}]";
            var evidence = outcome.Evidence[i];
            if (evidence is null)
            {
                errors.Add(new(path, Required));
                continue;
            }

            RequireNotNull(evidence.ArtifactRevision, $"{path}.ArtifactRevision", errors);
            RequireNotNull(evidence.CheckId, $"{path}.CheckId", errors);
            RequireNotNull(evidence.Kind, $"{path}.Kind", errors);
            RequireDefined(evidence.Result, $"{path}.Result", errors);
            RequireNotNull(evidence.Producer, $"{path}.Producer", errors);
        }
    }

    private static void ValidateReflection(Reflection reflection, List<StoreValidationError> errors)
    {
        RequireNotNull(reflection.Lesson, "Reflection.Lesson", errors);
        RequireStringList(reflection.SuccessfulApproaches, "Reflection.SuccessfulApproaches", errors);
        RequireStringList(reflection.FailedApproaches, "Reflection.FailedApproaches", errors);
        RequireStringList(reflection.Preconditions, "Reflection.Preconditions", errors);
        RequireStringList(reflection.Warnings, "Reflection.Warnings", errors);
        RequireNotNull(reflection.EvidenceIds, "Reflection.EvidenceIds", errors);
        RequireDefined(reflection.VerificationStatus, "Reflection.VerificationStatus", errors);
        RequireUnitInterval(reflection.CompletionScore, "Reflection.CompletionScore", errors);
        RequireNotNull(reflection.VerificationRuleVersion, "Reflection.VerificationRuleVersion", errors);
        RequireNotNull(reflection.Producer, "Reflection.Producer", errors);
    }

    private static void ValidateEnvironment(EnvironmentFingerprint? environment, List<StoreValidationError> errors)
    {
        if (environment is null)
        {
            errors.Add(new("Environment", Required));
            return;
        }

        RequireNotNull(environment.HostName, "Environment.HostName", errors);
        RequireNotNull(environment.RuntimeVersion, "Environment.RuntimeVersion", errors);
        RequireNotNull(environment.OperatingSystem, "Environment.OperatingSystem", errors);

        if (environment.Metadata is null)
        {
            errors.Add(new("Environment.Metadata", Required));
            return;
        }

        // The path deliberately omits the key: metadata keys are payload content.
        if (environment.Metadata.Values.Any(value => value is null))
        {
            errors.Add(new("Environment.Metadata", "must not contain null values."));
        }
    }

    private static void ValidateProvenance(Provenance? provenance, List<StoreValidationError> errors)
    {
        if (provenance is null)
        {
            errors.Add(new("Provenance", Required));
            return;
        }

        RequireNotNull(provenance.Source, "Provenance.Source", errors);
    }

    private static void RequireStringList(IReadOnlyList<string>? values, string path, List<StoreValidationError> errors)
    {
        if (values is null)
        {
            errors.Add(new(path, Required));
            return;
        }

        for (var i = 0; i < values.Count; i++)
        {
            RequireNotNull(values[i], $"{path}[{i}]", errors);
        }
    }

    private static void RequireNotNull(object? value, string path, List<StoreValidationError> errors)
    {
        if (value is null)
        {
            errors.Add(new(path, Required));
        }
    }

    private static void RequireNotBlank(string? value, string path, List<StoreValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(path, NotBlank));
        }
        else
        {
            RequireNoNul(value, path, errors);
        }
    }

    private static void RequireNullOrNotBlank(string? value, string path, List<StoreValidationError> errors)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(path, "must be null or a non-blank value."));
        }
        else if (value is not null)
        {
            RequireNoNul(value, path, errors);
        }
    }

    private static void RequireNoNul(string value, string path, List<StoreValidationError> errors)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            errors.Add(new(path, "must not contain the NUL character (U+0000)."));
        }
    }

    private static void RequireUnitInterval(double value, string path, List<StoreValidationError> errors)
    {
        if (!(value >= 0d && value <= 1d))
        {
            errors.Add(new(path, "must be between 0 and 1 inclusive."));
        }
    }

    private static void RequireDefined<TEnum>(TEnum value, string path, List<StoreValidationError> errors)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            errors.Add(new(path, "is not a defined value."));
        }
    }
}
