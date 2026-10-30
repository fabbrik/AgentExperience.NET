using AgentExperience.Abstractions;

// Shared source (story 28.7): the run store's validation rules, beside the core ports' (ExperienceRecordValidator.Shared.cs,
// whose private helpers these reuse: the same class, another part). AgentExperience.Storage.Postgres and
// AgentExperience.Storage.Sqlite link this file, so both run stores refuse exactly the same requests.
#if AGENTEXPERIENCE_POSTGRES && AGENTEXPERIENCE_SQLITE
#error Define exactly one of AGENTEXPERIENCE_POSTGRES and AGENTEXPERIENCE_SQLITE.
#elif AGENTEXPERIENCE_POSTGRES
namespace AgentExperience.Storage.Postgres;
#elif AGENTEXPERIENCE_SQLITE
namespace AgentExperience.Storage.Sqlite;
#else
#error Define AGENTEXPERIENCE_POSTGRES or AGENTEXPERIENCE_SQLITE to select the namespace of the shared run validator.
#endif

/// <summary>The run store's request validation, collecting every error with a field path and a content-free message.</summary>
internal static partial class ExperienceRecordValidator
{
    /// <summary>
    /// Validates a run to save: a completed run (execution status and end set), with a run ID, a scope, a task ID, its
    /// environment, provenance and attempts as a record holds them, and a completion event ID.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateRunSave(ExperienceRun? run, Guid completionEventId)
    {
        var errors = new List<StoreValidationError>();
        if (completionEventId == Guid.Empty)
        {
            errors.Add(new("CompletionEventId", "must not be an empty GUID."));
        }

        if (run is null)
        {
            errors.Add(new("Run", Required));
            return errors;
        }

        if (run.RunId == Guid.Empty)
        {
            errors.Add(new("Run.RunId", "must not be an empty GUID."));
        }

        ValidateScope(run.Scope, "Run.Scope", errors);
        RequireNotBlank(run.TaskId, "Run.TaskId", errors);
        if (run.TaskDescription is not null)
        {
            RequireWellFormedUtf16(run.TaskDescription, "Run.TaskDescription", errors);
        }

        ValidateAttempts(run.Attempts, errors);
        ValidateEnvironment(run.Environment, errors);
        ValidateProvenance(run.Provenance, errors);
        if (run.Outcome is not null)
        {
            ValidateOutcome(run.Outcome, errors);
        }

        if (run.ExecutionStatus is not { } status)
        {
            errors.Add(new("Run.ExecutionStatus", "must be set: only a completed run is stored."));
        }
        else
        {
            RequireDefined(status, "Run.ExecutionStatus", errors);
        }

        if (run.StartedAt == default)
        {
            errors.Add(new("Run.StartedAt", "must be set."));
        }

        if (run.EndedAt is not { } ended || ended == default)
        {
            errors.Add(new("Run.EndedAt", "must be set: only a completed run is stored."));
        }

        return errors;
    }

    /// <summary>Validates a read of one run by ID.</summary>
    public static IReadOnlyList<StoreValidationError> ValidateRunId(Guid runId)
    {
        var errors = new List<StoreValidationError>();
        if (runId == Guid.Empty)
        {
            errors.Add(new("RunId", "must not be an empty GUID."));
        }

        return errors;
    }

    /// <summary>Validates a write to one run in an exact scope, and, for marking it finalized, the instant.</summary>
    public static IReadOnlyList<StoreValidationError> ValidateRunInScope(Scope scope, Guid runId, DateTimeOffset? finalizedAt = null, bool requireInstant = false)
    {
        var errors = new List<StoreValidationError>();
        if (runId == Guid.Empty)
        {
            errors.Add(new("RunId", "must not be an empty GUID."));
        }

        ValidateScope(scope, "Scope", errors);
        if (requireInstant && (finalizedAt is not { } at || at == default))
        {
            errors.Add(new("FinalizedAt", "must be set."));
        }

        return errors;
    }

    /// <summary>Validates a page query: the scope, a limit in range, no empty cursor.</summary>
    public static IReadOnlyList<StoreValidationError> ValidateRunQuery(ExperienceRunQuery? query)
    {
        var errors = new List<StoreValidationError>();
        if (query is null)
        {
            errors.Add(new("Query", Required));
            return errors;
        }

        ValidateScope(query.Scope, "Scope", errors);
        if (query.Limit is < ExperienceRunQuery.MinLimit or > ExperienceRunQuery.MaxLimit)
        {
            errors.Add(new("Limit", $"must be between {ExperienceRunQuery.MinLimit} and {ExperienceRunQuery.MaxLimit}."));
        }

        if (query.AfterRunId == Guid.Empty)
        {
            errors.Add(new("AfterRunId", "must be null or name a run, never an empty GUID."));
        }

        if (query.EndedBefore is { } before && before == default)
        {
            errors.Add(new("EndedBefore", "must be null or set."));
        }

        return errors;
    }
}
