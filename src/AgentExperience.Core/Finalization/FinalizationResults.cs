using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Verification;

namespace AgentExperience.Core.Finalization;

/// <summary>
/// The ordered stages of <see cref="ExperienceFinalizationService.FinalizeAsync"/>. Every result
/// names the stage it ended at, so a host always knows how far finalization got.
/// </summary>
public enum FinalizationStage
{
    /// <summary>Reading the captured run's snapshot back from the capture service.</summary>
    Load,

    /// <summary>Computing the run's verification result from the host-closed round and its evidence.</summary>
    Evaluate,

    /// <summary>Checking the run's scope against the host authorization, then the host's storage decision. No store call has been made yet, and the reflector has not been called.</summary>
    Authorize,

    /// <summary>Reflecting on the evaluated run. Skipped entirely when verification did not pass, or when either gate above refused.</summary>
    Reflect,

    /// <summary>Creating the Experience Record through the store port.</summary>
    CreateRecord,

    /// <summary>Committing the record's initial lifecycle event, atomically with its projection.</summary>
    CommitInitialEvent,
}

/// <summary>
/// The disposition one <see cref="ExperienceFinalizationService.FinalizeAsync"/> call reached.
/// </summary>
public enum FinalizationOutcome
{
    /// <summary>
    /// The run verified, reflection succeeded, and the host permitted storage: the record was created
    /// as <see cref="ExperienceStatus.Candidate"/> and its initial lifecycle event moved it to
    /// <see cref="ExperienceStatus.Validated"/>.
    /// </summary>
    Validated,

    /// <summary>
    /// The host permitted storage but the run did not verify, or reflection failed or was refused by
    /// screening: the record was
    /// created as <see cref="ExperienceStatus.Candidate"/>, carrying no reflection, and its initial
    /// lifecycle event moved it to <see cref="ExperienceStatus.Quarantined"/>. <c>Failure</c> names why.
    /// </summary>
    Quarantined,

    /// <summary>
    /// This run had already been finalized, by an earlier call or by a concurrent one. Nothing was
    /// written: no second record and no second initial event. <c>Record</c> and <c>Revision</c> report
    /// the stored outcome, <c>Status</c> is the status that call produced, and <c>Failure</c> names why
    /// it was quarantined when it was.
    /// </summary>
    AlreadyFinalized,

    /// <summary>
    /// The host's <see cref="StorageDecision"/> did not permit storage. Nothing was written, no
    /// record ID was issued, and <c>Reason</c> carries the host's own content-free reason.
    /// </summary>
    StorageDenied,

    /// <summary>
    /// The run's scope lies outside the host-established <see cref="AuthorizationContext"/>. Denied
    /// before any store call; nothing was written.
    /// </summary>
    NotAuthorized,

    /// <summary>No captured run exists for the requested run ID. Nothing was written.</summary>
    RunNotFound,

    /// <summary>
    /// The run exists but has no <see cref="ExperienceRun.ExecutionStatus"/>, so it has not finished
    /// and cannot be finalized. Nothing was written.
    /// </summary>
    RunNotFinished,

    /// <summary>
    /// A stage failed. <c>Stage</c> and <c>Failure</c> name which and why. This is never a durable
    /// success: the captured run stays available so the host can retry finalization. When the record
    /// had already been created but its initial event was refused, <c>Record</c> carries that record --
    /// still a <see cref="ExperienceStatus.Candidate"/>, so it is not reusable -- and <c>Failure</c>
    /// carries either the commit's own refusal or the reason the record was going to be quarantined.
    /// <para>
    /// One case is terminal rather than retryable: the run's record was erased (the store reported
    /// <see cref="ExperienceStoreOutcome.Deleted"/>), whether before this call or between its create and
    /// its initial commit. <c>Reason</c> says so, <c>Record</c> is <see langword="null"/>, and a retry
    /// ends the same way, because re-finalizing the run would recreate what the erasure removed.
    /// </para>
    /// </summary>
    Failed,
}

