namespace AgentExperience.Core.Confidence;

/// <summary>
/// Turns a record's evidence counters into its reuse confidence. The lifecycle service scores every
/// accepted piece of evidence through it, and <c>ReadConfidenceAsync</c> scores its recomputation through it
/// too. Without a host engine, <see cref="ReuseConfidenceHeuristicEngine"/> computes Laplace's rule,
/// <c>(1 + S) / (2 + S + F)</c>, exactly as before.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine owns only the score.</b> Which statuses accept evidence, the status an update moves a
/// record to, the counter increments, independence keys, verification and exposure admission, and
/// idempotent replay all stay in the library, and no engine can change them. A number never makes an
/// ineligible record eligible.
/// </para>
/// <para>
/// <b>Identity is recorded, not configured.</b> <see cref="RuleId"/> and <see cref="RuleVersion"/> must each
/// be 1 to 64 characters from <c>[A-Za-z0-9._-]</c>, and a host engine may not use the default's
/// <see cref="ReuseConfidenceHeuristicEngine.HeuristicRuleId"/>. They are read and checked once, when the
/// lifecycle service is constructed, and a violation throws <see cref="ArgumentException"/> there. Every
/// update the service submits records <c>"{RuleId}/{RuleVersion}"</c> in
/// <see cref="AgentExperience.Abstractions.ConfidenceUpdate.RuleVersion"/>; the default engine records the
/// plain <c>"1.0.0"</c> it always has. Change <see cref="RuleVersion"/> whenever the arithmetic changes, so
/// stored scores stay auditable against the rule that produced them.
/// </para>
/// <para>
/// <b>The score is validated, never repaired.</b> A value that is NaN, infinite, or outside [0, 1] throws
/// <see cref="InvalidOperationException"/> naming the rule, and nothing is written. An exception the engine
/// throws propagates unchanged, and nothing is written either. Confidence is stored evidence, so a wrong
/// value is refused rather than clamped.
/// </para>
/// <para>
/// <b>It must be deterministic and thread-safe.</b> One instance serves concurrent calls, and the same input
/// must produce the same score: the score is computed from the record as it was read and committed against
/// that revision. It runs synchronously, so it must not block or perform I/O. <c>AddAgentExperienceCore</c>
/// resolves it once, from the root provider, so register it as a singleton.
/// </para>
/// <para>
/// <b>What it is given.</b> The record and two counters, never the evidence's kind, source, run or reviewer.
/// The supporting counter includes the validation a record was finalized with, so it starts at 1. On a
/// confidence read that recomputes, <see cref="ExperienceConfidenceInput.Record"/> still carries the stored,
/// unfiltered counters and score; score from the input's counters, which are the filtered ones. The stored
/// score is what <see cref="Retrieval.RetrievalPolicy.MinimumConfidence"/> is compared against, so the floor
/// applies on this engine's scale.
/// </para>
/// <para>
/// <b>It applies from the first evidence onward.</b> Finalization still stamps a freshly validated record
/// with the heuristic's initial 2/3, without asking the engine
/// (<see cref="Finalization.ExperienceFinalizationService.InitialValidatedReuseConfidence"/>); the engine's
/// score replaces it when the record's first evidence is applied.
/// </para>
/// </remarks>
public interface IExperienceConfidenceEngine
{
    /// <summary>
    /// A stable name for the rule this engine computes. 1 to 64 characters from <c>[A-Za-z0-9._-]</c>.
    /// </summary>
    string RuleId { get; }

    /// <summary>
    /// The version of the rule. 1 to 64 characters from <c>[A-Za-z0-9._-]</c>. Change it whenever the
    /// arithmetic changes.
    /// </summary>
    string RuleVersion { get; }

    /// <summary>Scores one record's evidence.</summary>
    /// <param name="input">The record as it was read, and the counters after this evidence is applied.</param>
    /// <returns>The reuse confidence, in [0, 1]. Anything else throws <see cref="InvalidOperationException"/> in the caller, and nothing is written.</returns>
    double Score(ExperienceConfidenceInput input);
}
