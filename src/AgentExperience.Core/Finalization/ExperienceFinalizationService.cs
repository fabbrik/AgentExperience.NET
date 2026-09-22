using System.Globalization;
using System.Security.Cryptography;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Verification;

namespace AgentExperience.Core.Finalization;

/// <summary>
/// The single Core call that turns a captured, completed run into a durable Experience Record. It
/// runs the stages of <see cref="FinalizationStage"/> in order and stops at the first one that ends
/// the call, returning a structured <see cref="FinalizeExperienceResult"/> naming that stage --
/// never an exception for an expected condition, and never a durable success for a database failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stages.</b> Load the captured snapshot, evaluate it against its own host-closed round, check the
/// host authorization and the host's storage decision, reflect on it, create the record, then commit
/// the record's initial lifecycle event. The two gates deliberately precede reflection:
/// <see cref="IExperienceReflector"/> is the documented seam for a model-backed reflector, so a run
/// the host is about to refuse is never handed to it.
/// </para>
/// <para>
/// <b>Validated vs quarantined.</b> A verified evaluation plus a successful reflection plus a
/// permitting storage decision produces a <see cref="ExperienceStatus.Validated"/> record with reuse
/// confidence <c>2/3</c>, one supporting validation and no contradictions. A permitted record whose
/// verification is not <see cref="TaskVerificationStatus.Verified"/>, or whose reflection threw, is
/// <see cref="ExperienceStatus.Quarantined"/> instead, carrying no reflection at all and safe failure
/// metadata on the result. Reflection is not even attempted for an unverified run, so an unreflected
/// lesson can never reach a quarantined record. Confidence is never computed from evidence counts
/// (that is a later story), and risk is never decided on the host's behalf.
/// </para>
/// <para>
/// <b>Candidate first.</b> The record is <em>created</em> as <see cref="ExperienceStatus.Candidate"/>,
/// and its initial lifecycle event performs the real transition to
/// <see cref="ExperienceStatus.Validated"/> or <see cref="ExperienceStatus.Quarantined"/> through
/// Core's transition table (both are allowed moves, and the store's prior-status guard applies). So a
/// commit that never lands leaves a <see cref="ExperienceStatus.Candidate"/> -- never injectable --
/// rather than a reusable record with no lifecycle history. The record's reuse-confidence inputs are
/// stamped at create time because a lifecycle commit updates only status, revision, and the updated
/// timestamp: the store persists Core's decision and derives no score of its own (2.4). The returned
/// record mirrors the projection the commit applied.
/// </para>
/// <para>
/// <b>Replay.</b> The record ID, the reflection ID, and the initial event ID are all derived from the
/// run ID, and the record's <see cref="ExperienceRecord.CreatedAt"/> is the event's
/// <see cref="LifecycleEvent.OccurredAt"/>, so finalizing the same run twice cannot create a second
/// record or a second initial confirmation. A second call finds the stored record and reports the
/// first call's outcome. When an earlier call created the record but its initial commit did not land
/// (the record is still at revision 0), a retry finishes that commit rather than starting over.
/// </para>
/// <para>
/// <b>Sanitization.</b> Finalization never sanitizes: capture already rejected anything unsafe before
/// storing an attempt, so the run's attempts are copied onto the record unchanged.
/// </para>
/// <para>
/// <b>Failures.</b> Every stage failure comes back as a structured
/// <see cref="FinalizationOutcome.Failed"/> result naming the stage, including any exception a port
/// throws; the captured snapshot is never evicted, so the host can retry. The one exception is
/// cancellation: an <see cref="OperationCanceledException"/> from <em>any</em> stage -- the caller's
/// token or a port cancelling for its own reasons -- always propagates, so a cancelled call never
/// silently becomes a quarantined record.
/// </para>
/// </remarks>
public sealed class ExperienceFinalizationService
{
    /// <summary>The <see cref="LifecycleEvent.Producer"/> every initial event this service commits carries.</summary>
    public const string ProducerIdentity = "AgentExperience.ExperienceFinalizationService/1.0.0";

    /// <summary>
    /// The reuse confidence a freshly validated record starts at: two thirds. It is an initial,
    /// evidence-gated value, not a score computed from evidence counts.
    /// </summary>
    public const double InitialValidatedReuseConfidence = 2d / 3d;

    /// <summary>The status every Experience Record is created in, before its initial lifecycle event moves it.</summary>
    public const ExperienceStatus CreatedStatus = ExperienceStatus.Candidate;