/// <summary>
/// Why a finalization stage could not produce what it was asked for. Present on
/// <see cref="FinalizationOutcome.Failed"/>, and also on
/// <see cref="FinalizationOutcome.Quarantined"/>, where it is the safe failure metadata explaining
/// why the record carries no eligible lesson.
/// </summary>
/// <param name="Stage">The stage the failure belongs to. On a quarantined record this is the stage that decided it could not be validated, which is not necessarily the stage the call ended at.</param>
/// <param name="Reason">A content-free, auditable explanation. Never echoes captured content, record payload, or private reasoning.</param>
/// <param name="Errors">The store's validation errors when a store call reported the request malformed; otherwise empty.</param>
/// <param name="Exception">The exception behind the failure, if any. Handed to the host for diagnostics only -- never persisted, and never recorded into the Experience Record.</param>
public sealed record FinalizationFailure(
    FinalizationStage Stage,
    string Reason,
    IReadOnlyList<StoreValidationError> Errors,
    Exception? Exception)
{
    /// <summary>
    /// Why screening refused the reflection, when that is what quarantined the record; otherwise
    /// <see langword="null"/>. See <see cref="AgentExperience.Core.Reflections.ReflectionScreening"/>.
    /// </summary>
    public AgentExperience.Core.Reflections.ReflectionScreeningRefusal? ScreeningRefusal { get; init; }

    /// <summary>
    /// The full name of the exception type behind a screening refusal -- a host sanitizer that threw, or a
    /// reflector list that threw while it was read -- when there was one. The exception itself is withheld
    /// (<see cref="Exception"/> is <see langword="null"/>), because its message may quote the reflection.
    /// </summary>
    public string? ExceptionType { get; init; }
}

/// <summary>
/// The result of one <see cref="ExperienceFinalizationService.FinalizeAsync"/> call.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Stage">The stage finalization ended at.</param>
/// <param name="Record">
/// The Experience Record, when one exists: the record this call created (with the projection its
/// initial event applied, when that committed), or the stored record a replay found.
/// <see langword="null"/> whenever nothing was persisted. On
/// <see cref="FinalizationOutcome.Failed"/> after a refused initial commit it is the created record,
/// still a <see cref="ExperienceStatus.Candidate"/> at revision 0.
/// </param>
/// <param name="Event">The initial lifecycle event this call committed, or <see langword="null"/> when none was committed by this call.</param>
/// <param name="Revision">The record's revision after the initial event was committed (1), or the stored revision reported by a replay; 0 when the record is still an uncommitted <see cref="ExperienceStatus.Candidate"/> or nothing was written.</param>
/// <param name="Evaluation">The verification result finalization computed for this run, once the evaluate stage ran; otherwise <see langword="null"/>.</param>
/// <param name="Reflection">The reflection stored on the record, or <see langword="null"/> -- always <see langword="null"/> for a quarantined record.</param>
/// <param name="Failure">Why the call failed, or why a record was (or was going to be) quarantined; otherwise <see langword="null"/>.</param>
/// <param name="Reason">Optional, auditable, content-free explanation of the outcome.</param>
/// <param name="Indexing">
/// What the optional post-commit indexing hook did, when one is wired in and this call actually
/// committed the record's initial event; otherwise <see langword="null"/>. It is reported, never
/// acted on: an indexing failure here is always retryable and never changes
/// <see cref="Outcome"/>, <see cref="IsDurable"/>, or anything about the stored record. A
/// <see langword="null"/> value means no indexing was attempted -- because no hook is registered, or
/// because nothing was committed by this call (a replay of an already-finalized run never re-indexes).
/// </param>
public sealed record FinalizeExperienceResult(
    FinalizationOutcome Outcome,
    FinalizationStage Stage,
    ExperienceRecord? Record,
    LifecycleEvent? Event,
    long Revision,
    VerificationResult? Evaluation,
    Reflection? Reflection,
    FinalizationFailure? Failure,
    string? Reason,
    ExperienceIndexingResult? Indexing = null)
{
    /// <summary>
    /// The paths of the reflection fields the host sanitizer redacted while this call screened the
    /// reflection (<c>Lesson</c>, <c>Warnings[2]</c>, ...), never their values. An item's index is its
    /// index in the stored list; a redacted item that screening then dropped as blank is not listed.
    /// Empty when nothing was redacted and when no reflection was stored. The paths are reported, never
    /// persisted: a replay -- <see cref="FinalizationOutcome.AlreadyFinalized"/>, or a retry that finishes an
    /// earlier call's initial commit -- carries none, even though the retry reflects and screens again,
    /// through the reflector and the sanitizer, before the store's conflict shows the record already exists. See
    /// <see cref="AgentExperience.Core.Reflections.ReflectionScreening"/>.
    /// </summary>
    public IReadOnlyList<string> ReflectionRedactedFieldPaths
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(ReflectionRedactedFieldPaths));
    } = [];

    /// <summary>
    /// The confidence evidence this call submitted about the records the run was given, one entry per distinct record
    /// the run's provenance names (in that order, at most <see cref="RunExposure.MaxPerRun"/>, never the run's own
    /// record): submitted, or <see cref="ReuseEvidenceResult.Skipped"/> with the reason. Empty when
    /// <see cref="ExperienceFinalizationOptions.ReuseEvidence"/> is <see cref="ReuseEvidenceMode.Off"/> (the default),
    /// when no record is durable, when the run neither verified nor (with
    /// <see cref="ExperienceFinalizationOptions.ContradictOnFailure"/>) failed, when finalization closed no round, and
    /// when the run was given no record. Reported, never acted on: nothing here changes
    /// <see cref="Outcome"/> or <see cref="IsDurable"/>. A replay (<see cref="FinalizationOutcome.AlreadyFinalized"/>)
    /// submits the same evidence under the same identifiers, so the store reports the originals
    /// (<see cref="ReuseEvidenceResult.Replay"/>) and nothing is counted twice.
    /// </summary>
    public IReadOnlyList<ReuseEvidenceResult> ReuseEvidence
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(ReuseEvidence));
    } = [];

    /// <summary>
    /// Whether the reuse-evidence step stopped before it reached every record the run was given, because the caller's
    /// token was cancelled or <see cref="ExperienceFinalizationOptions.ReuseEvidenceTimeout"/> passed. The last entry of
    /// <see cref="ReuseEvidence"/> is the record it stopped at; the records after it are not reported. Finalizing the run
    /// again resubmits them.
    /// </summary>
    public bool ReuseEvidenceTruncated { get; init; }

    /// <summary>The Experience Record's ID, when one exists. No ID is issued when nothing was persisted.</summary>
    public Guid? ExperienceId => Record?.ExperienceId;

    /// <summary>The record's lifecycle status, when one exists.</summary>
    public ExperienceStatus? Status => Record?.Status;

    /// <summary>
    /// Whether an Experience Record for this run is durably stored and confirmed by its initial
    /// lifecycle event -- true only for <see cref="FinalizationOutcome.Validated"/>,
    /// <see cref="FinalizationOutcome.Quarantined"/>, and
    /// <see cref="FinalizationOutcome.AlreadyFinalized"/>. A database failure is never reported as
    /// durable success.
    /// </summary>
    public bool IsDurable => Outcome is FinalizationOutcome.Validated
        or FinalizationOutcome.Quarantined
        or FinalizationOutcome.AlreadyFinalized;
}

