using AgentExperience.Abstractions;
using AgentExperience.Core.Reflections;

namespace AgentExperience.Core.Finalization;

/// <summary>
/// Options for <see cref="ExperienceFinalizationService"/>. Pass them to its constructor, or register a
/// singleton before or after <c>AddAgentExperienceCore</c>; without them, the defaults apply.
/// </summary>
public sealed record ExperienceFinalizationOptions
{
    /// <summary>The documented defaults.</summary>
    public static ExperienceFinalizationOptions Default { get; } = new();

    /// <summary>
    /// The limits every reflection is screened against before its record is created. Defaults to
    /// <see cref="ReflectionLimits.Default"/>.
    /// </summary>
    public ReflectionLimits ReflectionLimits
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(ReflectionLimits));
    } = ReflectionLimits.Default;

    /// <summary>
    /// Whether finalizing a run that was given stored lessons submits confidence evidence about them. Defaults to
    /// <see cref="ReuseEvidenceMode.Off"/>.
    /// </summary>
    /// <remarks>
    /// Under <see cref="ReuseEvidenceMode.SameTask"/>, once a run's record is durable, finalization submits machine
    /// evidence for every record the run was exposed to (<see cref="Provenance.ExposedTo"/>) that is readable in the
    /// run's scope and has the same <see cref="ExperienceRecord.TaskId"/>: supporting when the run verified, and
    /// contradicting when it failed verification and <see cref="ContradictOnFailure"/> is set. The evidence goes
    /// through <see cref="Lifecycle.ExperienceLifecycleService.ApplyEvidenceAsync"/>, bound to the round finalization
    /// closed, so independence verification applies exactly as for evidence a host submits. What happened is on
    /// <see cref="FinalizeExperienceResult.ReuseEvidence"/>; it never changes the finalization outcome.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="ReuseEvidenceMode"/>.</exception>
    public ReuseEvidenceMode ReuseEvidence
    {
        get;
        init => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ReuseEvidence), value, "Not a defined ReuseEvidenceMode.");
    } = ReuseEvidenceMode.Off;

    /// <summary>
    /// Under <see cref="ReuseEvidenceMode.SameTask"/>, whether a run that failed verification submits contradicting
    /// evidence about the records it was given. Defaults to <see langword="false"/>: only a verified run submits
    /// (supporting) evidence, because a failure is not necessarily the lesson's fault. Has no effect while
    /// <see cref="ReuseEvidence"/> is <see cref="ReuseEvidenceMode.Off"/>.
    /// </summary>
    public bool ContradictOnFailure { get; init; }

    /// <summary>The default for <see cref="ReuseEvidenceTimeout"/>: ten seconds.</summary>
    public static TimeSpan DefaultReuseEvidenceTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the reuse-evidence step may take, in total, before it stops and reports the list as truncated
    /// (<see cref="FinalizeExperienceResult.ReuseEvidenceTruncated"/>). The run's record is already durable, so this
    /// bounds nothing but the caller's wait. Defaults to <see cref="DefaultReuseEvidenceTimeout"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not strictly positive, or is longer than <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts.</exception>
    public TimeSpan ReuseEvidenceTimeout
    {
        get;
        init => field = value > TimeSpan.Zero && value.TotalMilliseconds <= uint.MaxValue - 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ReuseEvidenceTimeout), value, "The reuse-evidence budget must be strictly positive and at most uint.MaxValue - 1 milliseconds.");
    } = DefaultReuseEvidenceTimeout;
}

/// <summary>
/// Whether finalization turns a run's reuse of stored lessons into confidence evidence. See
/// <see cref="ExperienceFinalizationOptions.ReuseEvidence"/>.
/// </summary>
public enum ReuseEvidenceMode
{
    /// <summary>Finalization submits no evidence about the records a run was given. The default.</summary>
    Off = 0,

    /// <summary>
    /// Finalization submits machine evidence about each record the run was given that is on the run's own task
    /// (the same <see cref="ExperienceRecord.TaskId"/>, compared ordinally).
    /// </summary>
    SameTask = 1,
}
