using AgentExperience.Abstractions;

// Shared source (story 18.2): the exact wording of the default reflector that the compact Historical Reference
// leaves out. Core's DefaultExperienceReflector writes these strings and the MAF adapter's HistoricalReferenceWriter
// recognises them, both from this one file, so a change of wording cannot silently stop the filter matching. Each
// project defines exactly one of the constants below, which puts the type in that project's own namespace.
#if AGENTEXPERIENCE_CORE && AGENTEXPERIENCE_MAF
#error Define exactly one of AGENTEXPERIENCE_CORE and AGENTEXPERIENCE_MAF.
#elif AGENTEXPERIENCE_CORE
namespace AgentExperience.Core.Reflections;
#elif AGENTEXPERIENCE_MAF
namespace AgentExperience.MicrosoftAgentFramework.Reflections;
#else
#error Define AGENTEXPERIENCE_CORE or AGENTEXPERIENCE_MAF to select the namespace of the shared default-reflector text.
#endif

/// <summary>Wording the default reflector writes, shared with the writer that recognises it.</summary>
internal static class DefaultReflectionText
{
    /// <summary>What every <see cref="Reflection.Producer"/> the default reflector writes starts with, before its template version.</summary>
    public const string ProducerPrefix = "AgentExperience.DefaultExperienceReflector/";

    /// <summary>The value a precondition the default reflector could not capture is given.</summary>
    public const string UnknownValue = "unknown";

    /// <summary>What ends a precondition the default reflector could not capture (<c>Runtime version: unknown</c>).</summary>
    public const string UnknownPreconditionSuffix = ": " + UnknownValue;

    /// <summary>The warning the default reflector adds to every verified run: it restates the scope of any verification.</summary>
    public const string VerificationScopeWarning =
        "Verification applies only to this run and its captured environment; reuse in another context is not itself verified.";

    /// <summary>
    /// Whether <paramref name="reflection"/> declares the default reflector as its producer and deterministic
    /// authorship: the only reflections whose wording the constants above describe.
    /// </summary>
    public static bool IsDefaultReflector(Reflection? reflection) =>
        reflection is { Authorship: ReflectionAuthorship.Deterministic, Producer: { } producer }
        && producer.StartsWith(ProducerPrefix, StringComparison.Ordinal);
}
