namespace AgentExperience.Abstractions;

/// <summary>
/// What the one-call setup returns, so a storage package can add its own <c>Use…</c> method to it: for example
/// <c>services.AddAgentExperience(…).UseInMemoryStorageForDevelopment()</c> or <c>.UsePostgres(connectionString)</c>.
/// </summary>
/// <remarks>
/// <para>
/// A host never implements this. <c>AddAgentExperience</c> in <c>AgentExperience.MicrosoftAgentFramework</c> returns one
/// whose <typeparamref name="TServices"/> is <c>IServiceCollection</c>, and a storage package extends that closed type.
/// </para>
/// <para>
/// The service collection's type is a type parameter only so this package keeps no dependency beyond the BCL, and the
/// storage packages keep depending on these ports alone rather than on Core or on the MAF adapter.
/// </para>
/// </remarks>
/// <typeparam name="TServices">The host's service collection type: <c>IServiceCollection</c>.</typeparam>
public interface IAgentExperienceBuilder<out TServices>
{
    /// <summary>The service collection the setup registers into.</summary>
    TServices Services { get; }
}
