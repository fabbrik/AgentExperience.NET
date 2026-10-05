using AgentExperience.Abstractions;

// Shared source (story 17.1): the one rule that decides whether a stored reflection counts as model-authored.
// Core (finalization screening, the retrieval service's exclusion re-check), the MAF adapter (injection labelling,
// fencing and exclusion), and both stores (the in-memory candidate filter and the PostgreSQL authorship flag) link
// this file, so every place that decides authorship agrees. Migration 0022's payload_reflection_model_authored
// states the same rule again in SQL for plaintext rows. Each project defines exactly one of the constants below,
// which puts the type in that project's own namespace.
#if AGENTEXPERIENCE_CORE
namespace AgentExperience.Core.Reflections;
#elif AGENTEXPERIENCE_MAF
namespace AgentExperience.MicrosoftAgentFramework.Reflections;
#elif AGENTEXPERIENCE_POSTGRES
namespace AgentExperience.Storage.Postgres;
#elif AGENTEXPERIENCE_INMEMORY
namespace AgentExperience.Storage.InMemory;
#else
#error Define AGENTEXPERIENCE_CORE, AGENTEXPERIENCE_MAF, AGENTEXPERIENCE_POSTGRES or AGENTEXPERIENCE_INMEMORY to select the namespace of the shared authorship rule.
#endif

/// <summary>
/// Whether a reflection's free text counts as model-authored. It does when its
/// <see cref="Reflection.Authorship"/> is anything but <see cref="ReflectionAuthorship.Deterministic"/> (so a
/// tampered or future value counts), or when its <see cref="Reflection.Producer"/> starts with
/// <see cref="LibraryModelReflectorProducerPrefix"/>: the library's own model-backed reflector wrote it, including a
/// record it wrote before it marked its reflections <see cref="ReflectionAuthorship.Model"/> (story 14.3). Only the
/// library's own prefix is recognised; a third-party reflector's authorship is what it declared.
/// </summary>
internal static class ReflectionAuthorshipRule
{
    /// <summary>
    /// The start of every <see cref="Reflection.Producer"/> the MAF adapter's <c>ChatClientExperienceReflector</c>
    /// has written, in every version. Compared ordinally. Migration 0022 repeats this literal in SQL.
    /// </summary>
    public const string LibraryModelReflectorProducerPrefix = ReflectionAuthorshipConventions.LibraryModelReflectorProducerPrefix;

    /// <summary>Whether <paramref name="reflection"/> counts as model-authored; <see langword="false"/> for none.</summary>
    public static bool IsModelAuthored(Reflection? reflection) =>
        reflection is not null
        && (reflection.Authorship != ReflectionAuthorship.Deterministic
            || (reflection.Producer is { } producer
                && producer.StartsWith(LibraryModelReflectorProducerPrefix, StringComparison.Ordinal)));
}
