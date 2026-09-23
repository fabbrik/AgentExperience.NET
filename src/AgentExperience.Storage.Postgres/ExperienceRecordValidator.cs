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

        ValidateCreatedConfidence(record, errors);

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
    /// Validates an erasure: the scope, the record, and the optional expected revision. A negative
    /// revision is refused rather than treated as "any", because a caller that computed one is asking
    /// for something it did not mean -- and the thing it is asking for here is destructive.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateDelete(Scope scope, Guid experienceId, long? expectedRevision)
    {
        var errors = new List<StoreValidationError>();
        if (experienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        if (expectedRevision is < 0)
        {
            errors.Add(new("ExpectedRevision", "must not be negative."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>
    /// Validates a retention sweep: the scope, the age, and the batch bound. A non-positive age is
    /// refused rather than read as "delete everything": retention is indefinite until a host names a
    /// span, and a zero or negative one is the shape a misconfigured setting takes.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateRetentionSweep(Scope scope, TimeSpan retentionAge, int batchSize)
    {
        var errors = new List<StoreValidationError>();

        if (retentionAge <= TimeSpan.Zero)
        {
            errors.Add(new("RetentionAge", "must be strictly positive; there is no retention age that means 'delete everything'."));
        }

        if (batchSize is < PostgresExperienceRecordStore.MinSweepBatchSize or > PostgresExperienceRecordStore.MaxSweepBatchSize)
        {
            errors.Add(new(
                "BatchSize",
                $"must be between {PostgresExperienceRecordStore.MinSweepBatchSize} and {PostgresExperienceRecordStore.MaxSweepBatchSize}."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>
    /// Validates an expired-grant purge: the owner scope and the batch bound. There is no age
    /// parameter, because a grant carries its own: it is collected once its stored expiry has passed.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateGrantPurge(Scope recordScope, int batchSize)
    {
        var errors = new List<StoreValidationError>();

        if (batchSize is < PostgresExperienceRecordStore.MinSweepBatchSize or > PostgresExperienceRecordStore.MaxSweepBatchSize)
        {
            errors.Add(new(
                "BatchSize",
                $"must be between {PostgresExperienceRecordStore.MinSweepBatchSize} and {PostgresExperienceRecordStore.MaxSweepBatchSize}."));
        }

        ValidateScope(recordScope, "RecordScope", errors);
        return errors;
    }

    /// <summary>
    /// Validates a bounded history read: the scope, the record, the page bound, and the optional keyset
    /// cursor. A negative cursor is rejected rather than treated as "from the beginning", because a
    /// caller that computed one is asking for something it did not mean.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateHistoryQuery(ExperienceRecordHistoryQuery query)
    {
        var errors = new List<StoreValidationError>();

        if (query.ExperienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        ValidateScope(query.Scope, "Scope", errors);

        if (query.Limit is < ExperienceRecordHistoryQuery.MinLimit or > ExperienceRecordHistoryQuery.MaxLimit)
        {
            errors.Add(new(
                "Limit",
                $"must be between {ExperienceRecordHistoryQuery.MinLimit} and {ExperienceRecordHistoryQuery.MaxLimit}."));
        }

        if (query.StartAfterRevision is < 0)
        {
            errors.Add(new("StartAfterRevision", "must be null or a non-negative revision."));
        }

        return errors;
    }

    /// <summary>
    /// Validates a supersession check: the scope both records must lie in, and the two record IDs. The
    /// two being equal is not checked here -- a record replacing itself is a lifecycle rule Core refuses
    /// before any store call, not a malformed request.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateSupersessionCheck(
        Scope scope,
        Guid experienceId,
        Guid replacementExperienceId)
    {
        var errors = new List<StoreValidationError>();

        if (experienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        if (replacementExperienceId == Guid.Empty)
        {
            errors.Add(new("ReplacementExperienceId", "must not be an empty GUID."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>
    /// Validates a lifecycle commit: the event's own fields plus the request scope the record must lie
    /// in. Field paths name the <see cref="LifecycleEvent"/> member, so a caller can map an error back
    /// to what it supplied.
    /// </summary>
    /// <param name="scope">The exact request scope the record must lie in.</param>
    /// <param name="lifecycleEvent">The event to validate.</param>
    /// <param name="authorization">
    /// The host-established context the commit runs under. Only its
    /// <see cref="AuthorizationContext.PrincipalId"/> is checked, and only when the event carries a
    /// confidence payload: that principal becomes the reviewer identity a human submission is counted
    /// under, so a blank one would silently dissolve the human independence rule. Every other commit
    /// records it when there is one and null when there is not.
    /// </param>
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
                "must be non-blank for a confidence update: it is the actor the update is recorded against, and the reviewer a human submission is counted under."));
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
            // OccurredAt is part of the event's stored identity, so an unset value would silently become
            // part of the idempotency key. No upper bound: clock skew makes a future check unsafe.
            errors.Add(new("OccurredAt", "must be set to when the transition occurred."));
        }

        // The database states the same rule as a CHECK, so it holds for a writer that bypasses the
        // store; stating it here too turns it into a typed Invalid with a field path rather than an
        // infrastructure failure. Whether a *particular* replacement is acceptable is Core's decision
        // and is settled before the event reaches this port.
        if (lifecycleEvent.ReplacementExperienceId is { } replacementId)
        {
            // Supersession and a confidence update are different facts about different things, and an
            // event claiming both would make the replacement chain and the evidence trail depend on each
            // other. The database states this as a CHECK too.
            if (lifecycleEvent.Confidence is not null)
            {
                errors.Add(new(
                    "ReplacementExperienceId",
                    "must be null on an event that carries a confidence update; supersession and evidence are separate transitions."));
            }

            if (lifecycleEvent.CurrentStatus != ExperienceStatus.Superseded)
            {
                errors.Add(new(
                    "ReplacementExperienceId",
                    $"must be null unless the event moves the record to {ExperienceStatus.Superseded}."));
            }

            if (replacementId == Guid.Empty)
            {
                errors.Add(new("ReplacementExperienceId", "must not be an empty GUID."));
            }

            if (replacementId == lifecycleEvent.ExperienceRecordId)
            {
                errors.Add(new("ReplacementExperienceId", "must name a record other than the one being superseded."));
            }
        }
        else if (lifecycleEvent.CurrentStatus == ExperienceStatus.Superseded)
        {
            errors.Add(new(
                "ReplacementExperienceId",
                $"is required when the event moves the record to {ExperienceStatus.Superseded}."));
        }

        ValidateConfidenceUpdate(lifecycleEvent.Confidence, errors);

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

    /// <summary>
    /// Validates the optional confidence payload an event may carry. The database states every one of
    /// these rules as a CHECK, so they hold for a writer that bypasses the store; stating them here too
    /// turns a malformed submission into a typed <see cref="ExperienceStoreOutcome.Invalid"/> with a
    /// field path rather than an infrastructure failure.
    /// </summary>
    /// <remarks>
    /// The score is deliberately not re-derived here. Core owns the rule that turns counters into a score
    /// for a <em>write</em>, and a second implementation of it on this path would be a second rule that
    /// could disagree. What is checked is only what the columns can hold: ranges, non-negativity, that a
    /// counter only ever moves up, and that each evidence source carries the identifier its independence
    /// key is made of. (Creation is the exception -- see <c>ValidateCreatedConfidence</c> -- because it is
    /// the one moment the counters and the score arrive independently of each other.)
    /// </remarks>
    private static void ValidateConfidenceUpdate(ConfidenceUpdate? confidence, List<StoreValidationError> errors)
    {
        if (confidence is not { } update)
        {
            return;
        }

        const string Path = "Confidence";

        if (update.EvidenceId == Guid.Empty)
        {
            errors.Add(new($"{Path}.EvidenceId", "must not be an empty GUID."));
        }

        if (update.RunId == Guid.Empty)
        {
            errors.Add(new($"{Path}.RunId", "must name the run the reuse was observed in."));
        }

        RequireDefined(update.Kind, $"{Path}.Kind", errors);
        RequireNotBlank(update.RuleVersion, $"{Path}.RuleVersion", errors);
        RequireUnitInterval(update.PriorReuseConfidence, $"{Path}.PriorReuseConfidence", errors);
        RequireUnitInterval(update.NewReuseConfidence, $"{Path}.NewReuseConfidence", errors);

        foreach (var (count, name) in new[]
        {
            (update.PriorSupportingValidations, nameof(update.PriorSupportingValidations)),
            (update.NewSupportingValidations, nameof(update.NewSupportingValidations)),
            (update.PriorContradictions, nameof(update.PriorContradictions)),
            (update.NewContradictions, nameof(update.NewContradictions)),
        })
        {
            if (count < 0)
            {
                errors.Add(new($"{Path}.{name}", "must not be negative."));
            }
        }

        // A counter that went backwards is not a smaller update, it is a rewrite of history: the event
        // would claim evidence moved a count down, which no evidence can do.
        if (update.NewSupportingValidations < update.PriorSupportingValidations
            || update.NewContradictions < update.PriorContradictions)
        {
            errors.Add(new($"{Path}.NewSupportingValidations", "evidence only ever moves a counter up, never down."));
        }

        if (!Enum.IsDefined(update.Source))
        {
            errors.Add(new($"{Path}.Source", "must be a defined value."));
            return;
        }

        if (update.Source == ConfidenceEvidenceSource.Machine)
        {
            if (update.VerificationRoundId is not { } roundId || roundId == Guid.Empty)
            {
                errors.Add(new(
                    $"{Path}.VerificationRoundId",
                    $"is required for {ConfidenceEvidenceSource.Machine} evidence, which is counted once per run and round."));
            }

            if (update.ReviewerIdentity is not null)
            {
                errors.Add(new($"{Path}.ReviewerIdentity", $"must be null for {ConfidenceEvidenceSource.Machine} evidence."));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(update.ReviewerIdentity))
            {
                errors.Add(new(
                    $"{Path}.ReviewerIdentity",
                    $"is required for {ConfidenceEvidenceSource.Human} evidence, which is counted once per reviewer and run."));
            }

            if (update.VerificationRoundId is not null)
            {
                errors.Add(new($"{Path}.VerificationRoundId", $"must be null for {ConfidenceEvidenceSource.Human} evidence."));
            }
        }
    }

    /// <summary>
    /// Validates a grant request: both scopes, the record it names, its reason, and -- the rule that
    /// makes a grant a grant rather than a scope change -- that the recipient keeps the record's
    /// tenant, application, and project. Each of those three is reported on its own field path, so a
    /// caller learns which boundary it tried to cross without being told anything about the record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>lower</em> bound on expiry is deliberately not checked against the local clock. Whether
    /// a grant is still live is decided by the database's clock in the read predicate, and rejecting
    /// an already-past expiry here would put a second, disagreeing clock in charge of the same
    /// question. The <c>experience_grants_expires_after_issue</c> constraint catches it against the
    /// clock that does decide.
    /// </para>
    /// <para>
    /// The <em>upper</em> bound is checked here, and it has to be: it is a host-configured interval,
    /// and a CHECK constraint cannot express one. A local clock is good enough for it because the
    /// bound is generous and one-sided -- skew of seconds cannot turn a reasonable window into an
    /// unreasonable one -- and because the database keeps its own fixed ceiling underneath
    /// (<c>experience_grants_lifetime_bounded</c>), which no clock of ours is involved in.
    /// </para>
    /// </remarks>
    /// <param name="request">The grant to validate.</param>
    /// <param name="policy">The bounds this adapter administers grants under.</param>
    /// <param name="now">The moment to measure the maximum lifetime from.</param>
    public static IReadOnlyList<StoreValidationError> ValidateGrantRequest(
        ExperienceGrantRequest request,
        PostgresExperienceGrantPolicy policy,
        DateTimeOffset now)
    {
        var errors = new List<StoreValidationError>();

        if (request.GrantId == Guid.Empty)
        {
            errors.Add(new("GrantId", "must not be an empty GUID."));
        }

        if (request.ExperienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        ValidateScope(request.RecordScope, "RecordScope", errors);
        ValidateScope(request.RecipientScope, "RecipientScope", errors);
        RequireNotBlank(request.Reason, "Reason", errors);

        if (request.ExpiresAt == default)
        {
            errors.Add(new("ExpiresAt", "must be set to when the grant stops permitting reads."));
        }
        else if (policy.ExceedsMaxLifetime(request.ExpiresAt, now))
        {
            // The message names the bound, not the offending value: a caller needs to know what it may
            // ask for, and echoing back what it asked for tells it nothing it did not already have.
            errors.Add(new(
                "ExpiresAt",
                $"must not be more than {policy.MaxLifetime} from now, which is the configured maximum grant lifetime."));
        }

        if (request.RecordScope is { } record && request.RecipientScope is { } recipient)
        {
            RequireSameBound(record.TenantId, recipient.TenantId, "RecipientScope.TenantId", errors);
            RequireSameBound(record.ApplicationId, recipient.ApplicationId, "RecipientScope.ApplicationId", errors);
            RequireSameBound(record.ProjectId, recipient.ProjectId, "RecipientScope.ProjectId", errors);

            if (record == recipient)
            {
                // A grant to the scope that already owns the record permits nothing, and storing one
                // would leave an audit row claiming access was given when none was.
                errors.Add(new("RecipientScope", "must differ from the record's own scope, which already permits the read."));
            }
        }

        return errors;
    }

    public static IReadOnlyList<StoreValidationError> ValidateGrantRevocation(ExperienceGrantRevocation revocation)
    {
        var errors = new List<StoreValidationError>();

        if (revocation.GrantId == Guid.Empty)
        {
            errors.Add(new("GrantId", "must not be an empty GUID."));
        }

        ValidateScope(revocation.RecordScope, "RecordScope", errors);
        RequireNotBlank(revocation.Reason, "Reason", errors);

        return errors;
    }

    public static IReadOnlyList<StoreValidationError> ValidateGrantList(Scope recordScope, Guid experienceId, int limit)
    {
        var errors = new List<StoreValidationError>();

        if (experienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        if (limit is < ExperienceGrant.MinListLimit or > ExperienceGrant.MaxListLimit)
        {
            errors.Add(new(
                "Limit",
                $"must be between {ExperienceGrant.MinListLimit} and {ExperienceGrant.MaxListLimit}."));
        }

        ValidateScope(recordScope, "RecordScope", errors);
        return errors;
    }

    /// <summary>
    /// Validates a query over the grant access trail: the owner scope it reads within, the optional
    /// single record it narrows to, and the page bound.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateGrantAccessQuery(ExperienceGrantAccessQuery query)
    {
        var errors = new List<StoreValidationError>();

        if (query.ExperienceId == Guid.Empty)
        {
            // Null narrows to nothing and is the "everything this scope disclosed" question; an empty
            // GUID is a caller that meant to name a record and did not.
            errors.Add(new("ExperienceId", "must not be an empty GUID; use null to read the whole scope."));
        }

        if (query.Limit is < ExperienceGrantAccessQuery.MinLimit or > ExperienceGrantAccessQuery.MaxLimit)
        {
            errors.Add(new(
                "Limit",
                $"must be between {ExperienceGrantAccessQuery.MinLimit} and {ExperienceGrantAccessQuery.MaxLimit}."));
        }

        if (query.StartAfter is { } cursor && cursor.AccessId == Guid.Empty)
        {
            errors.Add(new("StartAfter.AccessId", "must not be an empty GUID."));
        }

        ValidateScope(query.RecordScope, "RecordScope", errors);
        return errors;
    }

    public static IReadOnlyList<StoreValidationError> ValidateGrantHistory(Scope recordScope, Guid grantId)
    {
        var errors = new List<StoreValidationError>();

        if (grantId == Guid.Empty)
        {
            errors.Add(new("GrantId", "must not be an empty GUID."));
        }

        ValidateScope(recordScope, "RecordScope", errors);
        return errors;
    }

    /// <summary>
    /// Validates the explicit administrator authority itself. It is validated, not merely checked for
    /// presence, because its <see cref="GrantAdministration.AuthorizedAt"/> is recorded on the audit
    /// event: an unset value would put "authority established at year zero" into the trail.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateAdministration(GrantAdministration administration)
    {
        var errors = new List<StoreValidationError>();
        RequireNotBlank(administration.AdministratorPrincipalId, "Administration.AdministratorPrincipalId", errors);

        if (administration.AuthorizedAt == default)
        {
            errors.Add(new("Administration.AuthorizedAt", "must be set to when the host established this authority."));
        }

        return errors;
    }

    private static void RequireSameBound(string? recordValue, string? recipientValue, string path, List<StoreValidationError> errors)
    {
        if (!string.Equals(recordValue, recipientValue, StringComparison.Ordinal))
        {
            errors.Add(new(path, "must equal the record's, because a grant may relax only the team, agent, and user fields."));
        }
    }

    /// <summary>
    /// Validates one reuse-feedback submission as a row: the identifiers, the scope, the measure, the
    /// exposures, and the shape each <see cref="ReuseAttributionSource"/> requires.
    /// </summary>
    /// <remarks>
    /// This is structural validation of what will be written, not a second opinion about attribution.
    /// Whether a submission <em>carries</em> attribution is Core's decision and arrives here already
    /// made; what is checked is that the decision is internally consistent -- that an unattributed row
    /// claims no benefit and names no reviewer, round, or evaluator, and that an attributed one carries
    /// exactly the identifiers its derived evidence will be keyed on. The database states the same rules
    /// as CHECKs, so a writer that bypassed this class is refused too.
    /// </remarks>
    public static IReadOnlyList<StoreValidationError> ValidateReuseFeedback(RecordedExperienceReuseFeedback feedback)
    {
        var errors = new List<StoreValidationError>();

        if (feedback.FeedbackId == Guid.Empty)
        {
            errors.Add(new("FeedbackId", "must not be an empty GUID."));
        }

        if (feedback.RunId == Guid.Empty)
        {
            errors.Add(new("RunId", "must not be an empty GUID."));
        }

        ValidateScope(feedback.Scope, "Scope", errors);
        RequireDefined(feedback.RunOutcome, "RunOutcome", errors);
        RequireDefined(feedback.ClaimedBenefit, "ClaimedBenefit", errors);
        RequireDefined(feedback.Benefit, "Benefit", errors);
        RequireDefined(feedback.AttributionSource, "AttributionSource", errors);

        if (feedback.ObservedAt == default)
        {
            errors.Add(new("ObservedAt", "must be set to when the feedback was observed."));
        }

        if (feedback.Measure is null)
        {
            errors.Add(new("Measure", Required));
        }
        else
        {
            RequireNotBlank(feedback.Measure.Kind, "Measure.Kind", errors);

            if (!double.IsFinite(feedback.Measure.Value))
            {
                errors.Add(new("Measure.Value", "must be a finite number."));
            }
        }

        // Through the shared guard, so a NUL -- which PostgreSQL cannot store in text -- is Invalid here
        // rather than an infrastructure failure from the driver.
        RequireNullOrNotBlank(feedback.TrialLabel, "TrialLabel", errors);

        if (feedback.AttributedAt is { } attributedAt && attributedAt == default)
        {
            errors.Add(new("AttributedAt", "must be set when the submission carries an attribution."));
        }

        ValidateReuseAttributionShape(feedback, errors);
        ValidateReuseExposures(feedback, errors);

        return errors;
    }

    private static void ValidateReuseAttributionShape(RecordedExperienceReuseFeedback feedback, List<StoreValidationError> errors)
    {
        // "Benefit is Unknown" and "there was no attribution" are one fact. Two columns that could
        // disagree would let a row claim an improvement nothing attributed.
        if ((feedback.AttributionSource == ReuseAttributionSource.None)
            != (feedback.Benefit == ExperienceReuseBenefit.Unknown))
        {
            errors.Add(new(
                "Benefit",
                "must be Unknown exactly when there is no attribution source, and named otherwise."));
        }

        switch (feedback.AttributionSource)
        {
            case ReuseAttributionSource.HumanAssessment:
                RequireNotBlank(feedback.ReviewerIdentity, "ReviewerIdentity", errors);
                RequireNull(feedback.EvaluatorId, "EvaluatorId", errors);
                RequireNotBlank(feedback.Rationale, "Rationale", errors);
                RequireSet(feedback.AttributedAt, "AttributedAt", errors);
                RequireEmpty(feedback.EvidenceIds, "EvidenceIds", errors);

                // The host-established review this judgement came out of. It is what keeps a human
                // attribution from being a benefit, a list of IDs, and a string -- which is the bare
                // claim this ledger refuses from anyone else.
                if (feedback.AssessmentId is not { } assessment || assessment == Guid.Empty)
                {
                    errors.Add(new("AssessmentId", "must name the host-established review a human assessment came out of."));
                }

                // Optional, and audit only: human evidence is counted once per reviewer and run, so a
                // round the reviewer chose must never reach the independence key.
                if (feedback.VerificationRoundId is { } humanRound && humanRound == Guid.Empty)
                {
                    errors.Add(new("VerificationRoundId", "must name a verification round or be null."));
                }

                break;

            case ReuseAttributionSource.ComparativeEvaluation:
                RequireNotBlank(feedback.EvaluatorId, "EvaluatorId", errors);
                RequireNull(feedback.ReviewerIdentity, "ReviewerIdentity", errors);
                RequireNull(feedback.AssessmentId, "AssessmentId", errors);
                RequireNotBlank(feedback.Rationale, "Rationale", errors);
                RequireSet(feedback.AttributedAt, "AttributedAt", errors);

                if (feedback.VerificationRoundId is not { } round || round == Guid.Empty)
                {
                    errors.Add(new(
                        "VerificationRoundId",
                        "must name the verification round the comparison was made in."));
                }

                // Stored, so an auditor sees what a moved score rested on and not only the evaluator's
                // own summary of it.
                if (feedback.EvidenceIds is not { Count: > 0 })
                {
                    errors.Add(new("EvidenceIds", "must carry the evidence the comparison was reached from."));
                }
                else if (feedback.EvidenceIds.Any(id => id == Guid.Empty))
                {
                    errors.Add(new("EvidenceIds", "must not contain an empty GUID."));
                }
                else if (feedback.EvidenceIds.Distinct().Count() != feedback.EvidenceIds.Count)
                {
                    errors.Add(new("EvidenceIds", "must not name the same piece of evidence twice."));
                }

                break;

            default:
                RequireNull(feedback.ReviewerIdentity, "ReviewerIdentity", errors);
                RequireNull(feedback.EvaluatorId, "EvaluatorId", errors);
                RequireNull(feedback.VerificationRoundId, "VerificationRoundId", errors);
                RequireNull(feedback.AssessmentId, "AssessmentId", errors);
                RequireNull(feedback.Rationale, "Rationale", errors);
                RequireNull(feedback.AttributedAt, "AttributedAt", errors);
                RequireEmpty(feedback.EvidenceIds, "EvidenceIds", errors);
                break;
        }
    }

    private static void ValidateReuseExposures(RecordedExperienceReuseFeedback feedback, List<StoreValidationError> errors)
    {
        const string Path = "Exposures";

        if (feedback.Exposures is null)
        {
            errors.Add(new(Path, Required));
            return;
        }

        if (feedback.Exposures.Count == 0)
        {
            errors.Add(new(Path, "must name at least one exposed record."));
            return;
        }

        // The same bound Core states, mirrored here so the port refuses an oversized fan-out whatever
        // built the submission, and mirrored again by the schema as a bound on an exposure's ordinal.
        if (feedback.Exposures.Count > ExperienceReuseFeedback.MaxExposedRecords)
        {
            errors.Add(new(Path, $"must name at most {ExperienceReuseFeedback.MaxExposedRecords} records."));
        }

        var seen = new HashSet<Guid>();
        var attributedWithoutEvidence = false;
        var unattributedWithEvidence = false;

        foreach (var exposure in feedback.Exposures)
        {
            if (exposure is null)
            {
                errors.Add(new(Path, "must not contain a null exposure."));
                continue;
            }

            if (exposure.ExperienceId == Guid.Empty)
            {
                errors.Add(new($"{Path}.ExperienceId", "must not be an empty GUID."));
            }
            else if (!seen.Add(exposure.ExperienceId))
            {
                errors.Add(new(Path, "must not name the same record twice."));
            }

            if (exposure.Attributed && (exposure.EvidenceId is not { } evidenceId || evidenceId == Guid.Empty))
            {
                attributedWithoutEvidence = true;
            }

            if (!exposure.Attributed && exposure.EvidenceId is not null)
            {
                unattributedWithEvidence = true;
            }

            if (exposure.Attributed && feedback.AttributionSource == ReuseAttributionSource.None)
            {
                errors.Add(new(Path, "must not mark a record attributed when the submission carries no attribution."));
            }
        }

        if (attributedWithoutEvidence)
        {
            errors.Add(new($"{Path}.EvidenceId", "is required for an attributed exposure: it is the confidence submission's idempotency key."));
        }

        if (unattributedWithEvidence)
        {
            errors.Add(new($"{Path}.EvidenceId", "must be null for an exposure nothing attributed, which produced no confidence submission."));
        }
    }

    private static void RequireNull(object? value, string path, List<StoreValidationError> errors)
    {
        if (value is not null)
        {
            errors.Add(new(path, "must be null for this attribution source."));
        }
    }

    private static void RequireEmpty<T>(IReadOnlyList<T>? value, string path, List<StoreValidationError> errors)
    {
        if (value is { Count: > 0 })
        {
            errors.Add(new(path, "must be empty for this attribution source."));
        }
    }

    private static void RequireSet(DateTimeOffset? value, string path, List<StoreValidationError> errors)
    {
        if (value is not { } set || set == default)
        {
            errors.Add(new(path, "must be set for this attribution source."));
        }
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

    /// <summary>
    /// Validates a candidate search: the scope, the task text to match, the caller's eligible status
    /// set, the confidence floor, and the bound on how many candidates may come back. An empty status
    /// set is rejected rather than widened to "every status", so a caller can never accidentally ask
    /// for records it considers ineligible.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateCandidateQuery(ExperienceCandidateQuery query)
    {
        var errors = new List<StoreValidationError>();
        ValidateScope(query.Scope, "Scope", errors);
        RequireNotBlank(query.TaskText, "TaskText", errors);

        if (query.TaskText is { Length: > ExperienceCandidateQuery.MaxTaskTextLength })
        {
            errors.Add(new("TaskText", $"must be at most {ExperienceCandidateQuery.MaxTaskTextLength} characters."));
        }

        if (query.EligibleStatuses is null)
        {
            errors.Add(new("EligibleStatuses", Required));
        }
        else if (query.EligibleStatuses.Count == 0)
        {
            errors.Add(new("EligibleStatuses", "must contain at least one status."));
        }
        else
        {
            for (var i = 0; i < query.EligibleStatuses.Count; i++)
            {
                RequireDefined(query.EligibleStatuses[i], $"EligibleStatuses[{i}]", errors);
            }
        }

        RequireUnitInterval(query.MinimumConfidence, "MinimumConfidence", errors);

        if (query.Limit is < ExperienceCandidateQuery.MinLimit or > ExperienceCandidateQuery.MaxLimit)
        {
            errors.Add(new("Limit", $"must be between {ExperienceCandidateQuery.MinLimit} and {ExperienceCandidateQuery.MaxLimit}."));
        }

        return errors;
    }

    /// <summary>
    /// Validates a conditional index write: the scope the record must lie in, the record ID, the
    /// descriptor that makes the write conditional, and the vector itself. The vector's length is
    /// checked against the descriptor's dimension here, so the two can never be stored disagreeing.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateIndexWrite(ExperienceIndexWrite write)
    {
        var errors = new List<StoreValidationError>();
        ValidateScope(write.Scope, "Scope", errors);

        if (write.ExperienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        if (write.Descriptor is null)
        {
            errors.Add(new("Descriptor", Required));
            return errors;
        }

        ValidateDescriptor(write.Descriptor, "Descriptor", errors);
        ValidateVector(write.Vector, "Vector", errors);

        if (write.Descriptor.Dimension > 0 && write.Vector.Length != write.Descriptor.Dimension)
        {
            errors.Add(new("Vector", "must hold exactly Descriptor.Dimension components."));
        }

        return errors;
    }

    /// <summary>Validates a vector removal: the scope the embedding must lie in, and the record it belongs to.</summary>
    public static IReadOnlyList<StoreValidationError> ValidateIndexRemove(Scope scope, Guid experienceId)
    {
        var errors = new List<StoreValidationError>();

        if (experienceId == Guid.Empty)
        {
            errors.Add(new("ExperienceId", "must not be an empty GUID."));
        }

        ValidateScope(scope, "Scope", errors);
        return errors;
    }

    /// <summary>Validates a scoped index scan: the scope, the model whose descriptors to report, the optional ID filter, and the bound.</summary>
    public static IReadOnlyList<StoreValidationError> ValidateIndexScan(ExperienceIndexScan scan)
    {
        var errors = new List<StoreValidationError>();
        ValidateScope(scan.Scope, "Scope", errors);
        RequireNotBlank(scan.ModelId, "ModelId", errors);

        if (scan.ModelId is { Length: > ExperienceEmbeddingDescriptor.MaxModelIdLength })
        {
            errors.Add(new("ModelId", $"must be at most {ExperienceEmbeddingDescriptor.MaxModelIdLength} characters."));
        }

        if (scan.EligibleStatuses is null)
        {
            errors.Add(new("EligibleStatuses", Required));
        }
        else if (scan.EligibleStatuses.Count == 0)
        {
            errors.Add(new("EligibleStatuses", "must contain at least one status."));
        }
        else
        {
            for (var i = 0; i < scan.EligibleStatuses.Count; i++)
            {
                RequireDefined(scan.EligibleStatuses[i], $"EligibleStatuses[{i}]", errors);
            }
        }

        RequireUnitInterval(scan.MinimumConfidence, "MinimumConfidence", errors);

        if (scan.StartAfterId == Guid.Empty)
        {
            errors.Add(new("StartAfterId", "must be null or a non-empty GUID."));
        }

        if (scan.ExperienceIds is not null)
        {
            if (scan.ExperienceIds.Count == 0)
            {
                errors.Add(new("ExperienceIds", "must contain at least one identifier when supplied."));
            }
            else if (scan.ExperienceIds.Count > ExperienceIndexScan.MaxLimit)
            {
                errors.Add(new("ExperienceIds", $"must hold at most {ExperienceIndexScan.MaxLimit} identifiers."));
            }
            else
            {
                for (var i = 0; i < scan.ExperienceIds.Count; i++)
                {
                    if (scan.ExperienceIds[i] == Guid.Empty)
                    {
                        errors.Add(new($"ExperienceIds[{i}]", "must not be an empty GUID."));
                    }
                }
            }
        }

        if (scan.Limit is < ExperienceIndexScan.MinLimit or > ExperienceIndexScan.MaxLimit)
        {
            errors.Add(new("Limit", $"must be between {ExperienceIndexScan.MinLimit} and {ExperienceIndexScan.MaxLimit}."));
        }

        return errors;
    }

    /// <summary>
    /// Validates a scoped vector search. The field paths and the bounds deliberately mirror
    /// <see cref="ValidateCandidateQuery"/>, so both retrieval channels reject the same requests for
    /// the same reasons.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateVectorQuery(ExperienceVectorQuery query)
    {
        var errors = new List<StoreValidationError>();
        ValidateScope(query.Scope, "Scope", errors);
        RequireNotBlank(query.ModelId, "ModelId", errors);

        if (query.ModelId is { Length: > ExperienceEmbeddingDescriptor.MaxModelIdLength })
        {
            errors.Add(new("ModelId", $"must be at most {ExperienceEmbeddingDescriptor.MaxModelIdLength} characters."));
        }

        ValidateVector(query.Vector, "Vector", errors);

        if (query.EligibleStatuses is null)
        {
            errors.Add(new("EligibleStatuses", Required));
        }
        else if (query.EligibleStatuses.Count == 0)
        {
            errors.Add(new("EligibleStatuses", "must contain at least one status."));
        }
        else
        {
            for (var i = 0; i < query.EligibleStatuses.Count; i++)
            {
                RequireDefined(query.EligibleStatuses[i], $"EligibleStatuses[{i}]", errors);
            }
        }

        RequireUnitInterval(query.MinimumConfidence, "MinimumConfidence", errors);

        if (query.Limit is < ExperienceCandidateQuery.MinLimit or > ExperienceCandidateQuery.MaxLimit)
        {
            errors.Add(new("Limit", $"must be between {ExperienceCandidateQuery.MinLimit} and {ExperienceCandidateQuery.MaxLimit}."));
        }

        return errors;
    }

    private static void ValidateDescriptor(ExperienceEmbeddingDescriptor descriptor, string path, List<StoreValidationError> errors)
    {
        RequireNotBlank(descriptor.ModelId, $"{path}.ModelId", errors);
        if (descriptor.ModelId is { Length: > ExperienceEmbeddingDescriptor.MaxModelIdLength })
        {
            errors.Add(new($"{path}.ModelId", $"must be at most {ExperienceEmbeddingDescriptor.MaxModelIdLength} characters."));
        }

        if (descriptor.Dimension is < 1 or > ExperienceEmbeddingDescriptor.MaxDimension)
        {
            errors.Add(new($"{path}.Dimension", $"must be between 1 and {ExperienceEmbeddingDescriptor.MaxDimension}."));
        }

        RequireNotBlank(descriptor.ContentHash, $"{path}.ContentHash", errors);

        if (descriptor.SourceRevision < 0)
        {
            errors.Add(new($"{path}.SourceRevision", "must not be negative."));
        }
    }

    /// <summary>
    /// A vector must be non-empty, within the dimension ceiling, and entirely finite. A NaN or an
    /// infinity would be accepted by pgvector's input parser in some forms and then poison every
    /// distance computed against it, so it is rejected before any database access.
    /// </summary>
    private static void ValidateVector(ReadOnlyMemory<float> vector, string path, List<StoreValidationError> errors)
    {
        if (vector.Length == 0)
        {
            errors.Add(new(path, "must hold at least one component."));
            return;
        }

        if (vector.Length > ExperienceEmbeddingDescriptor.MaxDimension)
        {
            errors.Add(new(path, $"must hold at most {ExperienceEmbeddingDescriptor.MaxDimension} components."));
            return;
        }

        foreach (var component in vector.Span)
        {
            if (!float.IsFinite(component))
            {
                errors.Add(new(path, "must hold only finite components."));
                return;
            }
        }
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

    /// <summary>
    /// Checks that a record arrives with a reuse confidence its own counters explain. Creation is the one
    /// moment the two can be set independently -- after it, every change goes through a revision-guarded
    /// update the database ties to the lifecycle event that recorded the evidence -- so without this a
    /// host could create a record at 0.99 with a single supporting validation and every guard this library
    /// adds afterwards would be satisfied forever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a consistency check, not the arithmetic: nothing here decides what a score should be for a
    /// <em>write</em>, which stays Core's alone. It applies only to a record that <em>claims</em>
    /// evidence -- one with a non-zero counter -- and requires its confidence to be the
    /// <c>(1 + S) / (2 + S + F)</c> those counters explain. The comparison allows a relative slack of
    /// 1e-12, so a caller that computed the same value through a different association of the same
    /// operations is not rejected over the last bit.
    /// </para>
    /// <para>
    /// A record created with no counters at all is deliberately left alone, whatever confidence it
    /// carries. That is the quarantined shape (no lesson, no evidence, no confidence), and it is also a
    /// host seeding a record it has its own reasons to trust -- which is its prerogative, since it
    /// chooses the status too. The seeded number cannot outlive contact with evidence: the first accepted
    /// submission recomputes from the counters, which are still zero, so it lands wherever the rule says
    /// and not wherever the record was seeded.
    /// </para>
    /// </remarks>
    private static void ValidateCreatedConfidence(ExperienceRecord record, List<StoreValidationError> errors)
    {
        if (record.SupportingValidations < 0 || record.Contradictions < 0
            || !(record.ReuseConfidence >= 0d && record.ReuseConfidence <= 1d))
        {
            // Already reported above; a second message about the same values would only be noise.
            return;
        }

        if (record.SupportingValidations == 0 && record.Contradictions == 0)
        {
            return;
        }

        var expected = (1d + record.SupportingValidations)
            / (2d + record.SupportingValidations + record.Contradictions);

        if (Math.Abs(record.ReuseConfidence - expected) > 1e-12 * expected)
        {
            errors.Add(new(
                "ReuseConfidence",
                "must be the confidence the record's own evidence counters explain."));
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
