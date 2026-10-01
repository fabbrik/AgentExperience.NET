namespace AgentExperience.Core.Reflections;

/// <summary>
/// Why finalization's screening refused a reflection (see <see cref="ReflectionScreening"/>). A closed set,
/// carried on <see cref="Finalization.FinalizationFailure.ScreeningRefusal"/> and on the <c>finalize</c>
/// span, so a host can tell, for example, "my sanitizer rejects the kind" apart from "the reflector wrote
/// too much" without parsing a reason.
/// </summary>
public enum ReflectionScreeningRefusal
{
    /// <summary>A field, a list item or a list count is over its <see cref="ReflectionLimits"/> limit, before or after the sanitizer.</summary>
    OverLimit,

    /// <summary>The lesson is missing, or empty once invisible characters are removed.</summary>
    MissingLesson,

    /// <summary>The producer is missing, or empty once invisible characters are removed.</summary>
    MissingProducer,

    /// <summary>The reflector returned a <see langword="null"/> list.</summary>
    MissingField,

    /// <summary>Reading one of the reflector's lists threw.</summary>
    Unreadable,

    /// <summary>
    /// The host sanitizer rejected the <see cref="ReflectionScreening.PayloadKind"/> payload. A host whose
    /// every record is quarantined with this code has a sanitizer that does not allow the kind.
    /// </summary>
    SanitizerRejected,

    /// <summary>The host sanitizer threw, cancelled on its own, or returned a result that is neither an allowed payload of text nor a rejection.</summary>
    SanitizerFailed,

    /// <summary>The host sanitizer did not answer within <see cref="ReflectionLimits.SanitizerTimeout"/>.</summary>
    SanitizerTimedOut,

    /// <summary>The host sanitizer omitted a screened field or a list item (reported it omitted, or left it out of its result).</summary>
    FieldOmitted,

    /// <summary>
    /// A model-authored reflection (<see cref="AgentExperience.Abstractions.ReflectionAuthorship.Model"/>) holds
    /// a URL or hostname that is not in the captured run it reflected, instruction-override phrasing, or
    /// credential-shaped text. The reason names the field and the rule, never the matched text. A heuristic:
    /// see the finalization guide's "Limits of model-authored lessons".
    /// </summary>
    UnsafeContent,

    /// <summary>The reflection's <see cref="AgentExperience.Abstractions.Reflection.Authorship"/> is not a defined value.</summary>
    UndefinedAuthorship,
}
