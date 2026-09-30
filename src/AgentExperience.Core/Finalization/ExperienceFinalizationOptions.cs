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
}