/// <summary>
/// What finalization's reuse-evidence step did for one record the run was given (see
/// <see cref="ExperienceFinalizationOptions.ReuseEvidence"/>).
/// </summary>
/// <param name="ExperienceId">The record the evidence is about.</param>
/// <param name="Kind">Supporting for a verified run; contradicting for a failed one under <see cref="ExperienceFinalizationOptions.ContradictOnFailure"/>.</param>
/// <param name="Outcome">
/// What <see cref="ExperienceLifecycleService.ApplyEvidenceAsync"/> answered, or <see langword="null"/> when nothing was
/// submitted: the record was <see cref="Skipped"/>, or reading it or applying the evidence threw, was cancelled or timed
/// out (<see cref="ExceptionType"/> names the exception). A refusal, such as
/// <see cref="ConfidenceUpdateOutcome.Unverified"/> or <see cref="ConfidenceUpdateOutcome.Ineligible"/>, is reported here
/// and never retried.
/// </param>
/// <param name="Reason">The lifecycle service's content-free reason, why the record was skipped, or why the step failed; otherwise <see langword="null"/>.</param>
public sealed record ReuseEvidenceResult(
    Guid ExperienceId,
    ConfidenceEvidenceKind Kind,
    ConfidenceUpdateOutcome? Outcome,
    string? Reason)
{
    /// <summary>
    /// Whether the evidence moved the record's counters. <see langword="false"/> for a duplicate (its independence key
    /// was already counted), for host-trusted evidence recorded only, for a <see cref="Replay"/>, and for every outcome
    /// other than <see cref="ConfidenceUpdateOutcome.Applied"/>.
    /// </summary>
    public bool Counted { get; init; }

    /// <summary>
    /// Whether the record was not submitted for, because it could not be read in the run's scope (any read outcome other
    /// than found is reported here), is readable only through a sharing grant, is on another task, or came from this run.
    /// <see cref="Reason"/> says which; <see cref="Outcome"/> is <see langword="null"/>.
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>
    /// Whether the store already held this evidence, under the same identifiers, from an earlier finalization of the
    /// same run, and reported it again. A replay is never <see cref="Counted"/>, whatever the original counted, so a sum
    /// over every call counts each piece of evidence once.
    /// </summary>
    public bool Replay { get; init; }

    /// <summary>Which identifier failed independence verification, on <see cref="ConfidenceUpdateOutcome.Unverified"/>; otherwise <see langword="null"/>.</summary>
    public IndependenceRefusal? Refusal { get; init; }

    /// <summary>The full name of the exception's type when the step threw for this record; otherwise <see langword="null"/>. The exception itself is not kept.</summary>
    public string? ExceptionType { get; init; }
}
