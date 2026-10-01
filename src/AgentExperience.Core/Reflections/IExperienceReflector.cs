using AgentExperience.Abstractions;

namespace AgentExperience.Core.Reflections;

/// <summary>
/// The stable port that turns an evaluated <see cref="ExperienceRun"/> into a structured,
/// auditable <see cref="Reflection"/>. <see cref="DefaultExperienceReflector"/> is the deterministic,
/// template-based implementation; an alternate implementation (e.g. one that phrases lessons
/// differently) may replace it, but must preserve the structured-content contract below so that
/// downstream consumers never depend on which implementation produced a reflection.
/// </summary>
/// <remarks>
/// <para><b>Structured-content contract</b> every implementation must preserve:</para>
/// <list type="number">
/// <item><description><see cref="Reflection.ReflectionId"/> and <see cref="Reflection.CreatedAt"/> are copied from the request; <see cref="Reflection.ExperienceRunId"/> is the request run's <see cref="ExperienceRun.RunId"/>. A reflector never generates identity or reads the clock itself.</description></item>
/// <item><description><see cref="Reflection.VerificationStatus"/>, <see cref="Reflection.CompletionScore"/>, and <see cref="Reflection.VerificationRuleVersion"/> exactly match the request's evaluation; <see cref="Reflection.EvidenceIds"/> are the evaluation outcome's distinct evidence IDs, in the order the evidence was produced. The supplied evaluation is the only source of verdict and evidence.</description></item>
/// <item><description><see cref="Reflection.Lesson"/> is non-empty. <see cref="Reflection.SuccessfulApproaches"/> is non-empty only when the verification status is <see cref="TaskVerificationStatus.Verified"/>.</description></item>
/// <item><description>Every non-<see cref="TaskVerificationStatus.Verified"/> reflection carries a warning that it is not a validated procedure, and non-<see langword="null"/> <see cref="Reflection.ReuseGuidance"/> saying the same.</description></item>
/// <item><description>Environment preconditions that were not captured are listed as <c>unknown</c> in <see cref="Reflection.Preconditions"/> and repeated in <see cref="Reflection.Warnings"/>, never omitted.</description></item>
/// <item><description>No causal explanation is invented beyond quoting captured, sanitized error/reason text; no reuse-confidence value is produced (that belongs to the durable Experience Record); no private chain-of-thought or reasoning content is produced or required.</description></item>
/// <item><description><see cref="Reflection.Producer"/> identifies the implementation and its version.</description></item>
/// <item><description><see cref="Reflection.Authorship"/> is <see cref="ReflectionAuthorship.Model"/> whenever a model wrote any of the free text. Authorship is self-declared: nothing infers it, so a model-backed reflector that leaves the default (<see cref="ReflectionAuthorship.Deterministic"/>) escapes finalization's content guard and the injection label. Such a reflector should also implement <see cref="IReflectionRunContent"/>, so the guard compares links with exactly what it sent.</description></item>
/// <item><description>Invalid input throws <see cref="ArgumentNullException"/>/<see cref="ArgumentException"/>; a cancelled token throws <see cref="OperationCanceledException"/>. In both cases no <see cref="Reflection"/> is produced.</description></item>
/// </list>
/// <para>
/// <b>Binding.</b> A reflector never has to check that the run and the evaluation it is handed belong
/// together: a <see cref="ReflectionRequest"/> cannot be constructed otherwise (see its remarks). What
/// a reflector returns is checked against its request by <see cref="ReflectionRequest.EnsureMatches"/>
/// (contract items 1 and 2). Finalization applies that check to every reflection, and a reflection
/// that fails it is handled exactly like a reflector that threw: the record is kept, quarantined,
/// with no lesson.
/// </para>
/// <para>
/// <b>Screening.</b> What a reflector writes is not trusted either. After the binding check,
/// finalization screens the free-text fields -- <see cref="Reflection.Lesson"/>, the four lists and
/// <see cref="Reflection.ReuseGuidance"/> -- and <see cref="Reflection.Producer"/> before the record is
/// created, whichever reflector wrote them (see <see cref="ReflectionScreening"/>): each is held to
/// <see cref="ReflectionLimits"/> and refused, never truncated, when over; invisible characters are
/// removed, and a text left empty counts as absent (a missing lesson or producer refuses the reflection);
/// then the six free-text fields go through the host's <see cref="ISanitizer"/> as a
/// <see cref="ReflectionScreening.PayloadKind"/> payload. A refused reflection is handled like a binding
/// mismatch, with a <see cref="ReflectionScreeningRefusal"/> saying why. So a reflector should write plain
/// text within <see cref="ReflectionLimits.Default"/>, as <see cref="DefaultExperienceReflector"/> does:
/// what it writes past a limit costs the record its lesson, and an invisible character it writes is not
/// stored.
/// </para>
/// </remarks>
public interface IExperienceReflector
{
    /// <summary>
    /// Produces a <see cref="Reflection"/> for <paramref name="request"/>'s run and evaluation,
    /// honoring this interface's structured-content contract.
    /// </summary>
    /// <param name="request">The finished run, its evaluation, and the caller-supplied reflection identity/timestamp.</param>
    /// <param name="cancellationToken">A cancelled token throws <see cref="OperationCanceledException"/> before anything is produced.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The run is still in progress, or its own outcome status disagrees with the evaluation's, or the request is otherwise malformed.</exception>
    Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default);
}
