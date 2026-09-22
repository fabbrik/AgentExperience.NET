using AgentExperience.Abstractions;
using AgentExperience.Core.Indexing;
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
    /// The host permitted storage but the run did not verify, or reflection failed: the record was
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
    Exception? Exception);

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
