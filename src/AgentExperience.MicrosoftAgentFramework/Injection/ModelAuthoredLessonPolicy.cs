namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// Whether <see cref="ExperienceContextProvider"/> injects records whose lesson a model wrote
/// (<see cref="AgentExperience.Abstractions.ReflectionAuthorship.Model"/>). See
/// <see cref="ExperienceInjectionOptions.ModelAuthoredLessons"/>.
/// </summary>
public enum ModelAuthoredLessonPolicy
{
    /// <summary>Inject them, each labelled with <see cref="HistoricalReferenceWriter.ModelAuthoredLine"/>. The default.</summary>
    Include = 0,

    /// <summary>Omit them, as <see cref="InjectionOmissionReason.ModelAuthored"/>.</summary>
    Exclude = 1,
}