    /// <summary>
    /// Fixed namespace for the derived identifiers below. Changing it would re-issue every record ID,
    /// so it is a constant of this library, never configurable.
    /// </summary>
    private static readonly Guid DerivationNamespace = new("0b6a8a3f-1c2d-4f5e-9a70-3d1c9f2b8e41");

    private const byte ExperienceIdTag = 1;
    private const byte InitialEventIdTag = 2;
    private const byte ReflectionIdTag = 3;

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly IExperienceCaptureService _captureService;
    private readonly IExperienceReflector _reflector;
    private readonly IExperienceRecordStore _store;
    private readonly ExperienceLifecycleService _lifecycleService;

    /// <summary>Creates a finalization service over the capture snapshot, the reflector, the record store, and Core's lifecycle owner.</summary>
    /// <param name="captureService">Where the completed run's sanitized snapshot is read from.</param>
    /// <param name="reflector">Turns the evaluated run into an auditable reflection.</param>
    /// <param name="store">The durable Experience Record store.</param>
    /// <param name="lifecycleService">Core's lifecycle owner, which stamps and commits the initial event.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ExperienceFinalizationService(
        IExperienceCaptureService captureService,
        IExperienceReflector reflector,
        IExperienceRecordStore store,
        ExperienceLifecycleService lifecycleService)
    {
        ArgumentNullException.ThrowIfNull(captureService);
        ArgumentNullException.ThrowIfNull(reflector);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lifecycleService);

        _captureService = captureService;
        _reflector = reflector;
        _store = store;
        _lifecycleService = lifecycleService;
    }

    /// <summary>The <see cref="ExperienceRecord.ExperienceId"/> finalizing <paramref name="runId"/> issues, derived from the run so a retry re-derives the same ID.</summary>
    /// <param name="runId">The captured run.</param>
    public static Guid ExperienceIdFor(Guid runId) => Derive(runId, ExperienceIdTag);

    /// <summary>The <see cref="LifecycleEvent.EventId"/> of the record's initial event, derived from the run so a retry cannot commit a second initial confirmation.</summary>
    /// <param name="runId">The captured run.</param>
    public static Guid InitialEventIdFor(Guid runId) => Derive(runId, InitialEventIdTag);

    /// <summary>The <see cref="Reflection.ReflectionId"/> finalizing <paramref name="runId"/> asks the reflector to stamp, derived from the run so a retry reflects under the same identity.</summary>
    /// <param name="runId">The captured run.</param>
    public static Guid ReflectionIdFor(Guid runId) => Derive(runId, ReflectionIdTag);

    /// <summary>
    /// Finalizes one captured run, running every stage in order and stopping at the first one that
    /// ends the call.
    /// </summary>
    /// <param name="request">The run to finalize, its verification inputs, the host authorization, and the host's storage decision.</param>
    /// <param name="cancellationToken">Cancels the operation. Cancellation is not an expected condition and propagates.</param>
    /// <returns>A structured result naming the stage finalization ended at.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/>, or its <see cref="FinalizeExperienceRequest.Authorization"/>, <see cref="FinalizeExperienceRequest.RequiredChecks"/>, <see cref="FinalizeExperienceRequest.Evidence"/>, or <see cref="FinalizeExperienceRequest.StorageDecision"/>, is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="FinalizeExperienceRequest.RunId"/> is <see cref="Guid.Empty"/>, <see cref="FinalizeExperienceRequest.CurrentArtifactRevision"/> is blank, or <see cref="FinalizeExperienceRequest.FinalizedAt"/> is unset.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, or a port cancelled.</exception>
    public async Task<FinalizeExperienceResult> FinalizeAsync(
        FinalizeExperienceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization, $"{nameof(request)}.{nameof(request.Authorization)}");
        ArgumentNullException.ThrowIfNull(request.RequiredChecks, $"{nameof(request)}.{nameof(request.RequiredChecks)}");
        ArgumentNullException.ThrowIfNull(request.Evidence, $"{nameof(request)}.{nameof(request.Evidence)}");
        ArgumentNullException.ThrowIfNull(request.StorageDecision, $"{nameof(request)}.{nameof(request.StorageDecision)}");
        if (request.RunId == Guid.Empty)
        {
            throw new ArgumentException("RunId must not be an empty GUID.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CurrentArtifactRevision))
        {
            throw new ArgumentException("CurrentArtifactRevision must not be null, empty, or whitespace.", nameof(request));
        }

        if (request.FinalizedAt == default)
        {
            // An unset timestamp would be stamped onto the record and its initial event, and the store
            // rejects an unset OccurredAt as Invalid -- so the record would be created and then every
            // commit, including every retry, would be refused forever.
            throw new ArgumentException("FinalizedAt must be set to when the host decided to finalize this run.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Stage 1 -- Load. An unknown or unfinished run is an expected condition, not an exception.
        ExperienceRun? run;
        try
        {
            _ = _captureService.TryGetRun(request.RunId, out run);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PortFailed(FinalizationStage.Load, ex, evaluation: null, record: null, reflection: null);
        }

        if (run is null)
        {
            return Ended(
                FinalizationOutcome.RunNotFound,
                FinalizationStage.Load,
                "No captured run exists for the requested run ID.");
        }

        if (run.ExecutionStatus is null)
        {
            return Ended(
                FinalizationOutcome.RunNotFinished,
                FinalizationStage.Load,
                "The captured run has no execution status, so it has not finished and cannot be finalized.");
        }

        // The store keeps whole microseconds, so the record's CreatedAt -- which is also the initial
        // event's OccurredAt -- is truncated here rather than by the database. That keeps a replay's
        // re-derived event byte-for-byte identical to the stored one.
        var finalizedAt = TruncateToMicroseconds(request.FinalizedAt);

        // Stage 2 -- Evaluate, against this run's own closed round and evidence. No caller-supplied
        // evaluation is accepted, so an evaluation from another run cannot be substituted.
        VerificationResult evaluation;
        try
        {
            evaluation = VerificationAggregator.Aggregate(
                request.Evidence,
                request.RequiredChecks,
                request.ClosedRound,
                request.CurrentArtifactRevision,
                finalizedAt,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ended(
                FinalizationOutcome.Failed,
                FinalizationStage.Evaluate,
                "The run could not be evaluated.",
                new FinalizationFailure(
                    FinalizationStage.Evaluate,
                    $"Aggregating verification threw {ex.GetType().FullName}; the verification inputs are malformed.",
                    NoErrors,
                    ex),
                evaluation: null);
        }

        // Stage 3 -- Authorize, then read the host's storage decision. Both are decided before any
        // store call *and before the reflector is called*, so a refused run is never handed to the
        // model-backed reflection seam and nothing at all is written.
        if (!request.Authorization.Permits(run.Scope))
        {
            return Ended(
                FinalizationOutcome.NotAuthorized,
                FinalizationStage.Authorize,
                "The captured run's scope lies outside the host-established authorization; nothing was stored and the run was not reflected on.",
                evaluation: evaluation);
        }

        if (!request.StorageDecision.Permitted)
        {
            return Ended(
                FinalizationOutcome.StorageDenied,
                FinalizationStage.Authorize,
                request.StorageDecision.Reason ?? "The host's storage decision did not permit persisting this run; nothing was stored and the run was not reflected on.",
                evaluation: evaluation);
        }

        // Stage 4 -- Reflect, but only on a verified run: a quarantined record must never carry an
        // unreflected lesson, so an unverified run is not reflected on at all.
        Reflection? reflection = null;
        FinalizationFailure? failure = null;

        if (evaluation.Outcome.Status == TaskVerificationStatus.Verified)
        {
            try
            {
                reflection = await _reflector
                    .ReflectAsync(
                        new ReflectionRequest(run, evaluation, ReflectionIdFor(run.RunId), finalizedAt),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (reflection is null)
                {
                    failure = new FinalizationFailure(
                        FinalizationStage.Reflect,
                        "The reflector returned no reflection; the record is quarantined without an eligible lesson.",
                        NoErrors,
                        Exception: null);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is never quietly turned into a quarantine, whoever cancelled and why.
                throw;
            }
            catch (Exception ex)
            {
                // A reflector failure does not end finalization: the run is still worth keeping, just
                // not as a validated lesson. The exception is caught, never rethrown.
                reflection = null;
                failure = new FinalizationFailure(
                    FinalizationStage.Reflect,
                    $"The reflector threw {ex.GetType().FullName}; the record is quarantined without an eligible lesson.",
                    NoErrors,
                    ex);
            }
        }
        else
        {
            failure = new FinalizationFailure(
                FinalizationStage.Evaluate,
                $"Verification resolved to {evaluation.Outcome.Status} rather than {TaskVerificationStatus.Verified}; the record is quarantined without an eligible lesson.",
                NoErrors,
                Exception: null);
        }

        // Stage 5 -- Create the record, as a Candidate. Attempts are copied unchanged: capture already
        // rejected anything unsafe, and finalization never sanitizes.
        var record = new ExperienceRecord(
            ExperienceId: ExperienceIdFor(run.RunId),
            SourceRunId: run.RunId,
            Scope: run.Scope,
            TaskId: run.TaskId,
            TaskSummary: run.TaskDescription,
            Attempts: run.Attempts,
            Outcome: evaluation.Outcome,
            CompletionScore: evaluation.CompletionScore,
            Reflection: reflection,
            Environment: run.Environment,
            Provenance: run.Provenance,
            Status: CreatedStatus,
            ReuseConfidence: reflection is not null ? InitialValidatedReuseConfidence : 0d,
            SupportingValidations: reflection is not null ? 1 : 0,
            Contradictions: 0,
            Revision: 0,
            CreatedAt: finalizedAt,
            UpdatedAt: finalizedAt);

        ExperienceRecordCreateResult created;
        try
        {
            created = await _store.CreateAsync(request.Authorization, record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PortFailed(FinalizationStage.CreateRecord, ex, evaluation, record: null, reflection);
        }

        switch (created.Outcome)
        {
            case ExperienceStoreOutcome.Created:
                break;

            case ExperienceStoreOutcome.Conflict:
                // This run has been finalized before (the ID is derived from it). Report the stored
                // outcome rather than writing anything a second time.
                return await ReplayAsync(request, run, record, evaluation, cancellationToken).ConfigureAwait(false);

            case ExperienceStoreOutcome.Denied:
                return Ended(
                    FinalizationOutcome.NotAuthorized,
                    FinalizationStage.CreateRecord,
                    "The store refused the record's scope as outside the host-established authorization; nothing was stored.",
                    evaluation: evaluation);

            case ExperienceStoreOutcome.Invalid:
                return Ended(
                    FinalizationOutcome.Failed,
                    FinalizationStage.CreateRecord,
                    "The store rejected the Experience Record as malformed; nothing was stored.",
                    new FinalizationFailure(
                        FinalizationStage.CreateRecord,
                        "The store reported the Experience Record invalid. See the validation errors.",
                        created.Errors,
                        Exception: null),
                    evaluation);

            default:
                return Ended(
                    FinalizationOutcome.Failed,
                    FinalizationStage.CreateRecord,
                    $"The store returned '{created.Outcome}', which is not a create outcome.",
                    new FinalizationFailure(
                        FinalizationStage.CreateRecord,
                        $"The Experience Record store returned '{created.Outcome}' from CreateAsync.",
                        created.Errors,
                        Exception: null),
                    evaluation);
        }

        // Stage 6 -- Commit the record's initial lifecycle event, which performs the real transition.
        return await CommitInitialEventAsync(request, run, record, evaluation, failure, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a create that conflicted because this run was already finalized. A record still at
    /// revision 0 had its create land but not its initial commit, so the commit is finished here;
    /// anything past revision 0 is fully finalized and is reported as it stands.
    /// </summary>
    private async Task<FinalizeExperienceResult> ReplayAsync(
        FinalizeExperienceRequest request,
        ExperienceRun run,
        ExperienceRecord attempted,
        VerificationResult evaluation,
        CancellationToken cancellationToken)
    {
        ExperienceRecordGetResult stored;
        try
        {
            stored = await _store
                .GetAsync(request.Authorization, run.Scope, attempted.ExperienceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PortFailed(FinalizationStage.CreateRecord, ex, evaluation, record: null, attempted.Reflection);
        }

        if (stored.Outcome != ExperienceStoreOutcome.Found || stored.Record is null)
        {
            // The derived ID is taken by a record this caller's scope cannot see. Nothing was written,
            // and nothing about the other record is revealed.
            return Ended(
                FinalizationOutcome.Failed,
                FinalizationStage.CreateRecord,
                "An Experience Record with this run's derived ID already exists outside the requested scope; nothing was stored.",
                new FinalizationFailure(
                    FinalizationStage.CreateRecord,
                    $"CreateAsync conflicted and the stored record is not readable in this scope ({stored.Outcome}).",
                    stored.Errors,
                    Exception: null),
                evaluation);
        }

        if (stored.Record.Revision > 0)
        {
            return AlreadyFinalized(stored.Record, evaluation);
        }

        // The earlier call created the record but never confirmed it. Finish that same commit, from
        // the stored record, so the event stays byte-for-byte what the first attempt would have sent.
        // The failure is reconstructed from the stored record, so a quarantined resume still names the
        // stage that decided it rather than reporting a reason-less quarantine.
        return await CommitInitialEventAsync(
            request,
            run,
            stored.Record,
            evaluation,
            FailureFor(stored.Record),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FinalizeExperienceResult> CommitInitialEventAsync(
        FinalizeExperienceRequest request,
        ExperienceRun run,
        ExperienceRecord record,
        VerificationResult evaluation,
        FinalizationFailure? failure,
        CancellationToken cancellationToken)
    {
        // Every field is a pure function of the stored record and the run, so a retry re-derives the
        // identical event and the store deduplicates it instead of appending a second one. This is a
        // real transition out of Candidate, so it goes through Core's transition table and the store's
        // prior-status guard, not a null-prior self-transition.
        var targetStatus = TargetStatusFor(record);
        var transition = new CommitLifecycleTransitionRequest(
            EventId: InitialEventIdFor(run.RunId),
            ExperienceId: record.ExperienceId,
            Scope: record.Scope,
            PriorStatus: CreatedStatus,
            CurrentStatus: targetStatus,
            Reason: InitialEventReason(record),
            Producer: ProducerIdentity,
            OccurredAt: record.CreatedAt,
            ExpectedRevision: 0);

        CommitLifecycleTransitionResult commit;
        try
        {
            commit = await _lifecycleService.CommitAsync(request.Authorization, transition, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PortFailed(FinalizationStage.CommitInitialEvent, ex, evaluation, record, record.Reflection);
        }

        if (commit.Outcome != LifecycleTransitionOutcome.Committed)
        {
            // Someone else may have finalized this record between the read above and this commit. A
            // moved revision is not a failure to report forever: re-read and converge on their result.
            if (commit.Outcome is LifecycleTransitionOutcome.StaleRevision or LifecycleTransitionOutcome.Conflict
                && await TryReadFinalizedAsync(request, record, cancellationToken).ConfigureAwait(false) is { } finalized)
            {
                return AlreadyFinalized(finalized, evaluation);
            }

            // The record exists and is still a Candidate. Report it, and why it was going to be
            // quarantined, so the host can reconcile rather than guess.
            return new FinalizeExperienceResult(
                FinalizationOutcome.Failed,
                FinalizationStage.CommitInitialEvent,
                record,
                commit.Event,
                record.Revision,
                evaluation,
                record.Reflection,
                failure ?? new FinalizationFailure(
                    FinalizationStage.CommitInitialEvent,
                    $"Committing the record's initial lifecycle event returned {commit.Outcome}.{(commit.Reason is null ? string.Empty : " " + commit.Reason)}",
                    commit.Errors,
                    Exception: null),
                $"The Experience Record's initial lifecycle event returned {commit.Outcome}, so the record is still a {record.Status} and finalization is not durable; the captured run is still available for a retry.");
        }

        // Mirror the projection the store just applied, so the returned record is the record as it now
        // stands rather than the pre-transition Candidate.
        var committed = record with
        {
            Status = targetStatus,
            Revision = commit.Revision,
            UpdatedAt = transition.OccurredAt,
        };

        return new FinalizeExperienceResult(
            targetStatus == ExperienceStatus.Validated ? FinalizationOutcome.Validated : FinalizationOutcome.Quarantined,
            FinalizationStage.CommitInitialEvent,
            committed,
            commit.Event,
            commit.Revision,
            evaluation,
            committed.Reflection,
            failure,
            Reason: null);
    }

    /// <summary>
    /// Re-reads the record after a commit the store refused, returning it only when its revision has
    /// moved past 0 -- that is, when someone else committed the initial event first. A read that fails
    /// or still shows revision 0 returns <see langword="null"/>, and the caller reports the original
    /// commit outcome.
    /// </summary>
    private async Task<ExperienceRecord?> TryReadFinalizedAsync(
        FinalizeExperienceRequest request,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            var reread = await _store
                .GetAsync(request.Authorization, record.Scope, record.ExperienceId, cancellationToken)
                .ConfigureAwait(false);

            return reread is { Outcome: ExperienceStoreOutcome.Found, Record.Revision: > 0 } ? reread.Record : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Reconciliation is best-effort; the caller still reports the commit outcome it saw.
            return null;
        }
    }

    private static FinalizeExperienceResult AlreadyFinalized(ExperienceRecord stored, VerificationResult evaluation) => new(
        FinalizationOutcome.AlreadyFinalized,
        FinalizationStage.CommitInitialEvent,
        stored,
        Event: null,
        stored.Revision,
        evaluation,
        stored.Reflection,
        FailureFor(stored),
        $"This run was already finalized as {stored.Status}; no second record and no second initial event were written.");

    /// <summary>
    /// The status a created record's initial event moves it to: <see cref="ExperienceStatus.Validated"/>
    /// only when the record carries a reflection, which it does only for a verified run whose
    /// reflection succeeded.
    /// </summary>
    private static ExperienceStatus TargetStatusFor(ExperienceRecord record) =>
        record.Reflection is not null ? ExperienceStatus.Validated : ExperienceStatus.Quarantined;

    /// <summary>
    /// Reconstructs the safe failure metadata for a record this call did not itself build -- a stored
    /// record found by a replay. A quarantine always names the stage that decided it.
    /// </summary>
    private static FinalizationFailure? FailureFor(ExperienceRecord record)
    {
        if (record.Reflection is not null)
        {
            return null;
        }

        return record.Outcome.Status == TaskVerificationStatus.Verified
            ? new FinalizationFailure(
                FinalizationStage.Reflect,
                "The stored record carries no reflection although its verification passed; it is quarantined without an eligible lesson.",
                NoErrors,
                Exception: null)
            : new FinalizationFailure(
                FinalizationStage.Evaluate,
                $"Verification resolved to {record.Outcome.Status} rather than {TaskVerificationStatus.Verified}; the record is quarantined without an eligible lesson.",
                NoErrors,
                Exception: null);
    }

    /// <summary>
    /// The initial event's auditable reason, derived only from the record so a retry -- which reads
    /// the stored record rather than recomputing -- produces exactly the same text.
    /// </summary>
    private static string InitialEventReason(ExperienceRecord record) => string.Format(
        CultureInfo.InvariantCulture,
        "Initial finalization: verification {0}, completion score {1}, {2}.",
        record.Outcome.Status,
        record.CompletionScore.ToString("R", CultureInfo.InvariantCulture),
        record.Reflection is null ? "no eligible lesson recorded" : "reflection recorded");

    /// <summary>
    /// Turns any non-cancellation exception a port threw into a structured failed stage, so "every
    /// stage failure comes back as a structured result" holds for more than
    /// <see cref="ExperienceStoreException"/>.
    /// </summary>
    private static FinalizeExperienceResult PortFailed(
        FinalizationStage stage,
        Exception exception,
        VerificationResult? evaluation,
        ExperienceRecord? record,
        Reflection? reflection) => new(
            FinalizationOutcome.Failed,
            stage,
            record,
            Event: null,
            record?.Revision ?? 0,
            evaluation,
            reflection,
            new FinalizationFailure(
                stage,
                $"The {stage} stage's port threw {exception.GetType().FullName}.",
                NoErrors,
                exception),
            "A port failed, so finalization is not durable; the captured run is still available for a retry.");

    private static FinalizeExperienceResult Ended(
        FinalizationOutcome outcome,
        FinalizationStage stage,
        string reason,
        FinalizationFailure? failure = null,
        VerificationResult? evaluation = null) => new(
            outcome,
            stage,
            Record: null,
            Event: null,
            Revision: 0,
            evaluation,
            Reflection: null,
            failure,
            reason);

    /// <summary>
    /// Truncates to whole microseconds in UTC, which is the precision PostgreSQL's <c>timestamptz</c>
    /// keeps. Without this, a value read back from the store would differ from the one sent, and a
    /// replay's re-derived lifecycle event would no longer be identical to the stored one.
    /// </summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
    }

    /// <summary>
    /// Derives a stable identifier from a run ID and a per-purpose tag: SHA-256 over a fixed
    /// namespace, the run ID, and the tag, stamped with the RFC 9562 custom version (8) and variant.
    /// Same run in, same identifiers out -- which is what makes replaying finalization safe.
    /// </summary>
    private static Guid Derive(Guid runId, byte tag)
    {
        Span<byte> input = stackalloc byte[33];
        DerivationNamespace.TryWriteBytes(input[..16], bigEndian: true, out _);
        runId.TryWriteBytes(input.Slice(16, 16), bigEndian: true, out _);
        input[32] = tag;

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        var id = hash[..16];
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id, bigEndian: true);
    }
}
