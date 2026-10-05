using AgentExperience.Abstractions;

// Shared source (story 11.2): the validation rules for the core storage ports, which depend on nothing but
// AgentExperience.Abstractions. Both AgentExperience.Storage.Postgres and AgentExperience.Storage.InMemory link this
// file, so the two stores refuse exactly the same requests. Each project defines exactly one of the constants below,
// which puts the validator in that project's own namespace. Keep this file free of every type only one store has:
// the PostgreSQL-only rules (erasure, retention, sealing, grants, the vector index) are in that project's
// ExperienceRecordValidator.cs. Where a comment below says "the database", it is the PostgreSQL store's schema,
// which states the same rule again as a CHECK.
#if AGENTEXPERIENCE_POSTGRES && AGENTEXPERIENCE_INMEMORY
#error Define exactly one of AGENTEXPERIENCE_POSTGRES and AGENTEXPERIENCE_INMEMORY.
#elif AGENTEXPERIENCE_POSTGRES
namespace AgentExperience.Storage.Postgres;
#elif AGENTEXPERIENCE_INMEMORY
namespace AgentExperience.Storage.InMemory;
#else
#error Define AGENTEXPERIENCE_POSTGRES or AGENTEXPERIENCE_INMEMORY to select the namespace of the shared validator.
#endif

