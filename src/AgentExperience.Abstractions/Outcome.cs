namespace AgentExperience.Abstractions;

/// <summary>
/// Whether a task's completion has been verified. This is distinct from
/// <see cref="RunExecutionStatus"/>: a run can execute to completion and still have an
/// <see cref="Unknown"/> task verification status when no evidence was available to confirm
/// the task was actually accomplished. Unverified runs must surface as
/// <see cref="Unknown"/> rather than being reported as a validated procedure.
/// </summary>
public enum TaskVerificationStatus
{
    /// <summary>No sufficient evidence exists to confirm or deny task completion.</summary>
    Unknown,

    /// <summary>Evidence confirms the task was completed as intended.</summary>
    Verified,

    /// <summary>Evidence confirms the task was not completed as intended.</summary>
    Failed,
}

/// <summary>
/// A single piece of evidence backing an <see cref="Outcome"/>, produced by a deterministic
/// evaluator (tool exit code, test result, workflow completion, human approval, human
/// correction, etc.) or another producer. Evidence content must never carry private
/// chain-of-thought; it records observable facts only.
/// </summary>
/// <param name="EvidenceId">Unique identifier for this piece of evidence.</param>
/// <param name="VerificationRoundId">The verification round this evidence was produced in.</param>
/// <param name="ArtifactRevision">The revision of the artifact under verification this evidence applies to.</param>
/// <param name="Kind">The kind of evidence, e.g. "ToolExitCode", "TestResult", "WorkflowCompletion", "HumanApproval", "HumanCorrection".</param>
/// <param name="Producer">Identity of whatever produced this evidence (an evaluator name, a tool, or a human principal identifier). Not tied to any identity-provider shape.</param>
/// <param name="Detail">Optional sanitized, human-readable detail supporting the evidence (e.g. a truncated log excerpt). Never private reasoning.</param>
/// <param name="CapturedAt">When this evidence was captured.</param>
public sealed record Evidence(
    Guid EvidenceId,
    Guid VerificationRoundId,
    string ArtifactRevision,
    string Kind,
    string Producer,
    string? Detail,
    DateTimeOffset CapturedAt);

/// <summary>
/// The verified (or unverified) result of an <see cref="ExperienceRun"/>'s task, distinct from
/// the run's own execution status. An <see cref="ExperienceRun"/> can fail to execute yet still
/// carry an <see cref="Outcome"/> (e.g. <see cref="TaskVerificationStatus.Unknown"/>), and a run
/// that executed to completion can still be unverified. This lets both a verified success and an
/// unverified failure be represented without requiring private chain-of-thought as evidence.
/// </summary>
/// <param name="Status">The task verification status.</param>
/// <param name="Evidence">The evidence backing this outcome, in the order it was produced.</param>
/// <param name="Reason">Optional, auditable, human-readable explanation for the status. Never private reasoning.</param>
/// <param name="EvaluatedAt">When this outcome was determined.</param>
public sealed record Outcome(
    TaskVerificationStatus Status,
    IReadOnlyList<Evidence> Evidence,
    string? Reason,
    DateTimeOffset EvaluatedAt);