/// <summary>
/// Structural validation of the core storage ports' requests, run before authorization and before any storage
/// access. Collects every error (never stops at the first) with a field path and a content-free message. Rejects
/// nulls in non-nullable members so a stored payload is always readable back.
/// </summary>
internal static partial class ExperienceRecordValidator
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

        if (record.ClosedRoundId == Guid.Empty)
        {
            // An empty round names no round; a record that closed none says so with null.
            errors.Add(new("ClosedRoundId", "must be null or name a verification round, never an empty GUID."));
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
        RequireDefined(record.Origin, "Origin", errors);

        if (record.ProvenanceSignature is { } signature)
        {
            ValidateProvenanceSignature(signature, errors);
        }
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
    /// Validates what is request-wide about a batched read: the scope and the number of IDs. An empty
    /// GUID in the list is deliberately <em>not</em> checked here -- it is answered per position, exactly
    /// as <see cref="ValidateGet"/> answers a single read of it.
    /// </summary>
    public static IReadOnlyList<StoreValidationError> ValidateGetMany(Scope scope, int count)
    {
        var errors = new List<StoreValidationError>();
        if (count > ExperienceRecordGetManyResult.MaxCount)
        {
            errors.Add(new("ExperienceIds", $"must name at most {ExperienceRecordGetManyResult.MaxCount} records."));
        }

        ValidateScope(scope, "Scope", errors);
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
        RefuseReservedText(lifecycleEvent.Reason, "Reason", errors);
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
        ValidateRecordedOnlyEvidence(lifecycleEvent, errors);

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
    /// Whether two confidence payloads are the same piece of evidence: the same evidence ID.
    /// </summary>
    public static bool IsSameEvidence(ConfidenceUpdate? submitted, ConfidenceUpdate? stored) =>
        submitted is not null && stored is not null && submitted.EvidenceId == stored.EvidenceId;

    /// <summary>
    /// <paramref name="submitted"/> as an event-ID replay compares it with <paramref name="stored"/>: the admission is
    /// the stored one, and for the same evidence so are the counters and the score, which follow from whether it was
    /// counted -- the host's <c>HostTrustedEvidenceEffect</c> -- rather than from what was observed.
    /// </summary>
    public static ConfidenceUpdate AsReplayOf(ConfidenceUpdate submitted, ConfidenceUpdate? stored)
    {
        if (stored is null || submitted.EvidenceId != stored.EvidenceId)
        {
            return submitted with { Admission = stored?.Admission };
        }

        return submitted with
        {
            Admission = stored.Admission,
            PriorReuseConfidence = stored.PriorReuseConfidence,
            NewReuseConfidence = stored.NewReuseConfidence,
            PriorSupportingValidations = stored.PriorSupportingValidations,
            NewSupportingValidations = stored.NewSupportingValidations,
            PriorContradictions = stored.PriorContradictions,
            NewContradictions = stored.NewContradictions,
        };
    }

    /// <summary>
    /// An event may carry evidence that moves no counter only when it is host-trusted evidence recorded without
    /// counting (<c>HostTrustedEvidenceEffect.RecordedOnly</c>), and then it must move nothing else either:
    /// not the score, and not the status. Any other evidence event that moves no counter is refused, as it always was
    /// (the PostgreSQL ledger states the same rule as a CHECK, from <c>0023</c>).
    /// </summary>
    private static void ValidateRecordedOnlyEvidence(LifecycleEvent lifecycleEvent, List<StoreValidationError> errors)
    {
        if (lifecycleEvent.Confidence is not { Counted: false } update)
        {
            return;
        }

        if (update.Admission != ConfidenceEvidenceAdmission.HostTrusted)
        {
            errors.Add(new(
                "Confidence.Admission",
                $"an event whose evidence moves no counter is accepted only for {ConfidenceEvidenceAdmission.HostTrusted} evidence recorded without being counted."));
        }

        if (!update.NewReuseConfidence.Equals(update.PriorReuseConfidence))
        {
            errors.Add(new("Confidence.NewReuseConfidence", "must equal the prior score when the evidence moves no counter."));
        }

        if (lifecycleEvent.PriorStatus is { } prior && prior != lifecycleEvent.CurrentStatus)
        {
            errors.Add(new("CurrentStatus", "must equal the prior status when the evidence moves no counter."));
        }
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

        RefuseReservedText(update.Detail, $"{Path}.Detail", errors);

        if (update.EvidenceId == Guid.Empty)
        {
            errors.Add(new($"{Path}.EvidenceId", "must not be an empty GUID."));
        }

        if (update.RunId == Guid.Empty)
        {
            errors.Add(new($"{Path}.RunId", "must name the run the reuse was observed in."));
        }

        RequireDefined(update.Kind, $"{Path}.Kind", errors);
        if (update.Admission is { } admission)
        {
            RequireDefined(admission, $"{Path}.Admission", errors);
        }

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

            if (update.AssessmentId is not null)
            {
                errors.Add(new(ConfidenceUpdate.AssessmentIdPath, $"must be null for {ConfidenceEvidenceSource.Machine} evidence."));
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

            if (update.AssessmentId == Guid.Empty)
            {
                errors.Add(new(ConfidenceUpdate.AssessmentIdPath, "must be null or name an assessment, never an empty GUID."));
            }
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

    /// <summary>
    /// The submission with its exposures in <see cref="ExperienceReuseExposure.ExperienceId"/> order, the one order
    /// a store keeps them in, so the same exposures listed differently are stored, compared and handed back as one
    /// submission. Only for a submission <see cref="ValidateReuseFeedback"/> accepted: it names no record twice and
    /// holds no null, so the order is total. The sort is <see cref="Guid"/>'s own comparison, the one Core orders by.
    /// </summary>
    public static RecordedExperienceReuseFeedback NormalizeReuseFeedback(RecordedExperienceReuseFeedback feedback)
    {
        for (var i = 1; i < feedback.Exposures.Count; i++)
        {
            if (feedback.Exposures[i - 1].ExperienceId.CompareTo(feedback.Exposures[i].ExperienceId) > 0)
            {
                return feedback with { Exposures = [.. feedback.Exposures.OrderBy(exposure => exposure.ExperienceId)] };
            }
        }

        return feedback;
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

        RefuseReservedText(feedback.Rationale, "Rationale", errors);
        if (string.Equals(feedback.Rationale, ReservedStoredText.SealedPlaceholder, StringComparison.Ordinal))
        {
            errors.Add(new("Rationale", $"must not be exactly '{ReservedStoredText.SealedPlaceholder}', which is reserved for a sealed rationale."));
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

        // A scope field is a signed provenance claim, encoded as UTF-8: a lone surrogate has no UTF-8 form, so it
        // is refused here rather than signed or stored as something else.
        RequireWellFormedUtf16(scope.TenantId, $"{path}.TenantId", errors);
        RequireWellFormedUtf16(scope.ApplicationId, $"{path}.ApplicationId", errors);
        RequireWellFormedUtf16(scope.ProjectId, $"{path}.ProjectId", errors);
        RequireWellFormedUtf16(scope.TeamId, $"{path}.TeamId", errors);
        RequireWellFormedUtf16(scope.AgentId, $"{path}.AgentId", errors);
        RequireWellFormedUtf16(scope.UserId, $"{path}.UserId", errors);
    }

    private static void RequireWellFormedUtf16(string? value, string path, List<StoreValidationError> errors)
    {
        if (value is null)
        {
            return;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                errors.Add(new(path, "must be well-formed UTF-16: a lone surrogate has no UTF-8 encoding."));
                return;
            }
        }
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
        RequireDefined(reflection.Authorship, "Reflection.Authorship", errors);
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

    /// <summary>The longest provenance signature a store accepts: far above HMAC-SHA256's 32 bytes, so it only bounds the row.</summary>
    private const int MaxProvenanceSignatureBytes = 512;

    /// <summary>
    /// A provenance signature's shape only. A store holds no key, so whether it verifies is never decided here;
    /// what is refused is what could not be a signature at all.
    /// </summary>
    private static void ValidateProvenanceSignature(ExperienceProvenanceSignature signature, List<StoreValidationError> errors)
    {
        var keyId = signature.KeyId;
        if (string.IsNullOrEmpty(keyId)
            || keyId.Length > 64
            || !keyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            errors.Add(new("ProvenanceSignature.KeyId", "must be 1 to 64 characters from [A-Za-z0-9._-]."));
        }

        if (string.IsNullOrEmpty(signature.Algorithm)
            || signature.Algorithm.Length > 64
            || !signature.Algorithm.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            errors.Add(new("ProvenanceSignature.Algorithm", "must be 1 to 64 characters from [A-Za-z0-9._-]."));
        }

        if (signature.Value.Length is 0 or > MaxProvenanceSignatureBytes)
        {
            errors.Add(new("ProvenanceSignature.Value", $"must be 1 to {MaxProvenanceSignatureBytes} bytes."));
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

        if (provenance.ExposedTo is not { } exposedTo)
        {
            errors.Add(new("Provenance.ExposedTo", Required));
            return;
        }

        if (exposedTo.Count > RunExposure.MaxPerRun)
        {
            errors.Add(new("Provenance.ExposedTo", $"must name at most {RunExposure.MaxPerRun} records."));
            return;
        }

        var seen = new HashSet<Guid>();
        for (var i = 0; i < exposedTo.Count; i++)
        {
            if (exposedTo[i] is not { } exposure)
            {
                errors.Add(new($"Provenance.ExposedTo[{i}]", Required));
            }
            else if (exposure.ExperienceId == Guid.Empty)
            {
                errors.Add(new($"Provenance.ExposedTo[{i}].ExperienceId", "must not be an empty GUID."));
            }
            else if (exposure.Revision < 0)
            {
                errors.Add(new($"Provenance.ExposedTo[{i}].Revision", "must not be negative."));
            }
            else if (!seen.Add(exposure.ExperienceId))
            {
                errors.Add(new($"Provenance.ExposedTo[{i}].ExperienceId", "must not name the same record twice; a run keeps one exposure per record."));
            }
        }
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

    /// <summary>
    /// Refuses free text that begins with the sealed-value format, in both modes: a stored value with that prefix
    /// is opened as ciphertext, so a caller's plaintext with it would make every later read of the row throw.
    /// </summary>
    private static void RefuseReservedText(string? value, string path, List<StoreValidationError> errors)
    {
        if (value is not null && value.StartsWith(ReservedStoredText.SealedPrefix, StringComparison.Ordinal))
        {
            errors.Add(new(path, $"must not begin with '{ReservedStoredText.SealedPrefix}', which is reserved for sealed values."));
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

/// <summary>
/// Free text reserved for the crypto-shredding format of sealed stored values. Every store refuses it, whether or not
/// it seals anything, so a request is valid or invalid the same way whichever store receives it.
/// </summary>
internal static class ReservedStoredText
{
    /// <summary>The prefix of a sealed stored value. Free text starting with it is refused.</summary>
    internal const string SealedPrefix = "aexp-sealed:v1:";

    /// <summary>What the reuse-feedback ledger stores in place of a sealed rationale. A rationale equal to it is refused.</summary>
    internal const string SealedPlaceholder = "(sealed)";
}
